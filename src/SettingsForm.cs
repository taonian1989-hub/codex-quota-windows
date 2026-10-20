using System;
using System.Drawing;
using System.Windows.Forms;

namespace CodexQuota
{
    public sealed class SettingsForm : Form
    {
        private readonly TextBox root;
        private readonly ComboBox interval;
        private readonly Label intervalHint;
        private readonly CheckBox number, light, startup;
        private readonly Settings settings;
        private readonly float uiScale;
        private static readonly int[] RefreshChoices = { 10, 15, 30, 60, 120, 300, 600, 1800, 3600 };

        public SettingsForm(Settings current) : this(current, 0) { }

        // Production uses the actual monitor DPI. Explicit scales also let previews
        // and regression tests exercise the exact production layout at other DPIs.
        internal SettingsForm(Settings current, float scaleOverride)
        {
            settings = current;
            using (Graphics g = CreateGraphics()) uiScale = scaleOverride > 0 ? scaleOverride : g.DpiX / 96f;
            SuspendLayout();
            AutoScaleMode = AutoScaleMode.None;
            Font = new Font("Microsoft YaHei UI", 12 * uiScale, FontStyle.Regular, GraphicsUnit.Pixel);
            Text = "Codex Quota 设置";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
            ClientSize = new Size(Math.Min(Px(560), area.Width - 24), Math.Min(Px(430), area.Height - 64));
            MinimumSize = new Size(Math.Min(Px(440), Width), Math.Min(Px(320), Height));
            Padding = Gap(20, 18, 20, 18);

            TableLayoutPanel shell = new TableLayoutPanel { Name = "settingsShell", Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
            shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(shell);
            Panel scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty, TabIndex = 0 };
            shell.Controls.Add(scroll, 0, 0);
            TableLayoutPanel content = new TableLayoutPanel { Name = "settingsContent", Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Margin = Padding.Empty, Padding = Gap(0, 0, 8, 0), TabIndex = 0 };
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            scroll.Controls.Add(content);

            AddRow(content, new Label { Text = "Codex 会话目录（sessions）", AutoSize = true, Margin = Gap(0, 0, 0, 8) });
            TableLayoutPanel pathRow = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 2, Margin = Gap(0, 0, 0, 8) };
            pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            root = new TextBox { Name = "sessionRoot", Text = current.ResolvedRoot, Dock = DockStyle.Fill, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = Gap(0, 4, 10, 4), TabIndex = 0 };
            Button browse = MakeButton("浏览…", "browseSessions"); browse.TabIndex = 1;
            browse.Click += delegate
            {
                using (FolderBrowserDialog dialog = new FolderBrowserDialog { Description = "选择包含 .jsonl 日志的 sessions 文件夹", SelectedPath = root.Text, ShowNewFolderButton = false })
                    if (dialog.ShowDialog(this) == DialogResult.OK) root.Text = dialog.SelectedPath;
            };
            pathRow.Controls.Add(root, 0, 0); pathRow.Controls.Add(browse, 1, 0); AddRow(content, pathRow);
            Button reset = MakeButton("恢复自动检测", "resetSessions");
            reset.Anchor = AnchorStyles.Left; reset.Margin = Gap(0, 0, 0, 20); reset.TabIndex = 1;
            reset.Click += delegate { root.Text = UsageReader.DefaultSessionRoot(); };
            AddRow(content, reset);

            TableLayoutPanel refreshRow = new TableLayoutPanel { Name = "refreshRow", AutoSize = true, Anchor = AnchorStyles.Left, ColumnCount = 3, Margin = Gap(0, 0, 0, 6), TabIndex = 2 };
            for (int i = 0; i < 3; i++) refreshRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            refreshRow.Controls.Add(new Label { Name = "refreshLabel", Text = "自动刷新间隔", AutoSize = true, Anchor = AnchorStyles.Left, Margin = Gap(0, 0, 16, 0) }, 0, 0);
            interval = new ComboBox { Name = "refreshInterval", DropDownStyle = ComboBoxStyle.DropDown, Width = Px(120), DropDownWidth = Px(120), MaxDropDownItems = 9, IntegralHeight = true, AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems, Margin = Gap(0, 2, 12, 2), TabIndex = 0, AccessibleName = "自动刷新间隔（秒）" };
            foreach (int seconds in RefreshChoices) interval.Items.Add(seconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            interval.Text = current.RefreshSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            refreshRow.Controls.Add(interval, 1, 0);
            refreshRow.Controls.Add(new Label { Text = "秒", AutoSize = true, Anchor = AnchorStyles.Left, Margin = Padding.Empty }, 2, 0);
            AddRow(content, refreshRow);
            intervalHint = new Label { Name = "refreshHint", Text = "可选择常用间隔，或输入 10–3600 秒。", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = Gap(0, 0, 0, 18) };
            AddRow(content, intervalHint);

            number = new CheckBox { Name = "showNumberIcon", Text = "显示数字托盘图标", Checked = current.ShowNumberIcon, AutoSize = true, Margin = Gap(0, 0, 0, 4), TabIndex = 3 };
            AddRow(content, number);
            AddRow(content, new Label { Text = "数字表示 5 小时窗口的剩余百分比。", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = Gap(23, 0, 0, 14) });
            light = new CheckBox { Name = "lightTheme", Text = "使用浅色详情面板", Checked = current.LightTheme, AutoSize = true, Margin = Gap(0, 0, 0, 14), TabIndex = 4 }; AddRow(content, light);
            startup = new CheckBox { Name = "autoStart", Text = "登录 Windows 时自动启动", Checked = Settings.AutoStart, AutoSize = true, Margin = Gap(0, 0, 0, 8), TabIndex = 5 }; AddRow(content, startup);

            FlowLayoutPanel actions = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Gap(0, 16, 0, 0), TabIndex = 1 };
            Button cancel = MakeButton("取消", "cancelSettings"); cancel.DialogResult = DialogResult.Cancel; cancel.TabIndex = 1;
            Button save = MakeButton("保存", "saveSettings"); save.Margin = Gap(0, 0, 10, 0); save.TabIndex = 0; save.Click += Save;
            actions.Controls.Add(cancel); actions.Controls.Add(save); shell.Controls.Add(actions, 0, 1);
            AcceptButton = save; CancelButton = cancel;
            ResumeLayout(true);
        }
        private int Px(float value) { return (int)Math.Round(value * uiScale); }
        private Padding Gap(int left, int top, int right, int bottom) { return new Padding(Px(left), Px(top), Px(right), Px(bottom)); }
        private Button MakeButton(string text, string name)
        {
            return new Button { Name = name, Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(Px(86), Px(34)), Padding = Gap(14, 5, 14, 5), Margin = Padding.Empty, UseVisualStyleBackColor = true };
        }
        private static void AddRow(TableLayoutPanel panel, Control control)
        {
            int row = panel.RowCount++;
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            panel.Controls.Add(control, 0, row);
        }
        internal bool TryReadRefreshInterval(out int seconds)
        {
            bool valid = Int32.TryParse(interval.Text.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out seconds) && seconds >= 10 && seconds <= 3600;
            intervalHint.Text = valid ? "可选择常用间隔，或输入 10–3600 秒。" : "请输入 10–3600 之间的整数秒数。";
            intervalHint.ForeColor = valid ? SystemColors.GrayText : Color.Firebrick;
            if (!valid) { interval.Focus(); interval.SelectAll(); }
            return valid;
        }
        private void Save(object sender, EventArgs e)
        {
            try
            {
                int refreshSeconds;
                if (!TryReadRefreshInterval(out refreshSeconds)) return;
                string path = Environment.ExpandEnvironmentVariables(root.Text.Trim().Trim('"'));
                if (String.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathRooted(path) || !System.IO.Directory.Exists(path))
                { MessageBox.Show(this, "请选择一个存在的 sessions 目录。", "目录无效", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                string previousRoot = settings.SessionRoot; int previousInterval = settings.RefreshSeconds;
                bool previousNumber = settings.ShowNumberIcon, previousLight = settings.LightTheme;
                bool previousStartup = Settings.AutoStart;
                settings.SessionRoot = System.IO.Path.GetFullPath(path); settings.RefreshSeconds = refreshSeconds;
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
