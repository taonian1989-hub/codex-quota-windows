using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace CodexQuota
{
    public static class Appearance
    {
        public static readonly Color Green = Color.FromArgb(99, 217, 171);
        public static readonly Color Amber = Color.FromArgb(255, 184, 84);
        public static readonly Color Red = Color.FromArgb(250, 112, 123);
        public static readonly Color Moon = Color.FromArgb(255, 184, 84);
        private static readonly double[] MoonThresholds = { 20, 35, 45, 55, 70, 90 };
        private static readonly double[] MoonCoefficients = { 0.78, 0.588, 0.309, 0, -0.309, -0.809, -1 };
        public static Color Status(double remaining) { return remaining > 40 ? Green : remaining > 15 ? Amber : Red; }
        public static string Percent(double value) { return value.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%"; }
        public static string Age(DateTimeOffset time)
        {
            TimeSpan age = DateTimeOffset.Now - time;
            if (age.TotalMinutes < 1) return "刚刚";
            if (age.TotalHours < 1) return ((int)age.TotalMinutes) + " 分钟前";
            if (age.TotalDays < 1) return ((int)age.TotalHours) + " 小时 " + age.Minutes + " 分钟前";
            return ((int)age.TotalDays) + " 天前";
        }
        public static string Countdown(UsageWindow w)
        {
            if (w == null) return "等待 Codex 写入额度记录";
            if (!w.ResetsAt.HasValue) return "日志未提供重置时间";
            TimeSpan left = w.ResetsAt.Value - DateTimeOffset.Now;
            if (left.TotalSeconds <= 0) return "重置时间已过 · 等待新快照";
            string duration = left.TotalDays >= 1 ? ((int)left.TotalDays) + " 天 " + left.Hours + " 小时" : left.TotalHours >= 1 ? ((int)left.TotalHours) + " 小时 " + left.Minutes + " 分钟" : Math.Max(1, (int)Math.Ceiling(left.TotalMinutes)) + " 分钟";
            return (w.ResetEstimated ? "预计 " : "") + duration + "后重置  ·  " + w.ResetsAt.Value.ToLocalTime().ToString("MM/dd HH:mm");
        }
        public static int MoonLevel(double remaining)
        {

            for (int i = 0; i < MoonThresholds.Length; i++) if (remaining < MoonThresholds[i]) return i;
            return 6;
        }
        public static GraphicsPath Rounded(RectangleF r, float radius)
        {
            float d = radius * 2;
            GraphicsPath p = new GraphicsPath();
            p.AddArc(r.Left, r.Top, d, d, 180, 90); p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90); p.CloseFigure();
            return p;
        }
        public static void FillRound(Graphics g, Color color, RectangleF rect, float radius)
        { using (GraphicsPath p = Rounded(rect, radius)) using (Brush b = new SolidBrush(color)) g.FillPath(b, p); }
        public static void Text(Graphics g, string text, float x, float y, float size, Color color, bool bold)
        {
            using (Font font = new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel))
            using (Brush brush = new SolidBrush(color)) g.DrawString(text, font, brush, x, y, StringFormat.GenericTypographic);
        }
        public static void DrawMoon(Graphics g, RectangleF r, double? remaining)
        {
            if (!remaining.HasValue) return;
            using (Brush dark = new SolidBrush(Color.FromArgb(105, 115, 126))) g.FillEllipse(dark, r);
            if (remaining <= 0) return;

            double k = MoonCoefficients[MoonLevel(remaining.Value)];
            float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, rx = r.Width / 2, ry = r.Height / 2;
            PointF[] points = new PointF[130];
            for (int i = 0; i <= 64; i++)
            {
                double theta = -Math.PI / 2 + Math.PI * i / 64;
                points[i] = new PointF(cx + rx * (float)Math.Cos(theta), cy + ry * (float)Math.Sin(theta));
            }
            for (int i = 0; i <= 64; i++)
            {
                double theta = Math.PI / 2 - Math.PI * i / 64;
                points[65 + i] = new PointF(cx + rx * (float)(k * Math.Cos(theta)), cy + ry * (float)Math.Sin(theta));
            }
            using (Brush lit = new SolidBrush(Moon)) g.FillPolygon(lit, points);
        }
        public static void DrawRing(Graphics g, RectangleF rect, UsageWindow primary, UsageWindow secondary)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float penWidth = rect.Width * 0.09f;
            RectangleF ring = RectangleF.Inflate(rect, -penWidth / 2 - 1, -penWidth / 2 - 1);
            using (Pen track = new Pen(Color.FromArgb(115, 125, 136), penWidth)) g.DrawEllipse(track, ring);
            if (primary != null)
            {
                Color color = primary.Expired(DateTimeOffset.Now) ? Color.FromArgb(138, 149, 162) : Status(primary.RemainingPercent);
                using (Pen pen = new Pen(color, penWidth))
                {
                    pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                    if (primary.RemainingPercent <= 0) g.DrawEllipse(pen, ring);
                    else g.DrawArc(pen, ring, -90, (float)Math.Max(0.2, 360 * primary.RemainingPercent / 100));
                }
            }
            DrawMoon(g, RectangleF.Inflate(rect, -rect.Width * 0.24f, -rect.Height * 0.24f), secondary == null ? (double?)null : secondary.RemainingPercent);
        }
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);
        public static Icon MakeIcon(UsageSnapshot snapshot, bool number, bool light, int size)
        {
            using (Bitmap bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                if (number)
                {
                    string text = snapshot == null || snapshot.Primary == null || snapshot.Primary.Expired(DateTimeOffset.Now) ? "--" : snapshot.Primary.RemainingPercent.ToString("0");
                    Color color = snapshot == null || snapshot.Primary == null ? Color.White : Status(snapshot.Primary.RemainingPercent);
                    // Opaque backing preserves contrast on both light and dark taskbars.
                    FillRound(g, Color.FromArgb(255, 23, 29, 36), new RectangleF(0, 2, size, size - 4), 5);
                    using (Font f = new Font("Segoe UI", text.Length > 2 ? size * 0.48f : size * 0.65f, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (Brush b = new SolidBrush(color))
                    using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap })
                        g.DrawString(text, f, b, new RectangleF(-1, 0, size + 2, size), sf);
                }
                else DrawRing(g, new RectangleF(0, 0, size, size), snapshot == null ? null : snapshot.Primary, snapshot == null ? null : snapshot.Secondary);
                IntPtr handle = bitmap.GetHicon();
                try { using (Icon original = Icon.FromHandle(handle)) return (Icon)original.Clone(); }
                finally { DestroyIcon(handle); }
            }
        }
    }
}
