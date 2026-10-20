using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace CodexQuota
{
    public sealed class UsageWindow
    {
        public double UsedPercent { get; set; }
        public double RemainingPercent { get { return Math.Max(0, 100 - UsedPercent); } }
        public int WindowMinutes { get; set; }
        public DateTimeOffset? ResetsAt { get; set; }
        public bool ResetEstimated { get; set; }
        public DateTimeOffset RecordedAt { get; set; }
        public string SourceFile { get; set; }
        public string Plan { get; set; }
        public bool Expired(DateTimeOffset now) { return ResetsAt.HasValue && ResetsAt.Value <= now; }
    }

    public sealed class UsageSnapshot
    {
        public UsageWindow Primary { get; set; }
        public UsageWindow Secondary { get; set; }
        public DateTimeOffset LoadedAt { get; set; }
        public string SessionRoot { get; set; }
        public string Error { get; set; }
        public int FileCount { get; set; }
        public int ReadCount { get; set; }
        public int UnreadableFiles { get; set; }
        public int LimitedFiles { get; set; }
        public bool HasData { get { return Primary != null || Secondary != null; } }
        public DateTimeOffset? RecordedAt
        {
            get
            {
                if (Primary == null) return Secondary == null ? (DateTimeOffset?)null : Secondary.RecordedAt;
                return Secondary == null || Primary.RecordedAt >= Secondary.RecordedAt ? Primary.RecordedAt : Secondary.RecordedAt;
            }
        }
        public string Plan
        {
            get
            {
                UsageWindow w = Primary == null ? Secondary : Secondary == null || Primary.RecordedAt >= Secondary.RecordedAt ? Primary : Secondary;
                return w == null || String.IsNullOrWhiteSpace(w.Plan) ? "Codex" : w.Plan;
            }
        }
    }

    // Session logs are an implementation detail, not a public stable API.
    // Never read auth.json, conversation bodies, or contact a remote service.
    public sealed class UsageReader
    {
        private sealed class Cached
        {
            public long Length, Stamp;
            public UsageWindow Primary, Secondary;
            public bool Limited;
        }
        private readonly Dictionary<string, Cached> cache = new Dictionary<string, Cached>(StringComparer.OrdinalIgnoreCase);
        private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024, RecursionLimit = 64 };
        public const int BlockSize = 256 * 1024;
        public const int MaxScanBytes = 64 * 1024 * 1024;
        private const int MaxLineBytes = 4 * 1024 * 1024;

        public static string DefaultSessionRoot()
        {
            string home = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (String.IsNullOrWhiteSpace(home)) home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            return Path.Combine(home, "sessions");
        }

        public UsageSnapshot Load(string root, bool force)
        {
            UsageSnapshot result = new UsageSnapshot { SessionRoot = root, LoadedAt = DateTimeOffset.Now };
            if (force) cache.Clear();
            if (!Directory.Exists(root)) { result.Error = "没有找到会话目录，请在设置中选择 Codex 的 sessions 文件夹。"; return result; }
            List<FileInfo> files = new List<FileInfo>();
            Enumerate(new DirectoryInfo(root), files, result);
            result.FileCount = files.Count;
            HashSet<string> alive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (FileInfo file in files.OrderByDescending(f => f.LastWriteTimeUtc).ThenBy(f => f.FullName, StringComparer.OrdinalIgnoreCase))
            {
                alive.Add(file.FullName);
                try
                {
                    Cached entry;
                    long size = file.Length, stamp = file.LastWriteTimeUtc.Ticks;
                    if (!cache.TryGetValue(file.FullName, out entry) || entry.Length != size || entry.Stamp != stamp)
                    {
                        entry = ReadFile(file.FullName, result.LoadedAt);
                        entry.Length = size; entry.Stamp = stamp;
                        cache[file.FullName] = entry;
                        result.ReadCount++;
                    }
                    if (entry.Limited) result.LimitedFiles++;
                    result.Primary = Newest(result.Primary, entry.Primary);
                    result.Secondary = Newest(result.Secondary, entry.Secondary);
                }
                catch (IOException) { cache.Remove(file.FullName); result.UnreadableFiles++; }
                catch (UnauthorizedAccessException) { cache.Remove(file.FullName); result.UnreadableFiles++; }
            }
            foreach (string path in cache.Keys.Where(k => !alive.Contains(k)).ToArray()) cache.Remove(path);
            if (!result.HasData)
                result.Error = result.UnreadableFiles > 0 ? "部分目录或日志无法读取；请检查权限或选择其他会话目录。" : files.Count == 0 ? "目录中没有 .jsonl 会话日志。使用一次 Codex 后再刷新。" : "未发现可用的额度快照。请在 Codex 中完成一次请求后刷新；部分登录方式不提供此数据。";
            return result;
        }

        private static void Enumerate(DirectoryInfo dir, List<FileInfo> files, UsageSnapshot result)
        {
            try
            {
                files.AddRange(dir.GetFiles("*.jsonl"));
                foreach (DirectoryInfo child in dir.GetDirectories())
                    if ((child.Attributes & FileAttributes.ReparsePoint) == 0) Enumerate(child, files, result);
            }
            catch (IOException) { result.UnreadableFiles++; }
            catch (UnauthorizedAccessException) { result.UnreadableFiles++; }
        }

        private static UsageWindow Newest(UsageWindow a, UsageWindow b)
        {
            if (a == null) return b;
            return b != null && b.RecordedAt > a.RecordedAt ? b : a;
        }

        private Cached ReadFile(string path, DateTimeOffset now)
        {
            Cached entry = new Cached();
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long end = stream.Length, consumed = 0;
                byte[] carry = new byte[0];
                bool skipOversize = false;
                // Reverse fixed-size byte blocks; decode only complete lines. UTF-8 splits
                // and partially appended events cannot invalidate an entire file.
                while (end > 0 && consumed < MaxScanBytes)
                {
                    int count = (int)Math.Min(BlockSize, Math.Min(end, MaxScanBytes - consumed));
                    long start = end - count;
                    byte[] block = new byte[count];
                    stream.Position = start;
                    int read = 0, n;
                    while (read < count && (n = stream.Read(block, read, count - read)) > 0) read += n;
                    if (read != count) break; // A concurrent truncate: retry next poll.
                    byte[] data = new byte[count + carry.Length];
                    Buffer.BlockCopy(block, 0, data, 0, count);
                    Buffer.BlockCopy(carry, 0, data, count, carry.Length);
                    int lineEnd = data.Length;
                    for (int i = count - 1; i >= 0; i--)
                    {
                        if (data[i] != 10) continue;
                        if (skipOversize) skipOversize = false;
                        else ParseLine(data, i + 1, lineEnd - i - 1, path, now, entry);
                        lineEnd = i;
                    }
                    if (start == 0)
                    {
                        if (!skipOversize) ParseLine(data, 0, lineEnd, path, now, entry);
                        carry = new byte[0];
                    }
                    else if (lineEnd > MaxLineBytes) { carry = new byte[0]; skipOversize = true; }
                    else { carry = new byte[lineEnd]; Buffer.BlockCopy(data, 0, carry, 0, lineEnd); }
                    consumed += count; end = start;
                    if (entry.Primary != null && entry.Secondary != null) break;
                }
                entry.Limited = end > 0 && consumed >= MaxScanBytes && (entry.Primary == null || entry.Secondary == null);
            }
            return entry;
        }

        private void ParseLine(byte[] data, int offset, int length, string path, DateTimeOffset now, Cached entry)
        {
            if (length < 20 || length > MaxLineBytes) return;
            string line = Encoding.UTF8.GetString(data, offset, length).TrimStart('\uFEFF');
            if (line.IndexOf("\"rate_limits\"", StringComparison.Ordinal) < 0) return;
            try
            {
                Dictionary<string, object> root = json.DeserializeObject(line) as Dictionary<string, object>;
                if (Text(root, "type") != "event_msg") return;
                Dictionary<string, object> payload = Object(root, "payload");
                if (Text(payload, "type") != "token_count") return;
                Dictionary<string, object> limits = Object(payload, "rate_limits");
                if (limits == null) return;
                string id = Text(limits, "limit_id");
                if (!String.IsNullOrEmpty(id) && !String.Equals(id, "codex", StringComparison.OrdinalIgnoreCase)) return;
                DateTimeOffset time;
                if (!DateTimeOffset.TryParse(Text(root, "timestamp"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out time) || time > now.AddMinutes(5)) return;
                entry.Primary = Newest(entry.Primary, ParseWindow(Object(limits, "primary"), time, path, Text(limits, "plan_type")));
                entry.Secondary = Newest(entry.Secondary, ParseWindow(Object(limits, "secondary"), time, path, Text(limits, "plan_type")));
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (FormatException) { }
            catch (OverflowException) { }
        }

        private static UsageWindow ParseWindow(Dictionary<string, object> raw, DateTimeOffset recorded, string path, string plan)
        {
            double? used = Number(raw, "used_percent");
            if (!used.HasValue) return null;
            double minutes = Number(raw, "window_minutes") ?? 0;
            UsageWindow w = new UsageWindow { UsedPercent = Math.Max(0, Math.Min(100, used.Value)), WindowMinutes = minutes > 0 && minutes <= 525600 ? (int)minutes : 0, RecordedAt = recorded, SourceFile = path, Plan = plan };
            double? epoch = Number(raw, "resets_at");
            if (epoch.HasValue && epoch > 0 && epoch <= 253402300799d)
                w.ResetsAt = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(epoch.Value);
            else if (w.WindowMinutes > 0)
            {
                w.ResetsAt = recorded.AddMinutes(w.WindowMinutes); w.ResetEstimated = true;
            }
            return w;
        }

        private static Dictionary<string, object> Object(Dictionary<string, object> d, string k)
        { object v; return d != null && d.TryGetValue(k, out v) ? v as Dictionary<string, object> : null; }
        private static string Text(Dictionary<string, object> d, string k)
        { object v; return d != null && d.TryGetValue(k, out v) ? v as string : null; }
        private static double? Number(Dictionary<string, object> d, string k)
        {
            object v;
            if (d == null || !d.TryGetValue(k, out v) || v == null || v is bool || v is string) return null;
            if (!(v is int || v is long || v is double || v is decimal || v is float)) return null;
            double n = Convert.ToDouble(v, CultureInfo.InvariantCulture);
            return Double.IsNaN(n) || Double.IsInfinity(n) ? null : (double?)n;
        }
    }
}
