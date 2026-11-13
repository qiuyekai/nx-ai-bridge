// nx-ai-bridge kernel - minimal JSON reader/writer.
// NOTE: everything under nx_receiver/ must compile with the C# 5 compiler that ships with
// .NET Framework 4.x (that is what NX and CodeDom use). No C# 6+ syntax anywhere.
//
// JSON values are represented with plain .NET types:
//   object -> Dictionary<string, object>     array  -> List<object>
//   string -> string    number -> double    true/false -> bool    null -> null
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace NxAiBridge
{
    public static class Json
    {
        // ------------------------------------------------------------------ parse
        public static object Parse(string text)
        {
            if (text == null) throw new FormatException("JSON text is null");
            int i = 0;
            if (text.Length > 0 && text[0] == '\ufeff') i = 1; // BOM
            object value = ReadValue(text, ref i);
            SkipWs(text, ref i);
            if (i != text.Length) throw new FormatException("Unexpected text after JSON value at position " + i);
            return value;
        }

        public static Dictionary<string, object> ParseObject(string text)
        {
            Dictionary<string, object> d = Parse(text) as Dictionary<string, object>;
            if (d == null) throw new FormatException("JSON object expected");
            return d;
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) i++;
        }

        private static object ReadValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) throw new FormatException("Unexpected end of JSON");
            char c = s[i];
            if (c == '{') return ReadObject(s, ref i);
            if (c == '[') return ReadArray(s, ref i);
            if (c == '"') return ReadString(s, ref i);
            if (c == 't') { Expect(s, ref i, "true"); return true; }
            if (c == 'f') { Expect(s, ref i, "false"); return false; }
            if (c == 'n') { Expect(s, ref i, "null"); return null; }
            return ReadNumber(s, ref i);
        }

        private static void Expect(string s, ref int i, string word)
        {
            if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0)
                throw new FormatException("Invalid JSON token at position " + i);
            i += word.Length;
        }

        private static object ReadNumber(string s, ref int i)
        {
            int start = i;
            while (i < s.Length && ("+-0123456789.eE".IndexOf(s[i]) >= 0)) i++;
            if (i == start) throw new FormatException("Invalid JSON character '" + s[start] + "' at position " + start);
            return double.Parse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        private static string ReadString(string s, ref int i)
        {
            StringBuilder sb = new StringBuilder();
            i++; // opening quote
            while (true)
            {
                if (i >= s.Length) throw new FormatException("Unterminated JSON string");
                char c = s[i++];
                if (c == '"') break;
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) throw new FormatException("Unterminated JSON escape");
                char e = s[i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw new FormatException("Bad \\u escape in JSON string");
                        sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: throw new FormatException("Bad escape '\\" + e + "' in JSON string");
                }
            }
            return sb.ToString();
        }

        private static List<object> ReadArray(string s, ref int i)
        {
            List<object> list = new List<object>();
            i++;
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return list; }
            while (true)
            {
                list.Add(ReadValue(s, ref i));
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("Unterminated JSON array");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return list; }
                throw new FormatException("Expected ',' or ']' at position " + i);
            }
        }

        private static Dictionary<string, object> ReadObject(string s, ref int i)
        {
            Dictionary<string, object> map = new Dictionary<string, object>();
            i++;
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return map; }
            while (true)
            {
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != '"') throw new FormatException("Expected string key at position " + i);
                string key = ReadString(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException("Expected ':' at position " + i);
                i++;
                map[key] = ReadValue(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("Unterminated JSON object");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return map; }
                throw new FormatException("Expected ',' or '}' at position " + i);
            }
        }

        // ------------------------------------------------------------------ write
        public static string Write(object value)
        {
            StringBuilder sb = new StringBuilder();
            WriteValue(sb, value, 0);
            return sb.ToString();
        }

        private static void WriteValue(StringBuilder sb, object v, int depth)
        {
            if (depth > 40) { sb.Append("null"); return; }
            if (v == null) { sb.Append("null"); return; }
            if (v is string) { WriteString(sb, (string)v); return; }
            if (v is bool) { sb.Append((bool)v ? "true" : "false"); return; }
            if (v is double || v is float || v is decimal)
            {
                double d = Convert.ToDouble(v, CultureInfo.InvariantCulture);
                if (double.IsNaN(d) || double.IsInfinity(d)) { sb.Append("null"); return; }
                if (Math.Abs(d) < 1e15 && d == Math.Floor(d)) sb.Append(((long)d).ToString(CultureInfo.InvariantCulture));
                else sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            if (v is int || v is long || v is short || v is byte || v is uint || v is ulong || v is ushort || v is sbyte)
            {
                sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture));
                return;
            }
            if (v is Enum || v is char || v is Guid || v is DateTime) { WriteString(sb, Convert.ToString(v, CultureInfo.InvariantCulture)); return; }
            IDictionary dict = v as IDictionary;
            if (dict != null)
            {
                sb.Append('{');
                bool first = true;
                foreach (DictionaryEntry e in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, Convert.ToString(e.Key, CultureInfo.InvariantCulture));
                    sb.Append(':');
                    WriteValue(sb, e.Value, depth + 1);
                }
                sb.Append('}');
                return;
            }
            IEnumerable seq = v as IEnumerable;
            if (seq != null)
            {
                sb.Append('[');
                bool first = true;
                foreach (object item in seq)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteValue(sb, item, depth + 1);
                }
                sb.Append(']');
                return;
            }
            WriteString(sb, v.ToString());
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }

    /// <summary>Shortcut for building result objects: R.Obj("key", value, "key2", value2).</summary>
    public static class R
    {
        public static Dictionary<string, object> Obj(params object[] pairs)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            for (int i = 0; i + 1 < pairs.Length; i += 2) d[Convert.ToString(pairs[i])] = pairs[i + 1];
            return d;
        }

        public static List<object> List(params object[] items)
        {
            return new List<object>(items);
        }

        public static double Round(double v)
        {
            return Math.Round(v, 6);
        }

        public static List<object> Nums(params double[] values)
        {
            List<object> l = new List<object>();
            foreach (double v in values) l.Add(Math.Round(v, 6));
            return l;
        }
    }
}
