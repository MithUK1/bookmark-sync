using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace BookmarkSync
{
    /// <summary>
    /// A small JSON reader and writer. Objects are Dictionary&lt;string, object&gt;,
    /// arrays List&lt;object&gt;, numbers double, plus string, bool and null. It keeps the
    /// program free of outside libraries and behaves the same on every runtime.
    /// </summary>
    public static class Json
    {
        public static object Parse(string text)
        {
            var reader = new Reader(text);
            reader.SkipSpace();
            object value = reader.ReadValue();
            reader.SkipSpace();
            if (!reader.AtEnd) throw new FormatException("Unexpected text after the JSON value.");
            return value;
        }

        public static string Write(object value)
        {
            var sb = new StringBuilder();
            WriteValue(sb, value);
            return sb.ToString();
        }

        // ----- Helpers for reading the parsed tree without casts everywhere -----

        public static Dictionary<string, object> Obj(object value)
        {
            return value as Dictionary<string, object>;
        }

        public static List<object> Arr(object value)
        {
            return value as List<object>;
        }

        /// <summary>A list of strings; a lone string counts as a list of one.</summary>
        public static List<string> StrList(object value)
        {
            var result = new List<string>();
            if (value is string) { result.Add((string)value); return result; }
            var arr = value as List<object>;
            if (arr != null) foreach (object item in arr) result.Add(Str(item));
            return result;
        }

        public static string Str(object value, string fallback = "")
        {
            return value is string ? (string)value : fallback;
        }

        public static int Int(object value, int fallback = 0)
        {
            if (value is double) return (int)(double)value;
            if (value is int) return (int)value;
            if (value is long) return (int)(long)value;
            return fallback;
        }

        public static double Num(object value, double fallback = 0)
        {
            if (value is double) return (double)value;
            if (value is int) return (int)value;
            if (value is long) return (long)value;
            return fallback;
        }

        public static bool Bool(object value, bool fallback = false)
        {
            return value is bool ? (bool)value : fallback;
        }

        public static object Get(object obj, string key)
        {
            var d = obj as Dictionary<string, object>;
            object v;
            return d != null && d.TryGetValue(key, out v) ? v : null;
        }

        // ----- Writing -----

        private static void WriteValue(StringBuilder sb, object value)
        {
            if (value == null) { sb.Append("null"); return; }
            string s = value as string;
            if (s != null) { WriteString(sb, s); return; }
            if (value is bool) { sb.Append((bool)value ? "true" : "false"); return; }
            if (value is int) { sb.Append(((int)value).ToString(CultureInfo.InvariantCulture)); return; }
            if (value is long) { sb.Append(((long)value).ToString(CultureInfo.InvariantCulture)); return; }
            if (value is double)
            {
                double d = (double)value;
                if (d == Math.Floor(d) && Math.Abs(d) < 1e15) sb.Append(((long)d).ToString(CultureInfo.InvariantCulture));
                else sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            var dict = value as IDictionary<string, object>;
            if (dict != null)
            {
                sb.Append('{');
                bool first = true;
                foreach (var pair in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, pair.Key);
                    sb.Append(':');
                    WriteValue(sb, pair.Value);
                }
                sb.Append('}');
                return;
            }
            var list = value as System.Collections.IEnumerable;
            if (list != null)
            {
                sb.Append('[');
                bool first = true;
                foreach (object item in list)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteValue(sb, item);
                }
                sb.Append(']');
                return;
            }
            throw new ArgumentException("Cannot write " + value.GetType().Name + " as JSON.");
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
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ----- Reading -----

        private sealed class Reader
        {
            private readonly string text;
            private int pos;

            public Reader(string text) { this.text = text; }

            public bool AtEnd { get { return pos >= text.Length; } }

            public void SkipSpace()
            {
                while (pos < text.Length && (text[pos] == ' ' || text[pos] == '\t' || text[pos] == '\n' || text[pos] == '\r' || text[pos] == '﻿')) pos++;
            }

            public object ReadValue()
            {
                if (AtEnd) throw new FormatException("Unexpected end of JSON.");
                char c = text[pos];
                if (c == '{') return ReadObject();
                if (c == '[') return ReadArray();
                if (c == '"') return ReadString();
                if (c == 't') { Expect("true"); return true; }
                if (c == 'f') { Expect("false"); return false; }
                if (c == 'n') { Expect("null"); return null; }
                return ReadNumber();
            }

            private void Expect(string word)
            {
                if (string.CompareOrdinal(text, pos, word, 0, word.Length) != 0) throw new FormatException("Bad JSON at position " + pos + ".");
                pos += word.Length;
            }

            private Dictionary<string, object> ReadObject()
            {
                var result = new Dictionary<string, object>();
                pos++;
                SkipSpace();
                if (pos < text.Length && text[pos] == '}') { pos++; return result; }
                while (true)
                {
                    SkipSpace();
                    string key = ReadString();
                    SkipSpace();
                    if (pos >= text.Length || text[pos] != ':') throw new FormatException("Expected ':' at position " + pos + ".");
                    pos++;
                    SkipSpace();
                    result[key] = ReadValue();
                    SkipSpace();
                    if (pos >= text.Length) throw new FormatException("Unexpected end of JSON.");
                    if (text[pos] == ',') { pos++; continue; }
                    if (text[pos] == '}') { pos++; return result; }
                    throw new FormatException("Expected ',' or '}' at position " + pos + ".");
                }
            }

            private List<object> ReadArray()
            {
                var result = new List<object>();
                pos++;
                SkipSpace();
                if (pos < text.Length && text[pos] == ']') { pos++; return result; }
                while (true)
                {
                    SkipSpace();
                    result.Add(ReadValue());
                    SkipSpace();
                    if (pos >= text.Length) throw new FormatException("Unexpected end of JSON.");
                    if (text[pos] == ',') { pos++; continue; }
                    if (text[pos] == ']') { pos++; return result; }
                    throw new FormatException("Expected ',' or ']' at position " + pos + ".");
                }
            }

            private string ReadString()
            {
                if (pos >= text.Length || text[pos] != '"') throw new FormatException("Expected a string at position " + pos + ".");
                pos++;
                var sb = new StringBuilder();
                while (true)
                {
                    if (pos >= text.Length) throw new FormatException("Unterminated string.");
                    char c = text[pos++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    if (pos >= text.Length) throw new FormatException("Unterminated string.");
                    char e = text[pos++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (pos + 4 > text.Length) throw new FormatException("Bad \\u escape.");
                            sb.Append((char)int.Parse(text.Substring(pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            pos += 4;
                            break;
                        default: throw new FormatException("Bad escape at position " + pos + ".");
                    }
                }
            }

            private double ReadNumber()
            {
                int start = pos;
                while (pos < text.Length && "+-0123456789.eE".IndexOf(text[pos]) >= 0) pos++;
                if (pos == start) throw new FormatException("Unexpected character at position " + pos + ".");
                return double.Parse(text.Substring(start, pos - start), CultureInfo.InvariantCulture);
            }
        }
    }
}
