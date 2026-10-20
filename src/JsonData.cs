using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace CodexQuota
{
    internal static class JsonData
    {
        private static class Contract<T> { internal static readonly DataContractJsonSerializer Serializer = new DataContractJsonSerializer(typeof(T)); }
        internal static T Read<T>(Stream stream) { return (T)Contract<T>.Serializer.ReadObject(stream); }
        internal static byte[] Encode<T>(T value)
        {
            using (MemoryStream stream = new MemoryStream()) { Contract<T>.Serializer.WriteObject(stream, value); return stream.ToArray(); }
        }
        internal static string Stringify<T>(T value) { return Encoding.UTF8.GetString(Encode(value)); }
    }
    [DataContract]
    internal sealed class ErrorReport { [DataMember(Name = "error")] internal string Error; }
    [DataContract]
    internal sealed class SnapshotReport
    {
        [DataMember(Name = "version")] internal string Version;
        [DataMember(Name = "sessions")] internal string Sessions;
        [DataMember(Name = "loaded_at")] internal string LoadedAt;
        [DataMember(Name = "recorded_at")] internal string RecordedAt;
        [DataMember(Name = "has_data")] internal bool HasData;
        [DataMember(Name = "error")] internal string Error;
        [DataMember(Name = "files")] internal int Files;
        [DataMember(Name = "read_files")] internal int ReadFiles;
        [DataMember(Name = "unreadable_files")] internal int UnreadableFiles;
        [DataMember(Name = "scan_limited_files")] internal int LimitedFiles;
        [DataMember(Name = "primary")] internal WindowReport Primary;
        [DataMember(Name = "secondary")] internal WindowReport Secondary;
    }
    [DataContract]
    internal sealed class WindowReport
    {
        [DataMember(Name = "remaining_percent")] internal double Remaining;
        [DataMember(Name = "used_percent")] internal double Used;
        [DataMember(Name = "window_minutes")] internal int Minutes;
        [DataMember(Name = "resets_at")] internal string Reset;
        [DataMember(Name = "reset_estimated")] internal bool Estimated;
        [DataMember(Name = "reset_time_passed")] internal bool Expired;
        [DataMember(Name = "recorded_at")] internal string Recorded;
        [DataMember(Name = "source")] internal string Source;
        [DataMember(Name = "plan")] internal string Plan;
    }
}
