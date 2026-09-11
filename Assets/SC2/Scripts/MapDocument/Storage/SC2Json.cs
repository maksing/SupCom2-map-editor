using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SC2.MapEditor.Document
{
    public enum SC2JsonKind { Null, Bool, Number, String, Array, Object }

    /// <summary>
    /// A tiny, dependency-free JSON tree used only by SC2MapDocumentSerializer.
    ///
    /// Deliberately NOT UnityEngine.JsonUtility (elsewhere in the codebase,
    /// e.g. SC2RuntimeTerrainPersistence.cs): JsonUtility is a native-engine
    /// binding, so it can't be exercised in the standalone characterization
    /// test project (Assets/SC2/Tests~/StandaloneCharacterization/) the way
    /// the rest of this Document layer is meant to be. Also NOT Newtonsoft —
    /// this package snapshot ships with no Packages/manifest.json at all, so
    /// nothing here can assume a package dependency is present in whatever
    /// project it's dropped into (build-compatibility principle). A tree of
    /// explicit values keeps manifest migrations (ISC2DocumentMigration)
    /// simple: read raw tree, inspect "formatVersion", patch shape, map
    /// fields explicitly — no reflection involved anywhere.
    /// </summary>
    public sealed class SC2JsonValue
    {
        public SC2JsonKind Kind { get; private set; }
        private bool _bool;
        private double _number;
        private string _string;
        private List<SC2JsonValue> _array;
        private List<KeyValuePair<string, SC2JsonValue>> _object;

        public static readonly SC2JsonValue Null = new SC2JsonValue { Kind = SC2JsonKind.Null };

        public static SC2JsonValue Of(bool value) => new SC2JsonValue { Kind = SC2JsonKind.Bool, _bool = value };
        public static SC2JsonValue Of(double value) => new SC2JsonValue { Kind = SC2JsonKind.Number, _number = value };
        public static SC2JsonValue Of(long value) => new SC2JsonValue { Kind = SC2JsonKind.Number, _number = value };
        public static SC2JsonValue Of(string value) =>
            value == null ? Null : new SC2JsonValue { Kind = SC2JsonKind.String, _string = value };

        public static SC2JsonValue NewArray() => new SC2JsonValue { Kind = SC2JsonKind.Array, _array = new List<SC2JsonValue>() };
        public static SC2JsonValue NewObject() => new SC2JsonValue { Kind = SC2JsonKind.Object, _object = new List<KeyValuePair<string, SC2JsonValue>>() };

        public List<SC2JsonValue> AsArrayItems => _array;

        public SC2JsonValue Add(SC2JsonValue item)
        {
            if (Kind != SC2JsonKind.Array) throw new InvalidOperationException("Not an array");
            _array.Add(item);
            return this;
        }

        public SC2JsonValue Set(string key, SC2JsonValue value)
        {
            if (Kind != SC2JsonKind.Object) throw new InvalidOperationException("Not an object");
            for (int i = 0; i < _object.Count; i++)
            {
                if (_object[i].Key == key)
                {
                    _object[i] = new KeyValuePair<string, SC2JsonValue>(key, value);
                    return this;
                }
            }
            _object.Add(new KeyValuePair<string, SC2JsonValue>(key, value));
            return this;
        }

        public bool TryGet(string key, out SC2JsonValue value)
        {
            if (Kind == SC2JsonKind.Object)
            {
                foreach (var kv in _object)
                {
                    if (kv.Key == key) { value = kv.Value; return true; }
                }
            }
            value = null;
            return false;
        }

        public SC2JsonValue Get(string key) => TryGet(key, out var v) ? v : Null;

        public string AsString(string fallback = null) => Kind == SC2JsonKind.String ? _string : fallback;
        public bool AsBool(bool fallback = false) => Kind == SC2JsonKind.Bool ? _bool : fallback;
        public long AsLong(long fallback = 0) => Kind == SC2JsonKind.Number ? (long)_number : fallback;
        public int AsInt(int fallback = 0) => Kind == SC2JsonKind.Number ? (int)_number : fallback;
        public double AsDouble(double fallback = 0) => Kind == SC2JsonKind.Number ? _number : fallback;

        public IEnumerable<SC2JsonValue> Items()
        {
            if (Kind != SC2JsonKind.Array) yield break;
            foreach (var item in _array) yield return item;
        }

        public IEnumerable<KeyValuePair<string, SC2JsonValue>> Members()
        {
            if (Kind != SC2JsonKind.Object) yield break;
            foreach (var kv in _object) yield return kv;
        }

        public string Write(bool indent = true)
        {
            var sb = new StringBuilder();
            WriteTo(sb, indent ? 0 : -1);
            return sb.ToString();
        }

        private void WriteTo(StringBuilder sb, int indent)
        {
            switch (Kind)
            {
                case SC2JsonKind.Null: sb.Append("null"); break;
                case SC2JsonKind.Bool: sb.Append(_bool ? "true" : "false"); break;
                case SC2JsonKind.Number: sb.Append(_number.ToString("R", CultureInfo.InvariantCulture)); break;
                case SC2JsonKind.String: WriteString(sb, _string); break;
                case SC2JsonKind.Array: WriteArray(sb, indent); break;
                case SC2JsonKind.Object: WriteObject(sb, indent); break;
            }
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
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        private void WriteArray(StringBuilder sb, int indent)
        {
            if (_array.Count == 0) { sb.Append("[]"); return; }
            sb.Append('[');
            var childIndent = indent < 0 ? -1 : indent + 1;
            for (int i = 0; i < _array.Count; i++)
            {
                if (i > 0) sb.Append(',');
                NewlineIndent(sb, childIndent);
                _array[i].WriteTo(sb, childIndent);
            }
            NewlineIndent(sb, indent);
            sb.Append(']');
        }

        private void WriteObject(StringBuilder sb, int indent)
        {
            if (_object.Count == 0) { sb.Append("{}"); return; }
            sb.Append('{');
            var childIndent = indent < 0 ? -1 : indent + 1;
            for (int i = 0; i < _object.Count; i++)
            {
                if (i > 0) sb.Append(',');
                NewlineIndent(sb, childIndent);
                WriteString(sb, _object[i].Key);
                sb.Append(':');
                if (childIndent >= 0) sb.Append(' ');
                _object[i].Value.WriteTo(sb, childIndent);
            }
            NewlineIndent(sb, indent);
            sb.Append('}');
        }

        private static void NewlineIndent(StringBuilder sb, int indent)
        {
            if (indent < 0) return;
            sb.Append('\n');
            sb.Append(' ', indent * 2);
        }

        public static SC2JsonValue Parse(string text)
        {
            int pos = 0;
            var value = ParseValue(text, ref pos);
            SkipWhitespace(text, ref pos);
            if (pos != text.Length)
                throw new FormatException($"Unexpected trailing content at position {pos}");
            return value;
        }

        private static void SkipWhitespace(string s, ref int pos)
        {
            while (pos < s.Length && char.IsWhiteSpace(s[pos])) pos++;
        }

        private static SC2JsonValue ParseValue(string s, ref int pos)
        {
            SkipWhitespace(s, ref pos);
            if (pos >= s.Length) throw new FormatException("Unexpected end of JSON");
            char c = s[pos];
            switch (c)
            {
                case '{': return ParseObject(s, ref pos);
                case '[': return ParseArray(s, ref pos);
                case '"': return Of(ParseStringLiteral(s, ref pos));
                case 't': Expect(s, ref pos, "true"); return Of(true);
                case 'f': Expect(s, ref pos, "false"); return Of(false);
                case 'n': Expect(s, ref pos, "null"); return Null;
                default: return ParseNumber(s, ref pos);
            }
        }

        private static void Expect(string s, ref int pos, string literal)
        {
            if (pos + literal.Length > s.Length || s.Substring(pos, literal.Length) != literal)
                throw new FormatException($"Expected '{literal}' at position {pos}");
            pos += literal.Length;
        }

        private static SC2JsonValue ParseObject(string s, ref int pos)
        {
            pos++; // {
            var result = NewObject();
            SkipWhitespace(s, ref pos);
            if (pos < s.Length && s[pos] == '}') { pos++; return result; }
            while (true)
            {
                SkipWhitespace(s, ref pos);
                string key = ParseStringLiteral(s, ref pos);
                SkipWhitespace(s, ref pos);
                if (pos >= s.Length || s[pos] != ':') throw new FormatException($"Expected ':' at position {pos}");
                pos++;
                var value = ParseValue(s, ref pos);
                result.Set(key, value);
                SkipWhitespace(s, ref pos);
                if (pos >= s.Length) throw new FormatException("Unexpected end of JSON in object");
                if (s[pos] == ',') { pos++; continue; }
                if (s[pos] == '}') { pos++; break; }
                throw new FormatException($"Expected ',' or '}}' at position {pos}");
            }
            return result;
        }

        private static SC2JsonValue ParseArray(string s, ref int pos)
        {
            pos++; // [
            var result = NewArray();
            SkipWhitespace(s, ref pos);
            if (pos < s.Length && s[pos] == ']') { pos++; return result; }
            while (true)
            {
                var value = ParseValue(s, ref pos);
                result.Add(value);
                SkipWhitespace(s, ref pos);
                if (pos >= s.Length) throw new FormatException("Unexpected end of JSON in array");
                if (s[pos] == ',') { pos++; continue; }
                if (s[pos] == ']') { pos++; break; }
                throw new FormatException($"Expected ',' or ']' at position {pos}");
            }
            return result;
        }

        private static string ParseStringLiteral(string s, ref int pos)
        {
            if (s[pos] != '"') throw new FormatException($"Expected '\"' at position {pos}");
            pos++;
            var sb = new StringBuilder();
            while (true)
            {
                if (pos >= s.Length) throw new FormatException("Unterminated string literal");
                char c = s[pos++];
                if (c == '"') break;
                if (c == '\\')
                {
                    if (pos >= s.Length) throw new FormatException("Unterminated escape sequence");
                    char esc = s[pos++];
                    switch (esc)
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
                            string hex = s.Substring(pos, 4);
                            pos += 4;
                            sb.Append((char)int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            break;
                        default: throw new FormatException($"Unknown escape '\\{esc}'");
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        private static SC2JsonValue ParseNumber(string s, ref int pos)
        {
            int start = pos;
            if (pos < s.Length && (s[pos] == '-' || s[pos] == '+')) pos++;
            while (pos < s.Length && (char.IsDigit(s[pos]) || s[pos] == '.' || s[pos] == 'e' || s[pos] == 'E' || s[pos] == '+' || s[pos] == '-'))
                pos++;
            string token = s.Substring(start, pos - start);
            if (token.Length == 0) throw new FormatException($"Expected number at position {pos}");
            return Of(double.Parse(token, NumberStyles.Float, CultureInfo.InvariantCulture));
        }
    }
}
