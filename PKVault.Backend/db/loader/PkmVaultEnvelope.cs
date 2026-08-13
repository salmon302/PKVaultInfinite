using System.Reflection;
using System.Text;
using PKHeX.Core;

/**
 * PKVault-level format envelope for persisted raw PKM bytes.
 *
 * Stored bytes are wrapped as: PKVAULT:<format>|<EntityContext>|<PKHeXVersion>|<raw>
 * so that a PKHeX version bump (which may shift the in-memory byte layout of a given
 * PKM format) is detectable on load instead of silently failing to re-read.
 *
 * Legacy files written before this envelope existed have no prefix and are still
 * accepted by TryUnwrap (returned as-is) for backward compatibility.
 */
public static class PkmVaultEnvelope
{
    public const string Magic = "PKVAULT";
    public const int FormatVersion = 1;

    private const byte Delimiter = (byte)'|';
    private static readonly byte[] Prefix = Encoding.ASCII.GetBytes($"{Magic}:");

    public static string GetPkhexVersion() =>
        Assembly.GetAssembly(typeof(PKM))?.GetName().Version?.ToString(3) ?? "0.0.0";

    public static byte[] Wrap(byte[] raw, EntityContext context, string? pkhexVersion = null)
    {
        pkhexVersion ??= GetPkhexVersion();

        var header = $"{Magic}:{FormatVersion}|{context}|{pkhexVersion}|";
        var headerBytes = Encoding.UTF8.GetBytes(header);

        var result = new byte[headerBytes.Length + raw.Length];
        Buffer.BlockCopy(headerBytes, 0, result, 0, headerBytes.Length);
        Buffer.BlockCopy(raw, 0, result, headerBytes.Length, raw.Length);

        return result;
    }

    public static bool IsEnveloped(ReadOnlySpan<byte> data)
    {
        return data.Length >= Prefix.Length && data[..Prefix.Length].SequenceEqual(Prefix);
    }

    public static bool TryUnwrap(
        byte[] data,
        out byte[] raw,
        out EntityContext? storedContext,
        out string? pkhexVersion,
        out string? error)
    {
        raw = [];
        storedContext = null;
        pkhexVersion = null;
        error = null;

        // Legacy (pre-envelope) file: return bytes untouched.
        if (!IsEnveloped(data))
        {
            raw = data;
            return true;
        }

        // Header ends after the 3rd delimiter: PKVAULT:<v>|<ctx>|<ver>|<raw>
        var delimCount = 0;
        var headerEnd = -1;
        for (var i = 0; i < data.Length; i++)
        {
            if (data[i] != Delimiter)
                continue;

            delimCount++;
            if (delimCount == 3)
            {
                headerEnd = i + 1;
                break;
            }
        }

        if (headerEnd < 0)
        {
            error = "Malformed PKVault envelope: missing header delimiter";
            return false;
        }

        var headerText = Encoding.UTF8.GetString(data, 0, headerEnd - 1);
        var parts = headerText.Split((char)Delimiter);

        if (parts.Length != 3 || !parts[0].StartsWith($"{Magic}:"))
        {
            error = "Malformed PKVault envelope: unexpected header fields";
            return false;
        }

        var versionToken = parts[0][$"{Magic}:".Length..];
        if (!int.TryParse(versionToken, out var formatVersion) || formatVersion != FormatVersion)
        {
            error = $"Unsupported PKVault envelope format version: {versionToken}";
            return false;
        }

        if (!Enum.TryParse<EntityContext>(parts[1], out var context))
        {
            error = $"Unknown PKVault envelope context: {parts[1]}";
            return false;
        }

        storedContext = context;
        pkhexVersion = parts[2];
        raw = data[headerEnd..];

        return true;
    }
}
