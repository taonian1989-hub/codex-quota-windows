using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;

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
        private byte[] buffer;
        internal int BufferCapacity { get { return buffer == null ? 0 : buffer.Length; } }
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

        private void EnsureBuffer(int size)
        {
            if (buffer != null && buffer.Length >= size) return;
            int capacity = Math.Min(MaxLineBytes + BlockSize, Math.Max(size, buffer == null ? BlockSize : buffer.Length * 2));
            byte[] next = new byte[capacity];
            if (buffer != null) Buffer.BlockCopy(buffer, 0, next, 0, Math.Min(buffer.Length, size));
            buffer = next;
        }
        private Cached ReadFile(string path, DateTimeOffset now)
        {
            Cached entry = new Cached();
            try
            {
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    long end = stream.Length, consumed = 0;
                    int carry = 0;
                    bool skipOversize = false;
                    while (end > 0 && consumed < MaxScanBytes)
                    {
                        int count = (int)Math.Min(BlockSize, Math.Min(end, MaxScanBytes - consumed));
                        long start = end - count;
                        EnsureBuffer(count + carry);
                        if (carry > 0) Buffer.BlockCopy(buffer, 0, buffer, count, carry);
                        stream.Position = start;
                        int read = 0, n;
                        while (read < count && (n = stream.Read(buffer, read, count - read)) > 0) read += n;
                        if (read != count) break;
                        int lineEnd = count + carry;
                        for (int i = count - 1; i >= 0; i--)
                        {
                            if (buffer[i] != 10) continue;
                            if (skipOversize) skipOversize = false;
                            else ParseLine(buffer, i + 1, lineEnd - i - 1, path, now, entry);
                            lineEnd = i;
                        }
                        if (start == 0)
                        {
                            if (!skipOversize) ParseLine(buffer, 0, lineEnd, path, now, entry);
                            carry = 0;
                        }
                        else if (lineEnd > MaxLineBytes) { carry = 0; skipOversize = true; }
                        else carry = lineEnd;
                        consumed += count; end = start;
                        if (entry.Primary != null && entry.Secondary != null) break;
                    }
                    entry.Limited = end > 0 && consumed >= MaxScanBytes && (entry.Primary == null || entry.Secondary == null);
                }
                return entry;
            }
            finally
            {
                // Keep the ordinary shared buffer, but do not pin a multi-MB buffer
                // in the resident reader after an unusually large log line.
                if (buffer != null && buffer.Length > 2 * BlockSize) buffer = null;
            }
        }
        private static bool ContainsRateLimits(byte[] data, int offset, int length)
        {
            // ASCII prefilter before any UTF-8 decoding or JSON allocation.
            int end = offset + length - 13;
            for (int i = offset; i <= end; i++)
                if (data[i] == 34 && data[i + 1] == 114 && data[i + 2] == 97 && data[i + 3] == 116 && data[i + 4] == 101 && data[i + 5] == 95 && data[i + 6] == 108 && data[i + 7] == 105 && data[i + 8] == 109 && data[i + 9] == 105 && data[i + 10] == 116 && data[i + 11] == 115 && data[i + 12] == 34) return true;
            return false;
        }
        private void ParseLine(byte[] data, int offset, int length, string path, DateTimeOffset now, Cached entry)
        {
            if (length < 20 || length > MaxLineBytes || !ContainsRateLimits(data, offset, length)) return;
            try
            {
                QuotaEvent record = QuotaJsonReader.Read(data, offset, length);
                if (record.Kind != "event_msg" || record.PayloadKind != "token_count") return;
                if (!String.IsNullOrEmpty(record.LimitId) && !String.Equals(record.LimitId, "codex", StringComparison.OrdinalIgnoreCase)) return;
                DateTimeOffset time;
                if (!DateTimeOffset.TryParse(record.Stamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out time) || time > now.AddMinutes(5)) return;
                if (entry.Primary == null || time > entry.Primary.RecordedAt)
                    entry.Primary = Newest(entry.Primary, ParseWindow(record.Primary, time, path, record.Plan));
                if (entry.Secondary == null || time > entry.Secondary.RecordedAt)
                    entry.Secondary = Newest(entry.Secondary, ParseWindow(record.Secondary, time, path, record.Plan));
            }
            catch (XmlException) { }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (FormatException) { }
            catch (OverflowException) { }
        }
        private static UsageWindow ParseWindow(QuotaValues raw, DateTimeOffset recorded, string path, string plan)
        {
            if (!raw.Used.HasValue) return null;
            double minutes = raw.Minutes ?? 0;
            UsageWindow w = new UsageWindow { UsedPercent = Math.Max(0, Math.Min(100, raw.Used.Value)), WindowMinutes = minutes > 0 && minutes <= 525600 ? (int)minutes : 0, RecordedAt = recorded, SourceFile = path, Plan = plan };
            double? epoch = raw.Reset;
            if (epoch.HasValue && epoch > 0 && epoch <= 253402300799d)
                w.ResetsAt = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(epoch.Value);
            else if (w.WindowMinutes > 0)
            {
                w.ResetsAt = recorded.AddMinutes(w.WindowMinutes); w.ResetEstimated = true;
            }
            return w;
        }
    }
}
