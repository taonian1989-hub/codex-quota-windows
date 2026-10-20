using System;
using System.Globalization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Xml;

namespace CodexQuota
{
    internal struct QuotaValues
    {
        internal double? Used, Minutes, Reset;
    }
    internal struct QuotaEvent
    {
        internal string Kind, PayloadKind, Stamp, LimitId, Plan;
        internal QuotaValues Primary, Secondary;
    }
    // Framework streaming JSON reader: unrelated fields are skipped rather than
    // materialized into dictionaries, strings and arrays. No Web stack is loaded.
    internal static class QuotaJsonReader
    {
        private static readonly XmlDictionaryReaderQuotas Quotas = new XmlDictionaryReaderQuotas
        {
            MaxDepth = 64, MaxArrayLength = 4 * 1024 * 1024,
            MaxStringContentLength = 4 * 1024 * 1024,
            MaxBytesPerRead = 4 * 1024 * 1024, MaxNameTableCharCount = 4 * 1024 * 1024
        };
        internal static QuotaEvent Read(byte[] bytes, int offset, int count)
        {
            while (count >= 3 && bytes[offset] == 239 && bytes[offset + 1] == 187 && bytes[offset + 2] == 191) { offset += 3; count -= 3; }
            if (!CompleteObject(bytes, offset, count)) throw new XmlException("Incomplete JSON envelope");
            QuotaEvent record = new QuotaEvent();
            using (XmlDictionaryReader r = JsonReaderWriterFactory.CreateJsonReader(bytes, offset, count, Encoding.UTF8, Quotas, null))
            {
                r.MoveToContent();
                if (!Object(r)) return record;
                while (r.MoveToContent() == XmlNodeType.Element)
                {
                    switch (r.LocalName)
                    {
                        case "timestamp": record.Stamp = String(r); break;
                        case "type": record.Kind = String(r); break;
                        case "payload": Payload(r, ref record); break;
                        default: r.Skip(); break;
                    }
                }
                r.ReadEndElement();
                if (r.MoveToContent() != XmlNodeType.None) throw new XmlException("Trailing JSON content");
            }
            return record;
        }
        private static bool CompleteObject(byte[] data, int offset, int count)
        {
            // The framework reader may synthesize an XML end node for truncated
            // JSON. Check the original envelope so a half-written event is never
            // promoted to a valid reading, and reject content after the root.
            int end = offset + count, depth = 0;
            bool quoted = false, escaped = false, started = false, finished = false;
            for (int i = offset; i < end; i++)
            {
                byte b = data[i];
                if (quoted)
                {
                    if (escaped) escaped = false;
                    else if (b == 92) escaped = true;
                    else if (b == 34) quoted = false;
                    continue;
                }
                if (b == 32 || b == 9 || b == 13 || b == 10) continue;
                if (finished) return false;
                if (!started) { if (b != 123) return false; started = true; }
                if (b == 34) quoted = true;
                else if (b == 123 || b == 91) { if (++depth > 64) return false; }
                else if (b == 125 || b == 93) { if (--depth < 0) return false; if (depth == 0) finished = true; }
            }
            return started && finished && !quoted && depth == 0;
        }
        private static bool Object(XmlDictionaryReader r)
        {
            if (r.GetAttribute("type") != "object") { r.Skip(); return false; }
            bool empty = r.IsEmptyElement; r.ReadStartElement(); return !empty;
        }
        private static string String(XmlDictionaryReader r)
        {
            string type = r.GetAttribute("type");
            if (type == null || type == "string") return r.ReadElementContentAsString();
            r.Skip(); return null;
        }
        private static double? Number(XmlDictionaryReader r)
        {
            if (r.GetAttribute("type") != "number") { r.Skip(); return null; }
            double number;
            return Double.TryParse(r.ReadElementContentAsString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)
                && !Double.IsNaN(number) && !Double.IsInfinity(number) ? (double?)number : null;
        }
        private static void Payload(XmlDictionaryReader r, ref QuotaEvent record)
        {
            // Duplicate members follow the last-member behavior of the previous reader.
            record.PayloadKind = null; record.Primary = new QuotaValues(); record.Secondary = new QuotaValues(); record.LimitId = null; record.Plan = null;
            if (!Object(r)) return;
            while (r.MoveToContent() == XmlNodeType.Element)
            {
                if (r.LocalName == "type") record.PayloadKind = String(r);
                else if (r.LocalName == "rate_limits") Limits(r, ref record);
                else r.Skip();
            }
            r.ReadEndElement();
        }
        private static void Limits(XmlDictionaryReader r, ref QuotaEvent record)
        {
            record.Primary = new QuotaValues(); record.Secondary = new QuotaValues(); record.LimitId = null; record.Plan = null;
            if (!Object(r)) return;
            while (r.MoveToContent() == XmlNodeType.Element)
            {
                switch (r.LocalName)
                {
                    case "limit_id": record.LimitId = String(r); break;
                    case "plan_type": record.Plan = String(r); break;
                    case "primary": record.Primary = Window(r); break;
                    case "secondary": record.Secondary = Window(r); break;
                    default: r.Skip(); break;
                }
            }
            r.ReadEndElement();
        }
        private static QuotaValues Window(XmlDictionaryReader r)
        {
            QuotaValues value = new QuotaValues();
            if (!Object(r)) return value;
            while (r.MoveToContent() == XmlNodeType.Element)
            {
                switch (r.LocalName)
                {
                    case "used_percent": value.Used = Number(r); break;
                    case "window_minutes": value.Minutes = Number(r); break;
                    case "resets_at": value.Reset = Number(r); break;
                    default: r.Skip(); break;
                }
            }
            r.ReadEndElement(); return value;
        }
    }
}
