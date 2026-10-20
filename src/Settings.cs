using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Runtime.Serialization;
using System.Xml;
using Microsoft.Win32;

namespace CodexQuota
{
    [DataContract]
    public sealed class Settings
    {
        [DataMember] public string SessionRoot { get; set; }
        [DataMember] public int RefreshSeconds { get; set; }
        [DataMember] public bool ShowNumberIcon { get; set; }
        [DataMember] public bool LightTheme { get; set; }
        public Settings() { RefreshSeconds = 30; ShowNumberIcon = true; }
        [OnDeserializing] private void InitializeDefaults(StreamingContext context) { RefreshSeconds = 30; ShowNumberIcon = true; }
        public static string DataDirectory { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexQuota"); } }
        public string ResolvedRoot { get { return String.IsNullOrWhiteSpace(SessionRoot) ? UsageReader.DefaultSessionRoot() : SessionRoot; } }
        public static Settings Load()
        {
            try
            {
                Settings s;
                using (FileStream stream = File.OpenRead(Path.Combine(DataDirectory, "settings.json"))) s = JsonData.Read<Settings>(stream);
                if (s == null) return new Settings();
                if (s.RefreshSeconds < 10 || s.RefreshSeconds > 3600) s.RefreshSeconds = 30;
                return s;
            }
            catch (SerializationException) { return new Settings(); }
            catch (XmlException) { return new Settings(); }
            catch (IOException) { return new Settings(); }
            catch (UnauthorizedAccessException) { return new Settings(); }
            catch (ArgumentException) { return new Settings(); }
            catch (InvalidOperationException) { return new Settings(); }
        }
        public void Save()
        {
            Directory.CreateDirectory(DataDirectory);
            string path = Path.Combine(DataDirectory, "settings.json");
            string tmp = path + ".tmp";
            File.WriteAllBytes(tmp, JsonData.Encode(this));
            if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
        }
        public static bool AutoStart
        {
            get
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                    return key != null && String.Equals(key.GetValue("CodexQuota") as string, AutoStartCommand, StringComparison.OrdinalIgnoreCase);
            }
            set
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                {
                    if (value) key.SetValue("CodexQuota", AutoStartCommand);
                    else key.DeleteValue("CodexQuota", false);
                }
            }
        }
        private static string AutoStartCommand { get { return "\"" + System.Windows.Forms.Application.ExecutablePath + "\" --background"; } }
    }

    public static class Diagnostics
    {
        public static string Export(UsageSnapshot s)
        {
            Directory.CreateDirectory(Settings.DataDirectory);
            string path = Path.Combine(Settings.DataDirectory, "diagnostics.log");
            if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024)
            { string old = path + ".1"; if (File.Exists(old)) File.Delete(old); File.Move(path, old); }
            StringBuilder b = new StringBuilder();
            b.AppendLine("=== Codex Quota 1.0.2 | " + DateTimeOffset.Now.ToString("o") + " ===");
            b.AppendLine("Runtime=" + Environment.Version + " OS=" + Environment.OSVersion);
            b.AppendLine("PrivateMB=" + (Process.GetCurrentProcess().PrivateMemorySize64 / 1048576.0).ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
            if (s != null)
            {
                b.AppendLine("Sessions=" + s.SessionRoot);
                b.AppendLine("Files=" + s.FileCount + " Read=" + s.ReadCount + " Unreadable=" + s.UnreadableFiles + " Limited=" + s.LimitedFiles);
                b.AppendLine("Error=" + s.Error);
                AppendWindow(b, "Primary", s.Primary); AppendWindow(b, "Secondary", s.Secondary);
            }
            File.AppendAllText(path, b.ToString(), new UTF8Encoding(false));
            return path;
        }
        private static void AppendWindow(StringBuilder b, string name, UsageWindow w)
        {
            if (w == null) { b.AppendLine(name + "=unavailable"); return; }
            b.AppendLine(name + " Remaining=" + w.RemainingPercent + " Recorded=" + w.RecordedAt.ToString("o") + " Reset=" + (w.ResetsAt.HasValue ? w.ResetsAt.Value.ToString("o") : "unknown") + " Estimated=" + w.ResetEstimated + " Source=" + w.SourceFile);
        }
    }
}
