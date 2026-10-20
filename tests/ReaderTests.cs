using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using CodexQuota;
using Appearance = CodexQuota.Appearance;

internal static class ReaderTests
{
    private static string root;
    private static int passed, failed;
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow.AddSeconds(-5);
    [DllImport("user32.dll")] private static extern int GetGuiResources(IntPtr process, int flag);

    [STAThread]
    private static int Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Run("missing directory is explicit", delegate { Check(!new UsageReader().Load(Path.Combine(root, "absent"), true).HasData); });
            Run("empty directory is explicit", delegate { Check(Read(Folder()).Error.Contains(".jsonl")); });
            Run("remaining = 100 - used, zero remains valid", delegate
            {
                string d = Folder(); Put(d, "a", Event(Now, W(0), W(100)));
                UsageSnapshot s = Read(d); Equal(s.Primary.RemainingPercent, 100); Equal(s.Secondary.RemainingPercent, 0);
            });
            Run("event timestamps outrank file modification time", delegate
            {
                string d = Folder(); string old = Put(d, "old", Event(Now.AddHours(-3), W(90), W(80)));
                string recent = Put(d, "new", Event(Now, W(12), W(27)));
                File.SetLastWriteTimeUtc(recent, DateTime.UtcNow.AddHours(-2)); File.SetLastWriteTimeUtc(old, DateTime.UtcNow);
                Equal(Read(d).Primary.RemainingPercent, 88);
            });
            Run("timestamps are normalized across timezones", delegate
            {
                string d = Folder(); Put(d, "a", Event(Now.ToOffset(TimeSpan.FromHours(8)), W(15), W(23)));
                Put(d, "b", Event(Now.AddMinutes(-1), W(80), W(80))); Equal(Read(d).Primary.RemainingPercent, 85);
            });
            Run("null windows independently fall back", delegate
            {
                string d = Folder(); Put(d, "a", Event(Now.AddMinutes(-1), W(10), W(20)) + "\n" + Event(Now, null, W(35)));
                UsageSnapshot s = Read(d); Equal(s.Primary.RemainingPercent, 90); Equal(s.Secondary.RemainingPercent, 65);
                Check(s.Primary.RecordedAt < s.Secondary.RecordedAt);
            });
            Run("missing window is unknown rather than zero", delegate
            {
                string d = Folder(); Put(d, "a", Event(Now, W(0), null)); UsageSnapshot s = Read(d);
                Check(s.Primary != null && s.Secondary == null && s.HasData);
            });
            Run("malformed JSON and partial append are skipped", delegate
            {
                string d = Folder(); Put(d, "a", "not json\n" + Event(Now, W(14), W(17)) + "\n{\"payload\":{\"rate_limits\":");
                Equal(Read(d).Primary.RemainingPercent, 86);
            });
            Run("message text and non-token events are ignored", delegate
            {
                string d = Folder(); Put(d, "a", Event(Now, W(99), W(99)).Replace("event_msg", "response_item") + "\n" + Event(Now, W(1), W(1)).Replace("token_count", "agent_message"));
                Check(!Read(d).HasData);
            });
            Run("model-specific bucket cannot overwrite Codex pool", delegate
            {
                string d = Folder(); Put(d, "a", Event(Now.AddMinutes(-1), W(5), W(6)) + "\n" + Event(Now, W(99), W(99)).Replace("\"codex\"", "\"codex_other\""));
                Equal(Read(d).Primary.RemainingPercent, 95);
            });
            Run("missing limit_id remains backward compatible", delegate
            {
                string d = Folder(); Put(d, "a", Event(Now, W(11), W(12)).Replace("\"limit_id\":\"codex\",", "")); Equal(Read(d).Primary.RemainingPercent, 89);
            });
            Run("invalid timestamps never fall back to mtime", delegate
            {
                string d = Folder(); Put(d, "a", Event(Now, W(15), W(15)).Replace(Now.ToString("o"), "bad-time")); Check(!Read(d).HasData);
            });
            Run("implausible future snapshots are ignored", delegate
            { string d = Folder(); Put(d, "a", Event(Now.AddDays(1), W(15), W(15))); Check(!Read(d).HasData); });
            Run("numeric strings and booleans are not percentages", delegate
            { string d = Folder(); Put(d, "a", Event(Now, new { used_percent = "25" }, new { used_percent = true })); Check(!Read(d).HasData); });
            Run("numeric percentages are bounded", delegate
            { string d = Folder(); Put(d, "a", Event(Now, W(-4), W(110))); UsageSnapshot s = Read(d); Equal(s.Primary.RemainingPercent, 100); Equal(s.Secondary.RemainingPercent, 0); });
            Run("reset epoch is parsed in UTC", delegate
            {
                string d = Folder(); Put(d, "a", Event(Now, W(20), W(25))); UsageSnapshot s = Read(d);
                Check(Math.Abs((s.Primary.ResetsAt.Value - Now.AddHours(2)).TotalSeconds) < 1);
            });
            Run("missing reset uses marked estimate", delegate
            {
                string d = Folder(); Put(d, "a", Event(Now, new { used_percent = 12, window_minutes = 300 }, null)); UsageWindow w = Read(d).Primary;
                Check(w.ResetEstimated && Math.Abs((w.ResetsAt.Value - Now.AddMinutes(300)).TotalSeconds) < 1);
            });
            Run("passed reset retains observed quota and warns", delegate
            {
                string d = Folder(); Put(d, "a", Event(Now.AddHours(-8), new { used_percent = 80, resets_at = Epoch(Now.AddHours(-3)) }, null));
                UsageWindow w = Read(d).Primary; Equal(w.RemainingPercent, 20); Check(w.Expired(DateTimeOffset.Now)); Check(Appearance.Countdown(w).Contains("等待"));
            });
            Run("UTF-8 block boundary does not lose records", delegate
            {
                string d = Folder(); string text = new string('汉', UsageReader.BlockSize) + "\n" + Event(Now, W(6), W(8)) + "\n" + new string('月', UsageReader.BlockSize / 3);
                Put(d, "a", text); Equal(Read(d).Primary.RemainingPercent, 94);
            });
            Run("reverse scan reaches older non-null secondary", delegate
            {
                string d = Folder(); Put(d, "a", Event(Now.AddMinutes(-2), W(10), W(44)) + "\n" + new string('x', UsageReader.BlockSize + 111) + "\n" + Event(Now, W(22), null));
                UsageSnapshot s = Read(d); Equal(s.Primary.RemainingPercent, 78); Equal(s.Secondary.RemainingPercent, 56);
            });
            Run("BOM and CRLF are accepted", delegate
            { string d = Folder(); Put(d, "a", "\uFEFF" + Event(Now, W(1), W(2)) + "\r\n"); Equal(Read(d).Primary.RemainingPercent, 99); });
            Run("all files considered beyond upstream twenty-file cap", delegate
            {
                string d = Folder(); string winner = Put(d, "winner", Event(Now, W(3), W(4))); File.SetLastWriteTimeUtc(winner, DateTime.UtcNow.AddDays(-30));
                for (int i = 0; i < 25; i++) Put(d, "empty-" + i, "{}");
                Equal(Read(d).Primary.RemainingPercent, 97);
            });
            Run("unchanged files are cached and force bypasses cache", delegate
            {
                string d = Folder(); Put(d, "a", Event(Now, W(3), W(4))); UsageReader reader = new UsageReader();
                Check(reader.Load(d, false).ReadCount == 1); Check(reader.Load(d, false).ReadCount == 0); Check(reader.Load(d, true).ReadCount == 1);
            });
            Run("every file fingerprint participates in cache", delegate
            {
                string d = Folder(); string a = Put(d, "a", Event(Now.AddMinutes(-5), W(10), W(10)));
                string b = Put(d, "b", Event(Now.AddMinutes(-4), W(20), W(20))); File.SetLastWriteTimeUtc(b, DateTime.UtcNow.AddDays(1));
                UsageReader reader = new UsageReader(); reader.Load(d, false);
                File.AppendAllText(a, "\n" + Event(Now, W(33), W(33))); UsageSnapshot s = reader.Load(d, false);
                Equal(s.Primary.RemainingPercent, 67); Check(s.ReadCount == 1);
            });
            Run("truncated and deleted files cannot retain old cache", delegate
            {
                string d = Folder(); string a = Put(d, "a", Event(Now, W(7), W(9))); UsageReader reader = new UsageReader(); Check(reader.Load(d, false).HasData);
                File.WriteAllText(a, "{}"); Check(!reader.Load(d, false).HasData); File.Delete(a); Check(!reader.Load(d, false).HasData);
            });
            Run("read succeeds while Codex holds append handle", delegate
            {
                string d = Folder(); string a = Put(d, "a", Event(Now, W(2), W(3)));
                using (FileStream stream = new FileStream(a, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite)) Equal(Read(d).Primary.RemainingPercent, 98);
            });
            Run("scan budget is bounded and explicitly reported", delegate
            {
                string d = Folder(); string path = Path.Combine(d, "big.jsonl");
                using (FileStream f = File.Create(path)) f.SetLength(UsageReader.MaxScanBytes + UsageReader.BlockSize);
                UsageSnapshot s = Read(d); Check(!s.HasData && s.LimitedFiles == 1);
            });
            Run("moon boundaries and lit area are monotonic", delegate
            {
                double[] values = { 10, 30, 40, 50, 60, 80, 100 }; int last = 0;
                for (int i = 0; i < values.Length; i++)
                {
                    Check(Appearance.MoonLevel(values[i]) == i);
                    using (Bitmap b = new Bitmap(64, 64))
                    {
                        using (Graphics g = Graphics.FromImage(b)) Appearance.DrawMoon(g, new RectangleF(4, 4, 56, 56), values[i]);
                        int lit = 0;
                        for (int x = 0; x < 64; x++) for (int y = 0; y < 64; y++) if (b.GetPixel(x, y).R > 210) lit++;
                        Check(lit > last); last = lit;
                    }
                }
                Check(Appearance.MoonLevel(20) == 1 && Appearance.MoonLevel(90) == 6);
            });
            Run("4000 icon lifecycles do not leak native handles", delegate
            {
                UsageSnapshot demo = Preview.Demo();
                using (Icon warm = Appearance.MakeIcon(demo, false, false, 32)) { }
                GC.Collect(); GC.WaitForPendingFinalizers();
                Process p = Process.GetCurrentProcess();
                int gdi = GetGuiResources(p.Handle, 0), user = GetGuiResources(p.Handle, 1);
                for (int i = 0; i < 4000; i++) using (Icon icon = Appearance.MakeIcon(demo, i % 2 == 0, false, 32)) { }
                GC.Collect(); GC.WaitForPendingFinalizers();
                int gd = GetGuiResources(p.Handle, 0) - gdi, ud = GetGuiResources(p.Handle, 1) - user;
                Console.WriteLine("  Native handles delta: GDI=" + gd + " USER=" + ud); Check(gd <= 8 && ud <= 8);
            });
            Run("native controls dispatch refresh, settings, folder and hide", delegate
            {
                using (QuotaForm form = new QuotaForm(new Settings()))
                {
                    int refresh = 0, settings = 0, folder = 0;
                    form.RefreshRequested += delegate { refresh++; }; form.SettingsRequested += delegate { settings++; }; form.FolderRequested += delegate { folder++; };
                    form.Location = new Point(-32000, -32000); form.TopMost = false; form.Show(); Application.DoEvents();
                    Check(form.Controls.Count == 4 && !form.ShowInTaskbar);
                    foreach (Button b in form.Controls.OfType<Button>())
                        if (b.Text == "立即刷新" || b.Text == "设置" || b.Text == "日志目录") b.PerformClick();
                    Check(refresh == 1 && settings == 1 && folder == 1);
                    form.Busy = true; form.UpdateView(); Check(!form.Controls.OfType<Button>().First(b => b.Text == "正在读取…").Enabled);
                    form.Controls.OfType<Button>().First(b => b.Text == "×").PerformClick(); Check(!form.Visible);
                    form.AllowClose = true; form.Close();
                }
            });
            foreach (int percent in new int[] { 100, 125, 150, 200 })
            {
                int scale = percent;
                Run("settings layout fits and interval is selectable at " + scale + "%", delegate
                {
                    using (SettingsForm form = new SettingsForm(new Settings(), scale / 100f))
                    {
                        form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-32000, -32000);
                        form.Show(); Application.DoEvents();
                        foreach (Control control in Descendants(form))
                        {
                            if (control is Label || control is Button || control is CheckBox)
                            {
                                Size preferred = control.GetPreferredSize(Size.Empty);
                                if (control.Width + 1 < preferred.Width || control.Height + 1 < preferred.Height)
                                    throw new Exception("Clipped control: " + control.Text + " actual=" + control.Size + " preferred=" + preferred);
                            }
                        }
                        ComboBox combo = (ComboBox)form.Controls.Find("refreshInterval", true)[0];
                        Control label = form.Controls.Find("refreshLabel", true)[0];
                        Check(!combo.Bounds.IntersectsWith(label.Bounds));
                        Check(combo.Parent.GetChildAtPoint(new Point(combo.Left + combo.Width / 2, combo.Top + combo.Height / 2)) == combo);
                        combo.SelectedItem = "60";
                        int seconds; Check(form.TryReadRefreshInterval(out seconds) && seconds == 60);
                        combo.Text = "45"; Check(form.TryReadRefreshInterval(out seconds) && seconds == 45);
                        foreach (string boundary in new string[] { "10", "3600" })
                        { combo.Text = boundary; Check(form.TryReadRefreshInterval(out seconds)); }
                        Button save = (Button)form.Controls.Find("saveSettings", true)[0];
                        Rectangle bounds = form.RectangleToClient(save.RectangleToScreen(save.ClientRectangle));
                        Check(form.ClientRectangle.Contains(bounds));
                        form.Close();
                    }
                });
            }
            Run("invalid interval blocks saving without modifying settings", delegate
            {
                Settings settings = new Settings();
                using (SettingsForm form = new SettingsForm(settings))
                {
                    form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-32000, -32000);
                    form.Show(); Application.DoEvents();
                    ComboBox combo = (ComboBox)form.Controls.Find("refreshInterval", true)[0];
                    int seconds;
                    foreach (string input in new string[] { "", "abc", "9", "3601", "30.5", "-1" })
                    { combo.Text = input; Check(!form.TryReadRefreshInterval(out seconds)); }
                    ((Button)form.Controls.Find("saveSettings", true)[0]).PerformClick();
                    Check(settings.RefreshSeconds == 30 && form.Visible && form.DialogResult != DialogResult.OK);
                    form.Close();
                }
            });
            Run("custom interval and cancel preserve existing settings", delegate
            {
                Settings settings = new Settings { RefreshSeconds = 45 };
                using (SettingsForm form = new SettingsForm(settings))
                {
                    form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-32000, -32000);
                    form.Show(); Application.DoEvents();
                    ComboBox combo = (ComboBox)form.Controls.Find("refreshInterval", true)[0];
                    int seconds; Check(combo.Text == "45" && form.TryReadRefreshInterval(out seconds) && seconds == 45);
                    combo.SelectedItem = "120";
                    ((Button)form.Controls.Find("cancelSettings", true)[0]).PerformClick();
                    Check(settings.RefreshSeconds == 45);
                    form.Close();
                }
            });
            Run("streaming reader handles reordered fields, escapes and skipped nested data", delegate
            {
                string d = Folder();
                string line = "{\"payload\":{\"ignored\":{\"nested\":[1,true,null,{\"text\":\"汉字\\n\\u6708\"}]},\"rate_limits\":{\"secondary\":{\"used_percent\":2.5e1},\"primary\":{\"used_percent\":1.25e1},\"plan_type\":\"pl\\u0075s\",\"limit_id\":\"codex\"},\"type\":\"token_count\"},\"type\":\"event_msg\",\"timestamp\":\"" + Now.ToString("o") + "\"}";
                Put(d, "a", line); UsageSnapshot s = Read(d); Equal(s.Primary.RemainingPercent, 87.5); Equal(s.Secondary.RemainingPercent, 75); Check(s.Plan == "plus");
            });
            Run("streaming reader rejects trailing or incomplete JSON atomically", delegate
            {
                string d = Folder(); string valid = Event(Now, W(1), W(2));
                Put(d, "a", valid + "garbage\n" + valid.Substring(0, valid.Length - 1)); Check(!Read(d).HasData);
            });
            Run("streaming reader honors last duplicate member", delegate
            {
                string d = Folder(); Put(d, "a", Event(Now, W(5), W(6)).Replace("\"used_percent\":5", "\"used_percent\":99,\"used_percent\":5")); Equal(Read(d).Primary.RemainingPercent, 95);
            });
            Run("oversized log lines cannot pin a large resident buffer", delegate
            {
                string d = Folder(); Put(d, "a", Event(Now, W(12), W(13)) + "\n" + new string('x', 6 * 1024 * 1024));
                UsageReader reader = new UsageReader(); Equal(reader.Load(d, true).Primary.RemainingPercent, 88); Check(reader.BufferCapacity <= 2 * UsageReader.BlockSize);
            });
            Run("legacy settings and defaults remain compatible without touching user files", delegate
            {
                string legacy = "{\"SessionRoot\":\"C:\\\\Codex\\\\sessions\",\"RefreshSeconds\":45,\"ShowNumberIcon\":false,\"LightTheme\":true,\"ResolvedRoot\":\"ignored\"}";
                Settings s;
                using (MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(legacy))) s = JsonData.Read<Settings>(stream);
                Check(s.RefreshSeconds == 45 && !s.ShowNumberIcon && s.LightTheme && s.SessionRoot != null);
                using (MemoryStream stream = new MemoryStream(JsonData.Encode(s)))
                { Settings copy = JsonData.Read<Settings>(stream); Check(copy.SessionRoot == s.SessionRoot && copy.RefreshSeconds == 45 && !copy.ShowNumberIcon && copy.LightTheme); }
                using (MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes("{}")))
                { Settings defaults = JsonData.Read<Settings>(stream); Check(defaults.RefreshSeconds == 30 && defaults.ShowNumberIcon); }
            });
            Run("diagnostic JSON retains the existing field names and values", delegate
            {
                UsageSnapshot s = Preview.Demo();
                System.Collections.Generic.Dictionary<string, object> parsed = (System.Collections.Generic.Dictionary<string, object>)Json.DeserializeObject(CodexQuota.Program.Report(s));
                Check((bool)parsed["has_data"] && parsed.ContainsKey("recorded_at") && parsed.ContainsKey("unreadable_files"));
                System.Collections.Generic.Dictionary<string, object> w = (System.Collections.Generic.Dictionary<string, object>)parsed["primary"];
                Equal(Convert.ToDouble(w["remaining_percent"]), 76); Check(w.ContainsKey("reset_estimated") && w.ContainsKey("reset_time_passed"));
                Check(!typeof(UsageReader).Assembly.GetReferencedAssemblies().Any(a => a.Name == "System.Web.Extensions"));
            });
            Run("unchanged tray images are reused and freshness does not grow the cache", delegate
            {
                using (TrayIconCache cache = new TrayIconCache())
                {
                    UsageSnapshot s = Preview.Demo(); cache.Update(s, false, true); Icon ring = cache.Ring, number = cache.Number;
                    for (int i = 0; i < 2000; i++) { s.Primary.RecordedAt = DateTimeOffset.Now; cache.Update(s, false, true); }
                    Check(cache.RenderCount == 2 && Object.ReferenceEquals(ring, cache.Ring) && Object.ReferenceEquals(number, cache.Number));
                    s.Secondary.UsedPercent = 39; cache.Update(s, false, true); Check(cache.RenderCount == 2);
                    s.Secondary.UsedPercent = 20; cache.Update(s, false, true); Check(cache.RenderCount == 3 && !cache.NumberChanged);
                    cache.Update(s, false, false); Check(cache.Number == null && cache.RenderCount == 3);
                    cache.Update(s, false, true); Check(cache.RenderCount == 4);
                    s.Primary.ResetsAt = DateTimeOffset.Now.AddSeconds(-1); cache.Update(s, false, true); Check(cache.RenderCount == 6);
                }
            });
            Run("hidden detail windows release their full-size paint buffer", delegate
            {
                using (QuotaForm form = new QuotaForm(new Settings()))
                {
                    form.Snapshot = Preview.Demo(); form.TopMost = false; form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-32000, -32000);
                    form.Show(); Application.DoEvents(); form.Refresh(); Check(form.BufferedBytes > 0);
                    form.Hide(); Check(form.BufferedBytes == 0);
                    form.Show(); Application.DoEvents(); form.Refresh(); Check(form.BufferedBytes > 0);
                    form.AllowClose = true; form.Close(); Check(form.BufferedBytes == 0);
                }
            });
            Run("background tray starts without constructing the detail window", delegate
            {
                string d = Folder(); Put(d, "a", Event(Now, W(1), W(2)));
                using (TrayContext context = new TrayContext(false, d))
                {
                    Stopwatch watch = Stopwatch.StartNew();
                    while (context.Snapshot == null && watch.ElapsedMilliseconds < 3000) { Application.DoEvents(); System.Threading.Thread.Sleep(1); }
                    Check(context.Snapshot != null && context.Snapshot.HasData && !context.HasPanel);
                    int count = context.IconRenderCount;
                    Application.DoEvents(); Check(context.IconRenderCount == count);
                    context.Shutdown();
                }
            });
            Console.WriteLine("RESULT: " + passed + " passed, " + failed + " failed");
            return failed == 0 ? 0 : 1;
        }
        finally
        {
            string full = Path.GetFullPath(root), parent = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory);
            if (full.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(full).StartsWith("fixtures-")) Directory.Delete(full, true);
        }
    }
    private static System.Collections.Generic.IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (Control nested in Descendants(child)) yield return nested;
        }
    }
    private static void Run(string name, Action test)
    { try { test(); passed++; Console.WriteLine("PASS " + name); } catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + ": " + e.Message); } }
    private static string Folder() { string d = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(d); return d; }
    private static string Put(string d, string name, string content) { string p = Path.Combine(d, name + ".jsonl"); File.WriteAllText(p, content, new UTF8Encoding(false)); return p; }
    private static UsageSnapshot Read(string d) { return new UsageReader().Load(d, true); }
    private static object W(double used) { return new { used_percent = used, window_minutes = 300, resets_at = Epoch(Now.AddHours(2)) }; }
    private static long Epoch(DateTimeOffset d) { return (long)(d - new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero)).TotalSeconds; }
    private static string Event(DateTimeOffset t, object primary, object secondary)
    { return Json.Serialize(new { timestamp = t.ToString("o"), type = "event_msg", payload = new { type = "token_count", rate_limits = new { limit_id = "codex", primary = primary, secondary = secondary, plan_type = "plus" } } }); }
    private static void Check(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
    private static void Equal(double actual, double expected) { if (Math.Abs(actual - expected) > 0.001) throw new Exception("Expected " + expected + ", got " + actual); }
}
