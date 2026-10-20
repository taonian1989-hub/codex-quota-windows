using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace CodexQuota
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                string sessions = Value(args, "--sessions") ?? Settings.Load().ResolvedRoot;
                string output = Value(args, "--output");
                if (Has(args, "--inspect"))
                {
                    UsageSnapshot s = new UsageReader().Load(sessions, true);
                    string report = Report(s);
                    if (output != null) Write(output, report); else Console.WriteLine(report);
                    return s.HasData ? 0 : 2;
                }
                if (Has(args, "--render"))
                {
                    if (output == null) throw new ArgumentException("--render 需要 --output 指定预览目录。");
                    UsageSnapshot s = Has(args, "--demo") ? Preview.Demo() : new UsageReader().Load(sessions, true);
                    Preview.Render(output, s); return 0;
                }
                if (Has(args, "--smoke"))
                {
                    using (TrayContext context = new TrayContext(false, sessions))
                    using (System.Windows.Forms.Timer stop = new System.Windows.Forms.Timer { Interval = 5000 })
                    {
                        stop.Tick += delegate
                        {
                            if (output != null) Write(output, Report(context.Snapshot));
                            context.Shutdown();
                        };
                        stop.Start(); Application.Run(context);
                        return context.Snapshot != null && context.Snapshot.HasData ? 0 : 2;
                    }
                }
                bool created;
                using (Mutex mutex = new Mutex(true, @"Local\CodexQuota-80609D7F-84DE-4418-8961-973275747531", out created))
                using (EventWaitHandle reveal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\CodexQuota-Show-80609D7F"))
                {
                    if (!created) { if (!Has(args, "--background")) reveal.Set(); return 0; }
                    try
                    {
                        using (TrayContext context = new TrayContext(!Has(args, "--background"), Value(args, "--sessions")))
                        {
                            RegisteredWaitHandle wait = ThreadPool.RegisterWaitForSingleObject(reveal, delegate { context.Reveal(); }, null, Timeout.Infinite, false);
                            try { Application.Run(context); }
                            finally { wait.Unregister(null); }
                        }
                    }
                    finally { mutex.ReleaseMutex(); }
                }
                return 0;
            }
            catch (Exception ex)
            {
                if (Has(args, "--inspect") || Has(args, "--render") || Has(args, "--smoke"))
                {
                    string output = Value(args, "--output");
                    if (output != null && !Has(args, "--render")) Write(output, new JavaScriptSerializer().Serialize(new { error = ex.Message }));
                    Console.Error.WriteLine(ex.Message);
                }
                else MessageBox.Show("无法启动 Codex Quota：" + ex.Message, "Codex Quota", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
        private static bool Has(string[] args, string key) { return Array.IndexOf(args, key) >= 0; }
        private static string Value(string[] args, string key)
        {
            int i = Array.IndexOf(args, key);
            if (i < 0) return null;
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--")) throw new ArgumentException(key + " 缺少参数。");
            return args[i + 1];
        }
        private static void Write(string path, string contents)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            Directory.CreateDirectory(dir); File.WriteAllText(path, contents, new UTF8Encoding(false));
        }
        internal static string Report(UsageSnapshot s)
        {
            if (s == null) return "{\"error\":\"not_loaded\"}";
            return new JavaScriptSerializer().Serialize(new
            {
                version = "1.0.1", sessions = s.SessionRoot, loaded_at = s.LoadedAt.ToString("o"),
                recorded_at = s.RecordedAt.HasValue ? s.RecordedAt.Value.ToString("o") : null,
                has_data = s.HasData, error = s.Error, files = s.FileCount, read_files = s.ReadCount,
                unreadable_files = s.UnreadableFiles, scan_limited_files = s.LimitedFiles,
                primary = WindowReport(s.Primary), secondary = WindowReport(s.Secondary)
            });
        }
        private static object WindowReport(UsageWindow w)
        {
            return w == null ? null : new
            {
                remaining_percent = w.RemainingPercent, used_percent = w.UsedPercent, window_minutes = w.WindowMinutes,
                resets_at = w.ResetsAt.HasValue ? w.ResetsAt.Value.ToString("o") : null,
                reset_estimated = w.ResetEstimated, reset_time_passed = w.Expired(DateTimeOffset.Now),
                recorded_at = w.RecordedAt.ToString("o"), source = w.SourceFile, plan = w.Plan
            };
        }
    }

    internal sealed class TrayContext : ApplicationContext
    {
        private readonly Settings settings;
        private readonly UsageReader reader = new UsageReader();
        private readonly QuotaForm panel;
        private readonly NotifyIcon ring, number;
        private readonly ContextMenuStrip menu;
        private readonly System.Windows.Forms.Timer timer;
        private Icon ringIcon, numberIcon;
        private bool busy, pendingForce, closing;
        public UsageSnapshot Snapshot { get; private set; }
        public TrayContext(bool show, string rootOverride)
        {
            settings = Settings.Load();
            if (rootOverride != null) settings.SessionRoot = rootOverride;
            panel = new QuotaForm(settings);
            IntPtr handle = panel.Handle;
            menu = new ContextMenuStrip();
            menu.Font = new Font("Microsoft YaHei UI", 9);
            menu.Items.Add("查看额度", null, delegate { panel.ShowNearTray(); });
            menu.Items.Add("立即刷新", null, delegate { Refresh(true); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("打开会话目录", null, delegate { OpenSessions(); });
            menu.Items.Add("设置…", null, delegate { Configure(); });
            menu.Items.Add("写入诊断日志", null, delegate
            {
                try { string path = Diagnostics.Export(Snapshot); panel.Notice = "诊断已保存至 " + Path.GetFileName(path); panel.UpdateView(); OpenPath(Settings.DataDirectory); }
                catch (Exception ex) { ShowError("无法写入诊断日志", ex); }
            });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出 Codex Quota", null, delegate { Shutdown(); });
            ring = new NotifyIcon { ContextMenuStrip = menu, Text = "Codex Quota · 正在读取", Visible = false };
            number = new NotifyIcon { ContextMenuStrip = menu, Text = "Codex Quota · 剩余百分比", Visible = false };
            ring.MouseClick += OnTrayClick; number.MouseClick += OnTrayClick;
            panel.RefreshRequested += delegate { Refresh(true); };
            panel.SettingsRequested += Configure;
            panel.FolderRequested += OpenSessions;
            UpdateTray();
            timer = new System.Windows.Forms.Timer { Interval = settings.RefreshSeconds * 1000 };
            timer.Tick += delegate { Refresh(false); }; timer.Start();
            if (show) panel.ShowNearTray();
            Refresh(true);
        }
        private void OnTrayClick(object sender, MouseEventArgs e)
        { if (e.Button == MouseButtons.Left) { if (panel.Visible) panel.Hide(); else panel.ShowNearTray(); } }
        public void Reveal()
        {
            if (closing || panel.IsDisposed) return;
            try { panel.BeginInvoke((Action)delegate { if (!closing) panel.ShowNearTray(); }); }
            catch (InvalidOperationException) { }
        }
        private async void Refresh(bool force)
        {
            if (closing) return;
            if (busy) { pendingForce |= force; return; }
            busy = true; panel.Busy = true; panel.UpdateView();
            string root = settings.ResolvedRoot;
            try
            {
                UsageSnapshot result = await Task.Run(() => reader.Load(root, force));
                if (closing) return;
                if (root != settings.ResolvedRoot) { pendingForce = true; return; }
                Snapshot = result; panel.Snapshot = result;
                panel.Notice = null; UpdateTray();
            }
            catch (Exception)
            {
                if (!closing) panel.Notice = "本次读取失败，稍后重试或检查目录权限。";
            }
            finally
            {
                busy = false;
                if (!closing)
                {
                    panel.Busy = false; panel.UpdateView();
                    if (pendingForce) { pendingForce = false; Refresh(true); }
                }
            }
        }
        private void UpdateTray()
        {
            Icon nextRing = Appearance.MakeIcon(Snapshot, false, settings.LightTheme, 32);
            Icon nextNumber = Appearance.MakeIcon(Snapshot, true, settings.LightTheme, 32);
            ring.Icon = nextRing; number.Icon = nextNumber;
            if (ringIcon != null) ringIcon.Dispose(); if (numberIcon != null) numberIcon.Dispose();
            ringIcon = nextRing; numberIcon = nextNumber;
            string text = "Codex · 5h " + Remaining(Snapshot == null ? null : Snapshot.Primary) + " · 周 " + Remaining(Snapshot == null ? null : Snapshot.Secondary);
            if (Snapshot != null && Snapshot.RecordedAt.HasValue) text += " · " + Appearance.Age(Snapshot.RecordedAt.Value);
            text = text.Length > 63 ? text.Substring(0, 63) : text;
            ring.Text = text; number.Text = text;
            ring.Visible = true; number.Visible = settings.ShowNumberIcon;
        }
        private static string Remaining(UsageWindow w)
        { return w == null ? "未知" : w.Expired(DateTimeOffset.Now) ? "待更新" : Appearance.Percent(w.RemainingPercent); }
        private void Configure()
        {
            using (SettingsForm dialog = new SettingsForm(settings))
            {
                if (dialog.ShowDialog(panel) == DialogResult.OK)
                { timer.Interval = settings.RefreshSeconds * 1000; panel.UpdateView(); UpdateTray(); Refresh(true); }
            }
        }
        private void OpenSessions()
        {
            if (!Directory.Exists(settings.ResolvedRoot)) { Configure(); return; }
            try { OpenPath(settings.ResolvedRoot); }
            catch (Exception ex) { ShowError("无法打开目录", ex); }
        }
        private static void OpenPath(string path) { Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true }); }
        private void ShowError(string title, Exception ex) { MessageBox.Show(panel, title + "：" + ex.Message, "Codex Quota", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        public void Shutdown()
        {
            if (closing) return;
            closing = true; timer.Stop(); ring.Visible = false; number.Visible = false;
            panel.AllowClose = true; panel.Close(); ExitThread();
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                closing = true; timer.Dispose(); ring.Dispose(); number.Dispose(); menu.Dispose();
                if (ringIcon != null) ringIcon.Dispose(); if (numberIcon != null) numberIcon.Dispose();
                panel.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    public static class Preview
    {
        public static UsageSnapshot Demo()
        {
            DateTimeOffset now = DateTimeOffset.Now;
            return new UsageSnapshot
            {
                Primary = new UsageWindow { UsedPercent = 24, WindowMinutes = 300, RecordedAt = now.AddMinutes(-3), ResetsAt = now.AddHours(3).AddMinutes(24), Plan = "plus" },
                Secondary = new UsageWindow { UsedPercent = 38, WindowMinutes = 10080, RecordedAt = now.AddMinutes(-3), ResetsAt = now.AddDays(4).AddHours(7), Plan = "plus" },
                LoadedAt = now, FileCount = 2, SessionRoot = "DEMO · 示例数据"
            };
        }
        public static void Render(string directory, UsageSnapshot snapshot)
        {
            Directory.CreateDirectory(directory);
            RenderSettings(directory);
            MakeApplicationIcon(Path.Combine(directory, "app.ico"));
            RenderPanel(Path.Combine(directory, "panel-dark.png"), snapshot, false);
            RenderPanel(Path.Combine(directory, "panel-light.png"), snapshot, true);
            RenderPanel(Path.Combine(directory, "panel-empty.png"), new UsageSnapshot { Error = "没有找到额度记录。请在 Codex 中完成一次请求后刷新。" }, false);
            UsageSnapshot expired = Demo();
            expired.Primary.RecordedAt = DateTimeOffset.Now.AddHours(-8); expired.Primary.ResetsAt = DateTimeOffset.Now.AddHours(-3);
            expired.Secondary.RecordedAt = DateTimeOffset.Now.AddHours(-8);
            RenderPanel(Path.Combine(directory, "panel-stale.png"), expired, false);
            using (Bitmap icons = new Bitmap(640, 184))
            using (Graphics g = Graphics.FromImage(icons))
            {
                g.Clear(Color.FromArgb(18, 23, 29));
                double[] levels = { 0, 10, 30, 40, 50, 60, 80, 100 };
                Appearance.Text(g, "外圈：5 小时余量    /    内圈：每周余量", 18, 12, 13, Color.White, false);
                for (int i = 0; i < levels.Length; i++)
                {
                    UsageSnapshot s = Demo(); s.Primary.UsedPercent = 100 - levels[i]; s.Secondary.UsedPercent = 100 - levels[i];
                    using (Icon icon = Appearance.MakeIcon(s, false, false, 48)) g.DrawIcon(icon, 18 + i * 78, 45);
                    Appearance.Text(g, Appearance.Percent(levels[i]), 20 + i * 78, 101, 12, Color.White, false);
                    using (Icon icon = Appearance.MakeIcon(s, true, false, 32)) g.DrawIcon(icon, 26 + i * 78, 132);
                }
                icons.Save(Path.Combine(directory, "icons.png"), ImageFormat.Png);
            }
        }
        private static void RenderSettings(string directory)
        {
            foreach (int percent in new int[] { 100, 125, 150, 200 })
            {
                using (SettingsForm form = new SettingsForm(new Settings { SessionRoot = @"C:\Codex\sessions" }, percent / 100f))
                {
                    form.StartPosition = FormStartPosition.Manual;
                    form.Location = new Point(-32000, -32000); form.Show(); Application.DoEvents();
                    using (Bitmap image = new Bitmap(form.Width, form.Height))
                    {
                        form.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
                        image.Save(Path.Combine(directory, "settings-" + percent + ".png"), ImageFormat.Png);
                    }
                    form.Close();
                }
            }
        }
        private static void RenderPanel(string path, UsageSnapshot snapshot, bool light)
        {
            Settings settings = new Settings { LightTheme = light };
            using (QuotaForm form = new QuotaForm(settings))
            {
                form.Snapshot = snapshot; form.UpdateView();
                form.TopMost = false; form.Location = new Point(-32000, -32000); form.Show();
                Application.DoEvents();
                using (Bitmap image = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size)); image.Save(path, ImageFormat.Png); }
                form.AllowClose = true; form.Close();
            }
        }
        private static void MakeApplicationIcon(string path)
        {
            int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };
            List<byte[]> images = new List<byte[]>();
            UsageSnapshot demo = Demo();
            foreach (int size in sizes)
            {
                using (Bitmap image = new Bitmap(size, size, PixelFormat.Format32bppArgb))
                using (Graphics g = Graphics.FromImage(image))
                using (MemoryStream buffer = new MemoryStream())
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    Appearance.FillRound(g, Color.FromArgb(18, 23, 29), new RectangleF(0, 0, size, size), size * 0.22f);
                    Appearance.DrawRing(g, new RectangleF(size * 0.12f, size * 0.12f, size * 0.76f, size * 0.76f), demo.Primary, demo.Secondary);
                    image.Save(buffer, ImageFormat.Png); images.Add(buffer.ToArray());
                }
            }
            using (BinaryWriter writer = new BinaryWriter(File.Create(path)))
            {
                writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i])); writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
                    writer.Write((byte)0); writer.Write((byte)0); writer.Write((ushort)1); writer.Write((ushort)32);
                    writer.Write(images[i].Length); writer.Write(offset); offset += images[i].Length;
                }
                foreach (byte[] bytes in images) writer.Write(bytes);
            }
        }
    }
}
