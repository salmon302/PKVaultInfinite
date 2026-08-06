using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;

// NOTE: namespace is deliberately NOT `PKHeX.Core.InfiniteFusion`; that would collide with the
// `SaveFileType.InfiniteFusion` enum member in files that `using static PKHeX.Core.SaveFileType;`.
namespace PKHeX.Core.Ruby;

/// <summary>
/// Minimal Ruby Marshal (v4.8, RGSS / Pokémon Essentials) deserializer.
/// Produces a generic object graph (<see cref="RbValue"/>) independent of PKHeX entity types.
/// </summary>
public static class RubyMarshal
{
    public static RbValue Load(ReadOnlySpan<byte> data)
    {
        var reader = new Reader(data.ToArray());
        return reader.ReadRoot();
    }

    private sealed class Reader(byte[] data)
    {
        private readonly byte[] _data = data;
        private int _pos;
        // Objects and symbols use SEPARATE link tables, exactly like Ruby Marshal's
        // `arg->links` (objects) and `arg->symbols` (symbols) lists.
        private readonly List<RbValue?> _links = [];
        private readonly List<RbSymbol> _symbols = [];

        public RbValue ReadRoot()
        {
            if (_data[0] != 0x04 || _data[1] != 0x08)
                throw new InvalidDataException("Not a Ruby Marshal stream (bad magic).");
            _pos = 2;
            var root = ReadObject();
            return root;
        }

        private byte Peek() => _data[_pos];
        private byte ReadByte() => _data[_pos++];
        private ReadOnlySpan<byte> ReadBytes(int n) { var s = _data.AsSpan(_pos, n); _pos += n; return s; }

        // Port of Ruby Marshal's w_long / r_long (matches rubymarshal reference implementation).
        private long ReadLong()
        {
            int length = ReadSByte();
            if (length == 0)
                return 0;
            if (length > 5 && length < 128)
                return length - 5;
            if (length > -129 && length < -5)
                return length + 5;
            long result = 0;
            long factor = 1;
            int count = Math.Abs(length);
            for (int s = 0; s < count; s++)
            {
                result += ReadUByte() * factor;
                factor *= 256;
            }
            if (length < 0)
                result -= factor;
            return result;
        }

        private sbyte ReadSByte() => (sbyte)_data[_pos++];
        private int ReadUByte() => _data[_pos++];

        private RbValue ReadObject()
        {
            byte t = ReadByte();
            if (t == TYPE_LINK)
            {
                long idx = ReadLong();
                // RGSS marshal link indices appear to be 1-based (outermost object not counted),
                // so try the literal index first, then fall back to index-1.
                RbValue? link = (idx >= 0 && idx < _links.Count) ? _links[(int)idx] : null;
                if (link is null && idx - 1 >= 0 && idx - 1 < _links.Count)
                    link = _links[(int)idx - 1];
                return link ?? throw new InvalidDataException($"Dangling link {idx}");
            }

            // Linkable objects get a placeholder BEFORE reading (mirrors Ruby's arg->links slot),
            // so nested objects occupy the correct link index. Symbols/ivars/links are not linkable.
            int objectIndex = -1;
            if (IsLinkable(t))
            {
                objectIndex = _links.Count;
                _links.Add(null);
            }

            RbValue result = t switch
            {
                TYPE_NIL => RbNil.Instance,
                TYPE_TRUE => new RbBool(true),
                TYPE_FALSE => new RbBool(false),
                TYPE_FIXNUM => new RbFixnum(ReadLong()),
                TYPE_BIGNUM => ReadBignum(),
                TYPE_FLOAT => ReadFloat(),
                TYPE_SYMBOL => ReadSymbol(),
                TYPE_SYMLINK => ReadSymbolLink(),
                TYPE_STRING => ReadString(),
                TYPE_REGEXP => ReadRegexp(),
                TYPE_ARRAY => ReadArray(objectIndex),
                TYPE_HASH => ReadHash(objectIndex),
                TYPE_OBJECT => ReadObjectInstance(objectIndex),
                TYPE_STRUCT => ReadStruct(objectIndex),
                TYPE_CLASS => ReadClass(),
                TYPE_MODULE => ReadModule(),
                TYPE_IVAR => ReadIvar(),
                TYPE_USERDEF => ReadUserDef(),
                TYPE_USRMARSHAL => ReadUsrMarshal(),
                _ => ReadSmallFixnum((sbyte)t),
            };

            if (objectIndex >= 0)
                _links[objectIndex] = result;
            return result;
        }

        private static bool IsLinkable(byte t) => t switch
        {
            TYPE_NIL or TYPE_TRUE or TYPE_FALSE or TYPE_IVAR or TYPE_SYMBOL or TYPE_SYMLINK or TYPE_LINK or TYPE_FIXNUM => false,
            _ => true,
        };

        private RbValue ReadSmallFixnum(int b)
        {
            // Immediate fixnum form (same encoding as read_long small ranges); b is the signed byte value.
            if (b == 0)
                return new RbFixnum(0);
            if (b > 5 && b < 128)
                return new RbFixnum(b - 5);
            if (b > -129 && b < -5)
                return new RbFixnum(b + 5);
            return new RbFixnum(b);
        }

        private RbValue ReadBignum()
        {
            byte sign = ReadByte();
            int words = (int)ReadLong();
            BigInteger value = 0;
            BigInteger factor = 1;
            for (int i = 0; i < words; i++)
            {
                int lo = ReadByte();
                int hi = ReadByte();
                value += (lo | (hi << 8)) * factor;
                factor *= 1 << 16;
            }
            if (sign == '-')
                value = -value;
            return new RbBignum(value);
        }

        private RbValue ReadFloat()
        {
            int len = (int)ReadLong();
            var bytes = ReadBytes(len);
            var text = Encoding.ASCII.GetString(bytes).TrimEnd('\0');
            if (text is "nan" or "Infinity" or "-Infinity")
                return new RbFloat(double.NaN);
            return new RbFloat(double.Parse(text, System.Globalization.CultureInfo.InvariantCulture));
        }

        private RbValue ReadSymbol()
        {
            int len = (int)ReadLong();
            var name = Encoding.UTF8.GetString(ReadBytes(len));
            var sym = new RbSymbol(name);
            _symbols.Add(sym);
            return sym;
        }

        private RbValue ReadSymbolLink()
        {
            long idx = ReadLong();
            return _symbols[(int)idx];
        }

        private RbValue ReadString()
        {
            int len = (int)ReadLong();
            var bytes = ReadBytes(len).ToArray();
            var enc = DetectEncoding(bytes);
            return new RbString(bytes, enc);
        }

        private static string DetectEncoding(ReadOnlySpan<byte> bytes)
        {
            // Simple heuristic: ASCII / UTF-8 when printable, else binary.
            bool printable = true;
            foreach (byte b in bytes)
            {
                if (b < 0x09 || (b > 0x0D && b < 0x20) || b == 0x7F)
                {
                    printable = false;
                    break;
                }
            }
            return printable ? "UTF-8" : "ASCII-8BIT";
        }

        private RbValue ReadRegexp()
        {
            int len = (int)ReadLong();
            var bytes = ReadBytes(len).ToArray();
            ReadByte(); // options
            return new RbRegexp(Encoding.UTF8.GetString(bytes));
        }

        private RbValue ReadArray(int objectIndex)
        {
            long count = ReadLong();
            var items = new List<RbValue?>();
            var arr = new RbArray(items);
            if (objectIndex >= 0)
                _links[objectIndex] = arr; // pre-store so forward links resolve to the live object
            for (long i = 0; i < count; i++)
                items.Add(ReadObject());
            return arr;
        }

        private RbValue ReadHash(int objectIndex)
        {
            long count = ReadLong();
            var pairs = new List<KeyValuePair<RbValue, RbValue?>>();
            var hash = new RbHash(pairs, null);
            if (objectIndex >= 0)
                _links[objectIndex] = hash;
            for (long i = 0; i < count; i++)
            {
                var key = ReadObject();
                var val = ReadObject();
                pairs.Add(new KeyValuePair<RbValue, RbValue?>(key, val));
            }
            return hash;
        }

        private RbValue ReadObjectInstance(int objectIndex)
        {
            var className = (RbSymbol)ReadObject();
            long count = ReadLong();
            var ivars = new Dictionary<RbValue, RbValue?>();
            var obj = new RbObject(className, ivars);
            if (objectIndex >= 0)
                _links[objectIndex] = obj;
            for (long i = 0; i < count; i++)
            {
                var key = ReadObject();
                var val = ReadObject();
                ivars[key] = val;
            }
            return obj;
        }

        private RbValue ReadStruct(int objectIndex)
        {
            var className = (RbSymbol)ReadObject();
            long count = ReadLong();
            var ivars = new Dictionary<RbValue, RbValue?>();
            var obj = new RbStruct(className, ivars);
            if (objectIndex >= 0)
                _links[objectIndex] = obj;
            for (long i = 0; i < count; i++)
            {
                var key = ReadObject();
                var val = ReadObject();
                ivars[key] = val;
            }
            return obj;
        }

        private RbValue ReadClass() => new RbClass(Encoding.UTF8.GetString(ReadBytes((int)ReadLong())));
        private RbValue ReadModule() => new RbModule(Encoding.UTF8.GetString(ReadBytes((int)ReadLong())));

        private RbValue ReadIvar()
        {
            var inner = ReadObject();
            long count = ReadLong();
            for (long i = 0; i < count; i++)
            {
                var key = ReadObject();
                var val = ReadObject();
                if (inner is RbString s && key is RbSymbol ks && ks.Name == "E")
                    s.EncodingName = val switch
                    {
                        RbString rs => rs.Text,
                        RbSymbol sym => sym.Name,
                        _ => "UTF-8",
                    };
                else
                    inner.SetIvar(key, val);
            }
            return inner;
        }

        private RbValue ReadUserDef()
        {
            var className = (RbSymbol)ReadObject();
            int len = (int)ReadLong();
            var bytes = ReadBytes(len).ToArray();
            return new RbUserDef(className, bytes);
        }

        private RbValue ReadUsrMarshal()
        {
            var className = (RbSymbol)ReadObject();
            var data = ReadObject();
            return new RbUsrMarshal(className, data);
        }
    }

    private const byte TYPE_NIL = (byte)'0';
    private const byte TYPE_TRUE = (byte)'T';
    private const byte TYPE_FALSE = (byte)'F';
    private const byte TYPE_FIXNUM = (byte)'i';
    private const byte TYPE_BIGNUM = (byte)'l';
    private const byte TYPE_FLOAT = (byte)'f';
    private const byte TYPE_SYMBOL = (byte)':';
    private const byte TYPE_SYMLINK = (byte)';';
    private const byte TYPE_STRING = (byte)'"';
    private const byte TYPE_REGEXP = (byte)'/';
    private const byte TYPE_ARRAY = (byte)'[';
    private const byte TYPE_HASH = (byte)'{';
    private const byte TYPE_OBJECT = (byte)'o';
    private const byte TYPE_STRUCT = (byte)'S';
    private const byte TYPE_CLASS = (byte)'c';
    private const byte TYPE_MODULE = (byte)'m';
    private const byte TYPE_IVAR = (byte)'I';
    private const byte TYPE_LINK = (byte)'@';
    private const byte TYPE_USERDEF = (byte)'u';
    private const byte TYPE_USRMARSHAL = (byte)'U';
}

/// <summary>Base node for a deserialized Ruby Marshal object graph.</summary>
public abstract class RbValue
{
    public Dictionary<RbValue, RbValue?>? IVars { get; private set; }

    public void SetIvar(RbValue key, RbValue? value)
    {
        IVars ??= new Dictionary<RbValue, RbValue?>();
        IVars[key] = value;
    }

    public RbValue? GetIvar(string name) =>
        IVars?.FirstOrDefault(kv => kv.Key is RbSymbol s && s.Name == name
                                 || kv.Key is RbString st && st.Text == name).Value;
}

public sealed class RbNil : RbValue
{
    public static readonly RbNil Instance = new();
    public override string ToString() => "nil";
}

public sealed class RbBool(bool value) : RbValue
{
    public bool Value { get; } = value;
    public override string ToString() => Value ? "true" : "false";
}

public sealed class RbFixnum(long value) : RbValue
{
    public long Value { get; } = value;
    public override string ToString() => Value.ToString();
    public override bool Equals(object? obj) => obj is RbFixnum f && f.Value == Value;
    public override int GetHashCode() => Value.GetHashCode();
}

public sealed class RbBignum(BigInteger value) : RbValue
{
    public BigInteger Value { get; } = value;
    public override string ToString() => Value.ToString();
}

public sealed class RbFloat(double value) : RbValue
{
    public double Value { get; } = value;
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public sealed class RbSymbol(string name) : RbValue
{
    public string Name { get; } = name;
    public override string ToString() => ":" + Name;
    public override bool Equals(object? obj) => obj is RbSymbol s && s.Name == Name;
    public override int GetHashCode() => Name.GetHashCode();
}

public sealed class RbString(byte[] bytes, string encoding) : RbValue
{
    public byte[] Bytes { get; } = bytes;
    public string EncodingName { get; set; } = encoding;
    public string Text => Encoding.UTF8.GetString(Bytes);
    public override string ToString() => $"\"{Text}\"";
    public override bool Equals(object? obj) => obj is RbString s && s.Text == Text;
    public override int GetHashCode() => Text.GetHashCode();
}

public sealed class RbRegexp(string pattern) : RbValue
{
    public string Pattern { get; } = pattern;
    public override string ToString() => $"/{Pattern}/";
}

public sealed class RbArray(List<RbValue?> items) : RbValue
{
    public List<RbValue?> Items { get; } = items;
    public int Count => Items.Count;
    public RbValue? this[int i] => Items[i];
    public override string ToString() => $"Array({Items.Count})";
}

public sealed class RbHash(List<KeyValuePair<RbValue, RbValue?>> pairs, RbValue? defaultValue) : RbValue
{
    public List<KeyValuePair<RbValue, RbValue?>> Pairs { get; } = pairs;
    public RbValue? DefaultValue { get; } = defaultValue;

    public RbValue? this[RbValue key] =>
        Pairs.FirstOrDefault(p => Equals(p.Key, key)).Value;

    public RbValue? this[string key] =>
        Pairs.FirstOrDefault(p => p.Key is RbSymbol s && s.Name == key).Value;

    private static bool Equals(RbValue a, RbValue b) =>
        a.Equals(b);

    public override string ToString() => $"Hash({Pairs.Count})";
}

public sealed class RbObject(RbSymbol className, Dictionary<RbValue, RbValue?> ivars) : RbValue
{
    public RbSymbol ClassName { get; } = className;
    public Dictionary<RbValue, RbValue?> IVariables { get; } = ivars;
    public RbValue? this[string ivar] => IVariables.FirstOrDefault(kv =>
        (kv.Key is RbSymbol s && s.Name == ivar) || (kv.Key is RbString st && st.Text == ivar)).Value;
    public override string ToString() => $"#{ClassName.Name}";
}

public sealed class RbStruct(RbSymbol className, Dictionary<RbValue, RbValue?> ivars) : RbValue
{
    public RbSymbol ClassName { get; } = className;
    public Dictionary<RbValue, RbValue?> Members { get; } = ivars;
    public RbValue? this[string member] => Members.FirstOrDefault(kv =>
        (kv.Key is RbSymbol s && s.Name == member) || (kv.Key is RbString st && st.Text == member)).Value;
    public override string ToString() => $"Struct(#{ClassName.Name})";
}

public sealed class RbClass(string name) : RbValue
{
    public string Name { get; } = name;
    public override string ToString() => $"class {Name}";
}

public sealed class RbModule(string name) : RbValue
{
    public string Name { get; } = name;
    public override string ToString() => $"module {Name}";
}

public sealed class RbUserDef(RbSymbol className, byte[] data) : RbValue
{
    public RbSymbol ClassName { get; } = className;
    public byte[] Data { get; } = data;
    public override string ToString() => $"UserDef(#{ClassName.Name})";
}

public sealed class RbUsrMarshal(RbSymbol className, RbValue data) : RbValue
{
    public RbSymbol ClassName { get; } = className;
    public RbValue Data { get; } = data;
    public override string ToString() => $"UsrMarshal(#{ClassName.Name})";
}
