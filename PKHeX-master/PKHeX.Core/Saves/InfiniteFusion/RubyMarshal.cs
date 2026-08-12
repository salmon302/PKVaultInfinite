using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
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


    /// <summary>
    /// Re-emits a Ruby Marshal (v4.8 / RGSS) object graph produced by <see cref="Load"/>.
    /// The output is a self-consistent stream (no link de-duplication) that round-trips through
    /// <see cref="Load"/> and is loadable by RGSS. Object identity / byte layout of the original
    /// stream is intentionally not preserved — only the logical graph is.
    /// </summary>
    public static byte[] Save(RbValue root)
    {
        var stream = new MemoryStream();
        stream.WriteByte(0x04);
        stream.WriteByte(0x08);
        new Writer(stream).WriteObject(root);
        return stream.ToArray();
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
                // Ruby Marshal object-link indices are 0-based: the first linkable object stored
                // occupies slot 0, so a back-reference `idx` resolves directly to _links[idx].
                if (idx >= 0 && idx < _links.Count)
                {
                    var resolved = _links[(int)idx]!;
                    return resolved;
                }
                throw new InvalidDataException($"Dangling link {idx}");
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

        // Only the 12 object types that carry a real link slot are linkable. Crucially, small
        // fixnums are encoded as raw bytes that fall into the `default`/ReadSmallFixnum path; they
        // must NOT reserve a link slot (the writer never does), otherwise every fixnum shifts the
        // link-slot index and back-references resolve to the wrong objects.
        private static bool IsLinkable(byte t) => t switch
        {
            TYPE_BIGNUM or TYPE_FLOAT or TYPE_STRING or TYPE_REGEXP or TYPE_ARRAY or TYPE_HASH
                or TYPE_OBJECT or TYPE_STRUCT or TYPE_CLASS or TYPE_MODULE or TYPE_USERDEF or TYPE_USRMARSHAL => true,
            _ => false,
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
            if (idx >= 0 && idx < _symbols.Count)
                return _symbols[(int)idx];
            throw new InvalidDataException($"Dangling symbol link {idx}");
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
            // Ruby 1.8/RGSS encodes a hash *with* a default value as a negative count:
            // |count| - 1 is the pair count, and a trailing default object follows the pairs.
            RbValue? defaultValue = null;
            if (count < 0)
            {
                count = -count - 1;
                defaultValue = ReadObject();
            }
            var pairs = new List<KeyValuePair<RbValue, RbValue?>>();
            var hash = new RbHash(pairs, defaultValue);
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
            var className = ReadObject();
            if (className is not RbSymbol symClass)
                throw new InvalidDataException($"struct class not a symbol: {className?.GetType().Name} at pos {_pos}");
            className = symClass;
            long count = ReadLong();
            var ivars = new Dictionary<RbValue, RbValue?>();
            var obj = new RbStruct(symClass, ivars);
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
                // `E` (encoding) and any other ivar are stored generically on the value so the
                // writer can re-emit them via the TYPE_IVAR wrapper. The reader still mirrors
                // `E` onto EncodingName for callers that inspect it.
                if (inner is RbString s && key is RbSymbol ks && ks.Name == "E")
                    s.EncodingName = val switch
                    {
                        RbString rs => rs.Text,
                        RbSymbol sym => sym.Name,
                        _ => "UTF-8",
                    };
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

    private sealed class Writer(Stream stream)
    {
        // RGSS Marshal omits the default-value terminator byte for hashes (unlike vanilla Ruby),
        // so WriteHash mirrors ReadHash and emits count + pairs only.
        // Link tables make the output self-consistent and let cyclic graphs (common in RGSS saves)
        // serialize without overflow. Indices are assigned in first-write order, matching the reader.
        private readonly Dictionary<string, int> _symbolLinks = new();
        private readonly Dictionary<RbValue, int> _objectLinks = new(ReferenceEqualityComparer.Instance);

        /// <summary>
        /// Link-dedup strategy: every node type (including strings and symbols) is deduplicated by
        /// reference identity. Sharing therefore reflects only what the loaded graph actually shares,
        /// which — given the reader interns equal strings/symbols — is exactly the sharing present in
        /// the source stream. This makes the writer reproduce Ruby's own link decisions and keeps the
        /// marshal round-trip idempotent: Save(Load(x)) byte-matches Save(Load(Save(Load(x)))).
        /// </summary>

        public void WriteObject(RbValue? value)
        {
            switch (value)
            {
                case null:
                    stream.WriteByte(TYPE_NIL);
                    return;
                case RbNil nil:
                    WriteScalar(nil);
                    return;
                case RbBool b:
                    WriteScalar(b);
                    return;
                case RbFixnum f:
                    WriteScalar(f);
                    return;
                case RbSymbol sym:
                    WriteScalar(sym);
                    return;
                default:
                    WriteLinkable(value!);
                    return;
            }
        }

        // Emits a (possibly ivar-wrapped) value that never occupies a link slot: nil, bool,
        // fixnum, symbol. These are cached/deduplicated by value in the reader, so they cannot
        // participate in the object link table, but Ruby still permits them to carry generic
        // instance variables via a TYPE_IVAR wrapper — and real RGSS saves do exactly that.
        private void WriteScalar(RbValue v)
        {
            if (v.IVars is { Count: > 0 } ivars)
            {
                stream.WriteByte(TYPE_IVAR);
                WriteRawValue(v);
                WriteLong(ivars.Count);
                foreach (var kv in ivars)
                {
                    WriteObject(kv.Key);
                    WriteObject(kv.Value);
                }
                return;
            }
            WriteRawValue(v);
        }

        private void WriteLinkable(RbValue v)
        {
            // Linkable object: emit a back-reference if already written, else reserve a link
            // slot (before writing children) so cycles serialize without infinite recursion.
            if (_objectLinks.TryGetValue(v, out int idx))
            {
                stream.WriteByte(TYPE_LINK);
                WriteLong(idx); // 0-based link slot, matching the reader's _links[idx] resolution
                return;
            }
            _objectLinks[v] = _objectLinks.Count;
            // Any value carrying generic ivars must be wrapped in TYPE_IVAR so the reader can
            // re-attach them — including RbObject / RbStruct, which also encode their own ivars
            // inline via TYPE_OBJECT / TYPE_STRUCT. Dropping generic ivars makes the round-trip lossy.
            if (v.IVars is { Count: > 0 } ivars)
            {
                stream.WriteByte(TYPE_IVAR);
                WriteRawValue(v);
                WriteLong(ivars.Count);
                foreach (var kv in ivars)
                {
                    WriteObject(kv.Key);
                    WriteObject(kv.Value);
                }
                return;
            }
            WriteRawValue(v);
        }

        private void WriteRawValue(RbValue v)
        {
            switch (v)
            {
                case RbString s:
                    WriteString(s);
                    break;
                case RbRegexp r:
                    WriteRegexp(r);
                    break;
                case RbArray a:
                    WriteArray(a);
                    break;
                case RbHash h:
                    WriteHash(h);
                    break;
                case RbObject o:
                    WriteObjectInstance(o);
                    break;
                case RbStruct st:
                    WriteStruct(st);
                    break;
                case RbBignum bi:
                    WriteBignum(bi);
                    break;
                case RbFloat fl:
                    WriteFloat(fl);
                    break;
                case RbClass c:
                    stream.WriteByte(TYPE_CLASS);
                    WriteSharedBinary(Encoding.UTF8.GetBytes(c.Name));
                    break;
                case RbModule m:
                    stream.WriteByte(TYPE_MODULE);
                    WriteSharedBinary(Encoding.UTF8.GetBytes(m.Name));
                    break;
                case RbUserDef u:
                    stream.WriteByte(TYPE_USERDEF);
                    WriteObject(u.ClassName);
                    WriteSharedBinary(u.Data);
                    break;
                case RbUsrMarshal um:
                    stream.WriteByte(TYPE_USRMARSHAL);
                    WriteObject(um.ClassName);
                    WriteObject(um.Data);
                    break;
                case RbBool b:
                    stream.WriteByte(b.Value ? TYPE_TRUE : TYPE_FALSE);
                    break;
                case RbFixnum f:
                    WriteFixnum(f.Value);
                    break;
                case RbSymbol sym:
                    WriteSymbol(sym);
                    break;
                default:
                    stream.WriteByte(TYPE_NIL);
                    break;
            }
        }

        private void WriteFixnum(long x)
        {
            if (x == 0) { stream.WriteByte(0); return; }
            if (x > 0 && x < 123)
            {
                byte b = (byte)(x + 5);
                if (!TypeMarkers.Contains(b)) { stream.WriteByte(b); return; }
            }
            if (x < 0 && x > -124)
            {
                byte b = (byte)(x - 5);
                if (!TypeMarkers.Contains(b)) { stream.WriteByte(b); return; }
            }
            stream.WriteByte((byte)'i');
            WriteLong(x);
        }

        private static readonly HashSet<byte> TypeMarkers = new()
        {
            TYPE_NIL, TYPE_TRUE, TYPE_FALSE, TYPE_FIXNUM, TYPE_BIGNUM, TYPE_FLOAT, TYPE_SYMBOL,
            TYPE_SYMLINK, TYPE_STRING, TYPE_REGEXP, TYPE_ARRAY, TYPE_HASH, TYPE_OBJECT, TYPE_STRUCT,
            TYPE_CLASS, TYPE_MODULE, TYPE_IVAR, TYPE_LINK, TYPE_USERDEF, TYPE_USRMARSHAL,
        };

        private void WriteLong(long x)
        {
            if (x == 0) { stream.WriteByte(0); return; }
            if (x > 0 && x < 123) { stream.WriteByte((byte)(x + 5)); return; }
            if (x < 0 && x > -124) { stream.WriteByte((byte)(x - 5)); return; }
            bool neg = x < 0;
            ulong mag = neg ? (ulong)(-x) : (ulong)x;
            int n = 0;
            for (ulong t = mag; t > 0; t >>= 8) n++;
            if (n == 0) n = 1;
            // Ruby marshal stores the magnitude as its two's-complement in n bytes for negatives,
            // so a negative value `-X` is written as `2^(8n) - X` (little-endian), not raw `X`.
            ulong encoded = neg ? ((1UL << (8 * n)) - mag) : mag;
            stream.WriteByte((byte)(neg ? -n : n));
            for (int i = 0; i < n; i++)
                stream.WriteByte((byte)(encoded >> (8 * i)));
        }

        private void WriteBignum(RbBignum b)
        {
            stream.WriteByte(TYPE_BIGNUM);
            stream.WriteByte((byte)(b.Value.Sign < 0 ? '-' : '+'));
            var words = new List<byte>();
            var m = BigInteger.Abs(b.Value);
            if (m == 0)
            {
                words.Add(0); words.Add(0);
            }
            else
            {
                while (m > 0)
                {
                    ushort w = (ushort)(m & 0xFFFF);
                    words.Add((byte)(w & 0xFF));
                    words.Add((byte)((w >> 8) & 0xFF));
                    m >>= 16;
                }
            }
            WriteLong(words.Count / 2);
            foreach (var by in words)
                stream.WriteByte(by);
        }

        private void WriteFloat(RbFloat f)
        {
            stream.WriteByte(TYPE_FLOAT);
            var text = double.IsNaN(f.Value) ? "NaN"
                : double.IsPositiveInfinity(f.Value) ? "Infinity"
                : double.IsNegativeInfinity(f.Value) ? "-Infinity"
                : f.Value.ToString("R", CultureInfo.InvariantCulture);
            WriteSharedBinary(Encoding.ASCII.GetBytes(text));
        }

        private void WriteSymbol(RbSymbol sym)
        {
            if (_symbolLinks.TryGetValue(sym.Name, out int idx))
            {
                stream.WriteByte(TYPE_SYMLINK);
                WriteLong(idx); // symbols are 0-based in this stream
                return;
            }
            _symbolLinks[sym.Name] = _symbolLinks.Count;
            stream.WriteByte(TYPE_SYMBOL);
            WriteSharedBinary(Encoding.UTF8.GetBytes(sym.Name));
        }

        private void WriteSharedBinary(byte[] data)
        {
            WriteLong(data.Length);
            stream.Write(data, 0, data.Length);
        }

        private void WriteString(RbString s)
        {
            // Generic ivars (if any) are emitted by the TYPE_IVAR wrapper in WriteLinkable.
            WriteStringBody(s);
        }

        private void WriteStringBody(RbString s)
        {
            stream.WriteByte(TYPE_STRING);
            WriteSharedBinary(s.Bytes);
        }

        private void WriteRegexp(RbRegexp r)
        {
            stream.WriteByte(TYPE_REGEXP);
            WriteSharedBinary(Encoding.UTF8.GetBytes(r.Pattern));
            stream.WriteByte(0); // options
        }

        private void WriteArray(RbArray a)
        {
            stream.WriteByte(TYPE_ARRAY);
            WriteLong(a.Items.Count);
            foreach (var item in a.Items)
                WriteObject(item);
        }

        private void WriteHash(RbHash h)
        {
            stream.WriteByte(TYPE_HASH);
            if (h.DefaultValue is not null)
            {
                // Match Ruby 1.8/RGSS: a negative count signals a trailing default value.
                WriteLong(-(h.Pairs.Count + 1));
                foreach (var kv in h.Pairs)
                {
                    WriteObject(kv.Key);
                    WriteObject(kv.Value);
                }
                WriteObject(h.DefaultValue);
            }
            else
            {
                WriteLong(h.Pairs.Count);
                foreach (var kv in h.Pairs)
                {
                    WriteObject(kv.Key);
                    WriteObject(kv.Value);
                }
            }
        }

        private void WriteObjectInstance(RbObject o)
        {
            stream.WriteByte(TYPE_OBJECT);
            WriteObject(o.ClassName);
            WriteLong(o.IVariables.Count);
            foreach (var kv in o.IVariables)
            {
                WriteObject(kv.Key);
                WriteObject(kv.Value);
            }
        }

        private void WriteStruct(RbStruct st)
        {
            stream.WriteByte(TYPE_STRUCT);
            WriteObject(st.ClassName);
            WriteLong(st.Members.Count);
            foreach (var kv in st.Members)
            {
                WriteObject(kv.Key);
                WriteObject(kv.Value);
            }
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
