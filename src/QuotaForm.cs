using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CodexQuota
{
    public sealed class QuotaForm : Form
    {
        public UsageSnapshot Snapshot;
        public bool Busy;
        public bool AllowClose;
        public string Notice;
        private readonly Settings settings;
        private readonly float scale;
        private readonly Button refresh, options, folder;
        public event Action RefreshRequested, SettingsRequested, FolderRequested;
        public Color Surface { get { return settings.LightTheme ? Color.FromArgb(245, 247, 249) : Color.FromArgb(18, 23, 29); } }
        private Color Foreground { get { return settings.LightTheme ? Color.FromArgb(27, 38, 48) : Color.FromArgb(235, 241, 246); } }
        private Color Muted { get { return settings.LightTheme ? Color.FromArgb(93, 108, 121) : Color.FromArgb(139, 155, 169); } }
        private Color Card { get { return settings.LightTheme ? Color.White : Color.FromArgb(27, 34, 42); } }
        public QuotaForm(Settings value)
        {
            settings = value;
            Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
            using (Graphics g = CreateGraphics()) scale = Math.Min(g.DpiX / 96f, Math.Min((area.Width - 32) / 440f, (area.Height - 32) / 700f));
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size((int)(440 * scale), (int)(700 * scale));
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Text = "Codex Quota · 额度监视器"; AccessibleName = Text;
            ShowInTaskbar = false; MaximizeBox = false; MinimizeBox = false;
            TopMost = true; DoubleBuffered = true; KeyPreview = true;
            BackColor = Surface;
            refresh = AddButton("立即刷新", 24, 600, 190, delegate { if (RefreshRequested != null) RefreshRequested(); }, true);
            options = AddButton("设置", 226, 600, 90, delegate { if (SettingsRequested != null) SettingsRequested(); }, false);
            folder = AddButton("日志目录", 326, 600, 90, delegate { if (FolderRequested != null) FolderRequested(); }, false);
            AddButton("×", 387, 16, 29, delegate { Hide(); }, false);
            KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) Hide(); if (e.KeyCode == Keys.F5 && RefreshRequested != null) RefreshRequested(); };
            MouseDown += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left && e.Y < 82 * scale) { ReleaseCapture(); SendMessage(Handle, 0xA1, new IntPtr(2), IntPtr.Zero); } };
        }
        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        protected override CreateParams CreateParams { get { CreateParams p = base.CreateParams; p.ClassStyle |= 0x20000; return p; } }
        private Button AddButton(string text, int x, int y, int w, Action action, bool accent)
        {
            Button b = new Button { Text = text, Location = new Point((int)(x * scale), (int)(y * scale)), Size = new Size((int)(w * scale), (int)((y == 16 ? 29 : 40) * scale)), FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, TabStop = true, AccessibleName = text };
            b.Font = new Font("Microsoft YaHei UI", 12 * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            b.FlatAppearance.BorderSize = 0; b.FlatAppearance.MouseOverBackColor = accent ? Color.FromArgb(123, 232, 191) : Color.FromArgb(60, 74, 86);
            b.BackColor = accent ? Appearance.Green : Card;
            b.ForeColor = accent ? Color.FromArgb(18, 45, 34) : Foreground;
            b.Click += delegate { action(); }; Controls.Add(b); return b;
        }
        public void UpdateView()
        {
            BackColor = Surface;
            foreach (Control c in Controls)
            {
                Button b = c as Button;
                if (b != null && b != refresh) { b.BackColor = Card; b.ForeColor = Foreground; }
            }
            refresh.Text = Busy ? "正在读取…" : "立即刷新"; refresh.Enabled = !Busy;
            AccessibleDescription = Snapshot == null ? "正在读取本地额度" : "5 小时剩余 " + (Snapshot.Primary == null ? "未知" : Appearance.Percent(Snapshot.Primary.RemainingPercent)) + "，每周剩余 " + (Snapshot.Secondary == null ? "未知" : Appearance.Percent(Snapshot.Secondary.RemainingPercent));
            Invalidate();
        }
        protected override void OnFormClosing(FormClosingEventArgs e)
        { if (!AllowClose && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } base.OnFormClosing(e); }
        public void ShowNearTray()
        {
            Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(Math.Max(area.Left, area.Right - Width - 18), Math.Max(area.Top, area.Bottom - Height - 16));
            Show(); Activate();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics; g.ScaleTransform(scale, scale); g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using (Pen border = new Pen(settings.LightTheme ? Color.FromArgb(211, 219, 226) : Color.FromArgb(53, 65, 77))) g.DrawRectangle(border, 0, 0, 439, 699);
            UsageWindow p = Snapshot == null ? null : Snapshot.Primary, s = Snapshot == null ? null : Snapshot.Secondary;
            Appearance.FillRound(g, Card, new RectangleF(24, 22, 42, 42), 12);
            Appearance.DrawRing(g, new RectangleF(31, 29, 28, 28), p, s);
            Appearance.Text(g, "Codex Quota", 78, 24, 20, Foreground, true);
            Appearance.Text(g, "WINDOWS  /  本地额度监视器", 79, 51, 10, Muted, false);
            Appearance.Text(g, "额度，抬眼可见。", 24, 91, 26, Foreground, true);
            Appearance.Text(g, "留一点余量，给下一个好想法。", 25, 131, 12, Muted, false);
            DrawCard(g, p, s, 161, true);
            DrawCard(g, s, s, 329, false);
            Appearance.FillRound(g, Card, new RectangleF(24, 497, 392, 88), 14);
            bool stale = Snapshot != null && Snapshot.RecordedAt.HasValue && DateTimeOffset.Now - Snapshot.RecordedAt.Value > TimeSpan.FromMinutes(10);
            using (Brush b = new SolidBrush(stale ? Appearance.Amber : Snapshot != null && Snapshot.HasData ? Appearance.Green : Muted)) g.FillEllipse(b, 40, 514, 6, 6);
            Appearance.Text(g, "最近读数", 53, 508, 12, Foreground, true);
            string plan = Snapshot == null ? "LOCAL" : Snapshot.Plan.ToUpperInvariant();
            if (plan.Length > 16) plan = "CODEX";
            Appearance.Text(g, plan + "  ·  本地日志", 273, 509, 10, Muted, false);
            if (Snapshot != null && Snapshot.RecordedAt.HasValue)
            {
                DateTimeOffset recorded = Snapshot.RecordedAt.Value;
                Appearance.Text(g, recorded.ToLocalTime().ToString("MM/dd HH:mm:ss") + "   ·   " + Appearance.Age(recorded), 40, 534, 12, Foreground, false);
                Appearance.Text(g, stale ? "读数已陈旧；使用 Codex 后会随日志更新。" : "显示最近一次请求的快照，每 " + settings.RefreshSeconds + " 秒检查日志。", 40, 558, 10, Muted, false);
            }
            else
            {
                string error = Snapshot == null ? "正在读取本地会话日志…" : Snapshot.Error;
                RectangleF r = new RectangleF(40, 533, 360, 44);
                using (Font f = new Font("Microsoft YaHei UI", 11, FontStyle.Regular, GraphicsUnit.Pixel))
                using (Brush b = new SolidBrush(Muted)) g.DrawString(error, f, b, r);
            }
            string notice = Notice;
            if (String.IsNullOrEmpty(notice) && Snapshot != null && Snapshot.UnreadableFiles > 0) notice = "有 " + Snapshot.UnreadableFiles + " 个目录或日志无法读取，结果可能不完整。";
            if (String.IsNullOrEmpty(notice) && Snapshot != null && Snapshot.LimitedFiles > 0) notice = "部分日志超过 64 MB 扫描范围，结果可能不完整。";
            Appearance.Text(g, String.IsNullOrEmpty(notice) ? "只读本机日志  ·  无网络请求  ·  无需 API Key" : notice, 25, 656, 10, Muted, false);
            Appearance.Text(g, "外圈  5 小时     /     月相  每周     ·     v1.0.0", 25, 677, 9, Muted, false);
        }
        private void DrawCard(Graphics g, UsageWindow w, UsageWindow weekly, int y, bool primary)
        {
            Appearance.FillRound(g, Card, new RectangleF(24, y, 392, 156), 14);
            bool stale = w != null && DateTimeOffset.Now - w.RecordedAt > TimeSpan.FromMinutes(10);
            Color color = w == null || w.Expired(DateTimeOffset.Now) ? Muted : stale ? Appearance.Amber : Appearance.Status(w.RemainingPercent);
            Appearance.Text(g, primary ? "5 小时窗口" : "每周窗口", 42, y + 16, 13, Foreground, true);
            string state = w == null ? "等待数据" : w.Expired(DateTimeOffset.Now) ? "待更新" : stale ? "读数陈旧" : w.RemainingPercent > 40 ? "余量充足" : w.RemainingPercent > 15 ? "留意余量" : "额度偏低";
            Appearance.FillRound(g, Color.FromArgb(30, color), new RectangleF(320, y + 14, 77, 24), 12);
            Appearance.Text(g, state, 333, y + 18, 10, color, false);
            Appearance.Text(g, w == null ? "—" : Appearance.Percent(w.RemainingPercent), 40, y + 42, 40, Foreground, true);
            Appearance.Text(g, "剩余", 166, y + 48, 11, Muted, false);
            Appearance.Text(g, w == null ? "尚无快照" : "已用 " + Appearance.Percent(w.UsedPercent), 166, y + 70, 11, Muted, false);
            if (w != null) Appearance.Text(g, "读于 " + w.RecordedAt.ToLocalTime().ToString("MM/dd HH:mm"), 166, y + 89, 8, Muted, false);
            if (primary) Appearance.DrawRing(g, new RectangleF(333, y + 49, 50, 50), w, weekly);
            else Appearance.DrawMoon(g, new RectangleF(343, y + 53, 37, 37), w == null ? (double?)null : w.RemainingPercent);
            Appearance.FillRound(g, settings.LightTheme ? Color.FromArgb(229, 235, 239) : Color.FromArgb(47, 58, 70), new RectangleF(42, y + 107, 356, 5), 2.5f);
            if (w != null && w.RemainingPercent > 0) Appearance.FillRound(g, color, new RectangleF(42, y + 107, Math.Max(5, (float)(356 * w.RemainingPercent / 100)), 5), 2.5f);
            Appearance.Text(g, Appearance.Countdown(w), 42, y + 127, 10, Muted, false);
        }
    }

    public sealed class SettingsForm : Form
    {
        private readonly TextBox root;
        private readonly NumericUpDown interval;
        private readonly CheckBox number, light, startup;
        private readonly Settings settings;
        public SettingsForm(Settings current)
        {
            settings = current; Text = "Codex Quota 设置"; StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
            AutoScaleMode = AutoScaleMode.Dpi; ClientSize = new Size(510, 340);
            Font = new Font("Microsoft YaHei UI", 9);
            Controls.Add(new Label { Text = "Codex 会话目录（sessions）", Location = new Point(20, 20), AutoSize = true });
            root = new TextBox { Text = current.ResolvedRoot, Location = new Point(20, 48), Width = 385 };
            Controls.Add(root);
            Button browse = new Button { Text = "浏览…", Location = new Point(413, 46), Size = new Size(75, 28) };
            browse.Click += delegate { using (FolderBrowserDialog dialog = new FolderBrowserDialog { Description = "选择包含 .jsonl 日志的 sessions 文件夹", SelectedPath = root.Text, ShowNewFolderButton = false }) if (dialog.ShowDialog(this) == DialogResult.OK) root.Text = dialog.SelectedPath; }; Controls.Add(browse);
            Button reset = new Button { Text = "恢复自动检测", Location = new Point(20, 86), Size = new Size(115, 28) };
            reset.Click += delegate { root.Text = UsageReader.DefaultSessionRoot(); }; Controls.Add(reset);
            Controls.Add(new Label { Text = "自动刷新间隔（秒）", Location = new Point(20, 136), AutoSize = true });
            interval = new NumericUpDown { Minimum = 10, Maximum = 3600, Value = current.RefreshSeconds, Location = new Point(190, 132), Width = 80 }; Controls.Add(interval);
            number = new CheckBox { Text = "同时显示数字托盘图标（5 小时剩余百分比）", Checked = current.ShowNumberIcon, Location = new Point(20, 175), AutoSize = true }; Controls.Add(number);
            light = new CheckBox { Text = "使用浅色详情面板", Checked = current.LightTheme, Location = new Point(20, 207), AutoSize = true }; Controls.Add(light);
            startup = new CheckBox { Text = "登录 Windows 时自动启动", Checked = Settings.AutoStart, Location = new Point(20, 239), AutoSize = true }; Controls.Add(startup);
            Button save = new Button { Text = "保存", Location = new Point(322, 290), Size = new Size(80, 30) };
            save.Click += Save; Controls.Add(save); AcceptButton = save;
            Button cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Location = new Point(410, 290), Size = new Size(80, 30) }; Controls.Add(cancel); CancelButton = cancel;
        }
        private void Save(object sender, EventArgs e)
        {
            try
            {
                string path = Environment.ExpandEnvironmentVariables(root.Text.Trim().Trim('"'));
                if (String.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathRooted(path) || !System.IO.Directory.Exists(path))
                { MessageBox.Show(this, "请选择一个存在的 sessions 目录。", "目录无效", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                string previousRoot = settings.SessionRoot; int previousInterval = settings.RefreshSeconds;
                bool previousNumber = settings.ShowNumberIcon, previousLight = settings.LightTheme;
                bool previousStartup = Settings.AutoStart;
                settings.SessionRoot = System.IO.Path.GetFullPath(path); settings.RefreshSeconds = (int)interval.Value;
                settings.ShowNumberIcon = number.Checked; settings.LightTheme = light.Checked;
                try
                {
                    if (startup.Checked != previousStartup) Settings.AutoStart = startup.Checked;
                    settings.Save();
                }
                catch
                {
                    settings.SessionRoot = previousRoot; settings.RefreshSeconds = previousInterval;
                    settings.ShowNumberIcon = previousNumber; settings.LightTheme = previousLight;
                    if (Settings.AutoStart != previousStartup) Settings.AutoStart = previousStartup;
                    throw;
                }
                DialogResult = DialogResult.OK; Close();
            }
            catch (Exception ex) { MessageBox.Show(this, "无法保存设置：" + ex.Message, "Codex Quota", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
    }
}
