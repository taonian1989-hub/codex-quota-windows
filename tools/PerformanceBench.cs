using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using CodexQuota;

internal static class PerformanceBench
{
    [DllImport("psapi.dll", SetLastError = true)] private static extern bool QueryWorkingSet(IntPtr process, IntPtr info, int size);
    [DllImport("user32.dll")] private static extern int GetGuiResources(IntPtr process, int flag);
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static Process process;
    private static double PrivateWorkingSet()
    {
        for (int bytes = 1024 * 1024; bytes <= 16 * 1024 * 1024; bytes *= 2)
        {
            IntPtr buffer = Marshal.AllocHGlobal(bytes);
            try
            {
                if (!QueryWorkingSet(process.Handle, buffer, bytes)) continue;
                long count = Marshal.ReadIntPtr(buffer).ToInt64(), pages = 0;
                for (int i = 0; i < count; i++)
                    if ((Marshal.ReadIntPtr(buffer, (i + 1) * IntPtr.Size).ToInt64() & 0x100) == 0) pages++;
                return pages * (double)Environment.SystemPageSize / 1048576;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return -1;
    }
    private static void Row(string phase, long elapsed, long allocated, double cpu)
    {
        double privateWorking = PrivateWorkingSet(); process.Refresh();
        Console.WriteLine(String.Join(",", new string[] { phase, elapsed.ToString(Invariant), (allocated / 1048576.0).ToString("F3", Invariant), cpu.ToString("F3", Invariant), privateWorking.ToString("F3", Invariant), (process.WorkingSet64 / 1048576.0).ToString("F3", Invariant), (process.PrivateMemorySize64 / 1048576.0).ToString("F3", Invariant), (GC.GetTotalMemory(false) / 1048576.0).ToString("F3", Invariant), GC.CollectionCount(0).ToString(), GC.CollectionCount(1).ToString(), GC.CollectionCount(2).ToString(), GetGuiResources(process.Handle, 0).ToString(), GetGuiResources(process.Handle, 1).ToString(), process.HandleCount.ToString() }));
        Console.Out.Flush();
    }
    [STAThread]
    private static int Main(string[] args)
    {
        process = Process.GetCurrentProcess();
        AppDomain.MonitoringIsEnabled = true;
        Console.WriteLine("phase,elapsed_ms,allocated_mb,cpu_ms,private_working_mb,working_mb,private_commit_mb,managed_heap_mb,gc0,gc1,gc2,gdi,user,handles");
        if (args[0] == "reader")
        {
            UsageReader reader = new UsageReader(); reader.Load(args[1], true);
            Row("reader_warm", 0, 0, 0);
            long memory = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
            double cpu = process.TotalProcessorTime.TotalMilliseconds;
            Stopwatch watch = Stopwatch.StartNew();
            int runs = args.Length > 2 ? Int32.Parse(args[2], Invariant) : 100;
            for (int i = 0; i < runs; i++) if (!reader.Load(args[1], true).HasData) return 2;
            Row("forced_" + runs, watch.ElapsedMilliseconds, AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize - memory, process.TotalProcessorTime.TotalMilliseconds - cpu);
            memory = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize; cpu = process.TotalProcessorTime.TotalMilliseconds; watch.Restart();
            for (int i = 0; i < 500; i++) if (reader.Load(args[1], false).ReadCount != 0) return 3;
            Row("cached_500", watch.ElapsedMilliseconds, AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize - memory, process.TotalProcessorTime.TotalMilliseconds - cpu);
            return 0;
        }
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        Type type = typeof(UsageReader).Assembly.GetType("CodexQuota.TrayContext", true);
        using (ApplicationContext context = (ApplicationContext)Activator.CreateInstance(type, new object[] { false, args[1] }))
        using (System.Windows.Forms.Timer driver = new System.Windows.Forms.Timer { Interval = 1000 })
        {
            FieldInfo timerField = type.GetField("timer", BindingFlags.Instance | BindingFlags.NonPublic);
            ((System.Windows.Forms.Timer)timerField.GetValue(context)).Interval = 100;
            int tick = 0; long allocated = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
            double cpu = process.TotalProcessorTime.TotalMilliseconds;
            driver.Tick += delegate
            {
                tick++;
                if (tick == 3) Row("tray_idle", 3000, AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize - allocated, process.TotalProcessorTime.TotalMilliseconds - cpu);
                if (tick == 4 && args[0] != "quiet")
                {
                    driver.Stop();
                    MethodInfo ensure = type.GetMethod("EnsurePanel", BindingFlags.Instance | BindingFlags.NonPublic);
                    QuotaForm form = ensure != null ? (QuotaForm)ensure.Invoke(context, null) : (QuotaForm)type.GetField("panel", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(context);
                    form.TopMost = false; form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-32000, -32000);
                    for (int i = 0; i < 30; i++) { form.Show(); Application.DoEvents(); form.Refresh(); form.Hide(); }
                    Row("panel_cycles_30", 0, AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize - allocated, process.TotalProcessorTime.TotalMilliseconds - cpu);
                    for (int i = 0; i < 20; i++) using (SettingsForm settings = new SettingsForm(new Settings()))
                    { settings.StartPosition = FormStartPosition.Manual; settings.Location = new Point(-32000, -32000); settings.Show(); Application.DoEvents(); settings.Close(); }
                    Row("settings_cycles_20", 0, AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize - allocated, process.TotalProcessorTime.TotalMilliseconds - cpu);
                    driver.Start();
                }
                if (tick == (args[0] == "quiet" ? 30 : 10))
                {
                    Row("tray_after_idle", tick * 1000, AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize - allocated, process.TotalProcessorTime.TotalMilliseconds - cpu);
                    driver.Stop(); type.GetMethod("Shutdown").Invoke(context, null);
                }
            };
            driver.Start(); Application.Run(context);
        }
        return 0;
    }
}
