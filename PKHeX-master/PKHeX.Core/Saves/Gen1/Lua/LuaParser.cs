using System;
using System.Collections.Generic;
using System.Text;

namespace PKHeX.Core.Saves.Gen1.Lua;

/// <summary>
/// A parsed Lua table value: either a string, number, boolean, or nested LuaTable.
/// </summary>
public readonly struct LuaValue
{
    public enum Kind { String, Number, Boolean, Table }

    public Kind Type { get; init; }
    public string? StringValue { get; init; }
    public double NumberValue { get; init; }
    public bool BoolValue { get; init; }
    public LuaTable? TableValue { get; init; }

    private LuaValue(Kind type)
    {
        Type = type;
        StringValue = null;
        NumberValue = 0;
        BoolValue = false;
        TableValue = null;
    }

    public static LuaValue ForString(string value) => new(Kind.String) { StringValue = value };
    public static LuaValue ForNumber(double value) => new(Kind.Number) { NumberValue = value };
    public static LuaValue ForNumber(int value) => new(Kind.Number) { NumberValue = value };
    public static LuaValue ForBoolean(bool value) => new(Kind.Boolean) { BoolValue = value };
    public static LuaValue ForTable(LuaTable table) => new(Kind.Table) { TableValue = table };

    /// <summary>Serialize this value as a Lua expression string.</summary>
    public void Serialize(StringBuilder sb, int indent)
    {
        switch (Type)
        {
            case Kind.String:
                sb.Append('"').Append(Escape(StringValue ?? ""))
                    .Append('"');
                break;
            case Kind.Number:
                if (NumberValue == (int)NumberValue)
                    sb.Append(NumberValue);
                else
                    sb.Append(NumberValue);
                break;
            case Kind.Boolean:
                sb.Append(BoolValue ? "true" : "false");
                break;
            case Kind.Table:
                TableValue?.Serialize(sb, indent);
                break;
        }
    }

    private static string Escape(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}

/// <summary>
/// A parsed Lua table — an ordered collection of (key, value) pairs.
/// Keys are either bare identifiers or integer array indices.
/// </summary>
public sealed class LuaTable
{
    private readonly List<(string key, LuaValue value)> _entries = new();

    public void Add(string key, LuaValue value)
    {
        _entries.Add((key, value));
    }

    public bool TryGetValue(string key, out LuaValue value)
    {
        foreach (var (k, v) in _entries)
        {
            if (k == key)
            {
                value = v;
                return true;
            }
        }
        value = default;
        return false;
    }

    public LuaValue? GetValue(string key)
    {
        return TryGetValue(key, out var v) ? v : null;
    }

    public string? GetString(string key)
    {
        if (TryGetValue(key, out var v) && v.Type == LuaValue.Kind.String)
            return v.StringValue;
        return null;
    }

    public double GetNumber(string key, double defaultValue = 0)
    {
        if (TryGetValue(key, out var v) && v.Type == LuaValue.Kind.Number)
            return v.NumberValue;
        return defaultValue;
    }

    public int GetInt(string key, int defaultValue = 0) => (int)GetNumber(key, defaultValue);

    public bool GetBool(string key, bool defaultValue = false)
    {
        if (TryGetValue(key, out var v) && v.Type == LuaValue.Kind.Boolean)
            return v.BoolValue;
        return defaultValue;
    }

    public LuaTable? GetTable(string key)
    {
        if (TryGetValue(key, out var v) && v.Type == LuaValue.Kind.Table)
            return v.TableValue;
        return null;
    }

    public IReadOnlyList<(string key, LuaValue value)> Entries => _entries;

    public IEnumerable<LuaTable> GetArrayEntries()
    {
        foreach (var (key, value) in _entries)
        {
            if (value.Type == LuaValue.Kind.Table && value.TableValue != null)
                yield return value.TableValue;
        }
    }

    public IEnumerable<LuaTable> GetNamedTableEntries()
    {
        foreach (var (key, value) in _entries)
        {
            if (key.StartsWith("[") && value.Type == LuaValue.Kind.Table && value.TableValue != null)
                yield return value.TableValue;
        }
    }

    /// <summary>Set or replace a string key's value.</summary>
    public void Set(string key, LuaValue value)
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].key == key)
            {
                _entries[i] = (key, value);
                return;
            }
        }
        _entries.Add((key, value));
    }

    /// <summary>Set or replace an array index entry.</summary>
    public void SetArray(int index, LuaValue value)
    {
        var key = "[" + index + "]";
        Set(key, value);
    }

    /// <summary>Remove all entries.</summary>
    public void Clear() => _entries.Clear();

    /// <summary>Serialize this table as Lua text.</summary>
    public void Serialize(StringBuilder sb, int indent)
    {
        var pad = new string(' ', indent);
        var padInner = new string(' ', indent + 2);
        sb.AppendLine("{");
        foreach (var (key, value) in _entries)
        {
            if (string.IsNullOrEmpty(key))
                continue;
            if (key.StartsWith("[") && key.EndsWith("]"))
                sb.Append(padInner).Append(key).Append(" = ");
            else
                sb.Append(padInner).Append(key).Append(" = ");
            value.Serialize(sb, indent + 2);
            sb.AppendLine(",");
        }
        sb.Append(pad).Append("}");
    }

    /// <summary>Serialize as a full Lua module (return { ... }).</summary>
    public string Serialize()
    {
        var sb = new StringBuilder();
        sb.Append("return ");
        Serialize(sb, 0);
        return sb.ToString();
    }
}

/// <summary>
/// Minimal Lua table parser — handles the subset used by "Recomp" save files:
/// `return { key = value, [n] = value, "str", number, true, false, nested {} }`
/// No functions, no metatables, no blocks/expressions beyond table literals.
/// </summary>
public static class LuaParser
{
    public static LuaTable Parse(string text)
    {
        var reader = new LuaReader(text);
        reader.SkipWhitespaceAndComments();
        if (reader.MatchKeyword("return"))
            reader.SkipWhitespaceAndComments();
        else
            throw new FormatException("Lua save must start with 'return'");
        var table = ParseTable(reader);
        return table;
    }

    private static LuaTable ParseTable(LuaReader reader)
    {
        if (reader.Current != '{')
            throw new FormatException($"Expected '{{', got '{reader.Current}'");
        reader.Advance(); // consume {
        var table = new LuaTable();

        while (true)
        {
            reader.SkipWhitespaceAndComments();
            if (reader.Current == '}')
            {
                reader.Advance();
                break;
            }

            string? key;
            // Check for [index] = key
            if (reader.Current == '[')
            {
                reader.Advance(); // consume [
                var numStr = reader.ReadNumberToken();
                key = "[" + numStr + "]";
                reader.SkipWhitespaceAndComments();
                if (reader.Current != ']')
                    throw new FormatException("Expected ']' after array index");
                reader.Advance(); // consume ]
                reader.SkipWhitespaceAndComments();
                if (reader.Current != '=')
                    throw new FormatException("Expected '=' after key");
                reader.Advance(); // consume =
            }
            else if (char.IsLetter(reader.Current) || reader.Current == '_')
            {
                key = reader.ReadIdentifier();
                reader.SkipWhitespaceAndComments();
                if (reader.Current != '=')
                    throw new FormatException($"Expected '=' after key '{key}', got '{reader.Current}'");
                reader.Advance(); // consume =
            }
            else
            {
                // Could be a table-only entry (array style without key)
                key = null;
            }

            reader.SkipWhitespaceAndComments();
            var value = ParseValue(reader);

            if (key != null)
                table.Add(key, value);
            else if (value.Type == LuaValue.Kind.Table && value.TableValue != null)
                table.Add("[]", LuaValue.ForTable(value.TableValue));

            // Skip optional comma separator between entries
            reader.SkipWhitespaceAndComments();
            if (reader.Current == ',')
                reader.Advance();
        }

        return table;
    }

    private static LuaValue ParseValue(LuaReader reader)
    {
        reader.SkipWhitespaceAndComments();
        char c = reader.Current;

        if (c == '"')
            return LuaValue.ForString(reader.ReadString());

        if (c == '{')
            return LuaValue.ForTable(ParseTable(reader));

        if (reader.MatchKeyword("true"))
            return LuaValue.ForBoolean(true);
        if (reader.MatchKeyword("false"))
            return LuaValue.ForBoolean(false);

        if (c == '-' && reader.Peek == '-')
        {
            // shouldn't reach here since SkipWhitespaceAndComments handles it, but safety
            reader.SkipWhitespaceAndComments();
            return ParseValue(reader);
        }

        if (c == '-' || char.IsDigit(c) || c == '.')
            return LuaValue.ForNumber(double.Parse(reader.ReadNumberToken()));

        throw new FormatException($"Unexpected character '{c}' while parsing Lua value");
    }

    /// <summary>
    /// Minimal Lua reader for table parsing.
    /// </summary>
    private sealed class LuaReader
    {
        private readonly string _text;
        private int _pos;

        public LuaReader(string text)
        {
            _text = text;
            _pos = 0;
        }

        public char Current => _pos < _text.Length ? _text[_pos] : '\0';
        public char Peek => _pos + 1 < _text.Length ? _text[_pos + 1] : '\0';

        public void Advance() => _pos++;

        public void SkipWhitespaceAndComments()
        {
            while (_pos < _text.Length)
            {
                if (char.IsWhiteSpace(_text[_pos]))
                {
                    _pos++;
                    continue;
                }

                // Line comment: -- ...
                if (_text[_pos] == '-' && _pos + 1 < _text.Length && _text[_pos + 1] == '-')
                {
                    _pos += 2;
                    // Check for block comment: --[[
                    if (_pos + 1 < _text.Length && _text[_pos] == '[' && _text[_pos + 1] == '[')
                    {
                        _pos += 2;
                        var close = _text.IndexOf("]]", _pos);
                        _pos = close == -1 ? _text.Length : close + 2;
                    }
                    else
                    {
                        while (_pos < _text.Length && _text[_pos] != '\n')
                            _pos++;
                    }
                    continue;
                }

                break;
            }
        }

        public bool MatchKeyword(string keyword)
        {
            if (_pos + keyword.Length > _text.Length)
                return false;
            if (_text.AsSpan(_pos, keyword.Length).Equals(keyword.AsSpan(), StringComparison.Ordinal))
            {
                _pos += keyword.Length;
                return true;
            }
            return false;
        }

        public string ReadIdentifier()
        {
            var start = _pos;
            while (_pos < _text.Length && (char.IsLetterOrDigit(_text[_pos]) || _text[_pos] == '_'))
                _pos++;
            return _text.Substring(start, _pos - start);
        }

        public string ReadNumberToken()
        {
            var start = _pos;
            while (_pos < _text.Length && IsNumberChar(_text[_pos]))
                _pos++;
            return _text.Substring(start, _pos - start);
        }

        private static bool IsNumberChar(char c) => char.IsDigit(c) || c == '.' || c == '-' || c == '+' || c == 'e' || c == 'E';

        public string ReadString()
        {
            if (_text[_pos] != '"')
                throw new FormatException("Expected '\"'");
            _pos++; // consume opening quote

            var sb = new StringBuilder();
            while (_pos < _text.Length)
            {
                char c = _text[_pos++];
                if (c == '"')
                    break;
                if (c == '\\')
                {
                    if (_pos >= _text.Length)
                        break;
                    char next = _text[_pos++];
                    sb.Append(next switch
                    {
                        '"' => '"',
                        '\\' => '\\',
                        'n' => '\n',
                        't' => '\t',
                        '\'' => '\'',
                        'r' => '\r',
                        _ => next,
                    });
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }
    }
}
