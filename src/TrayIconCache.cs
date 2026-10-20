using System;
using System.Drawing;
using System.Globalization;

namespace CodexQuota
{
    // Only the two currently displayed images are retained. Tooltip freshness is
    // updated separately, so an unchanged image does not require native GDI work.
    internal sealed class TrayIconCache : IDisposable
    {
        internal Icon Ring { get; private set; }
        internal Icon Number { get; private set; }
        internal int RenderCount { get; private set; }
        internal bool RingChanged { get; private set; }
        internal bool NumberChanged { get; private set; }
        private string ringKey, numberKey;
        internal void Update(UsageSnapshot snapshot, bool light, bool showNumber)
        {
            UsageWindow p = snapshot == null ? null : snapshot.Primary;
            UsageWindow s = snapshot == null ? null : snapshot.Secondary;
            string key = (p == null ? "?" : p.RemainingPercent.ToString("R", CultureInfo.InvariantCulture) + ":" + p.Expired(DateTimeOffset.Now))
                + "|" + (s == null ? -1 : s.RemainingPercent <= 0 ? 0 : Appearance.MoonLevel(s.RemainingPercent) + 1) + "|" + light;
            RingChanged = key != ringKey;
            if (RingChanged)
            {
                Icon next = Appearance.MakeIcon(snapshot, false, light, 32);
                if (Ring != null) Ring.Dispose(); Ring = next; ringKey = key; RenderCount++;
            }
            key = !showNumber ? null : (p == null || p.Expired(DateTimeOffset.Now) ? "--" : p.RemainingPercent.ToString("0"))
                + "|" + (p == null ? Color.White.ToArgb() : Appearance.Status(p.RemainingPercent).ToArgb()) + "|" + light;
            NumberChanged = key != numberKey;
            if (NumberChanged)
            {
                Icon next = showNumber ? Appearance.MakeIcon(snapshot, true, light, 32) : null;
                if (Number != null) Number.Dispose(); Number = next; numberKey = key;
                if (next != null) RenderCount++;
            }
        }
        public void Dispose()
        {
            if (Ring != null) Ring.Dispose(); if (Number != null) Number.Dispose();
            Ring = null; Number = null; ringKey = null; numberKey = null;
        }
    }
}
