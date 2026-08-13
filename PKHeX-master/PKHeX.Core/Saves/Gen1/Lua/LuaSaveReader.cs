using System;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace PKHeX.Core.Saves.Gen1.Lua;

/// <summary>
/// Recognizes and loads Gen1 "Recomp" project Lua save files (e.g. <c>slot1.lua</c>).
/// Registered as a <see cref="CustomSaveReaders"/> entry in <see cref="SaveUtil"/>.
/// </summary>
/// <remarks>
/// The Lua format is a single <c>return { ... }</c> table that serializes the game's
/// save state as named fields. This reader parses the Lua text, maps the relevant
/// fields to a standard Gen1 <see cref="SAV1"/> binary buffer, and returns the <see cref="SAV1"/>.
/// Write-back is handled by <see cref="LuaSaveWrapper"/> (in PKVault) which calls
/// <see cref="LuaSaveConverter.SAV1ToLua"/> to serialize the SAV1 back to Lua text.
/// </remarks>
public sealed class LuaSaveReader : ISaveReader
{
    /// <summary>Whether the data is large enough to potentially be a Lua save file.</summary>
    public bool IsRecognized(long dataLength) => dataLength > 4;

    /// <inheritdoc/>
    public bool TryRead(Memory<byte> data, [NotNullWhen(true)] out SaveFile? result, string? path = null)
    {
        result = null;

        if (!LuaSaveConverter.IsLuaSave(data.Span))
            return false;

        // Quick check: must reference a Gen1 version or engine marker.
        var text = Encoding.UTF8.GetString(data.Span);
        if (!text.Contains("\"yellow\"") && !text.Contains("\"red\"") && !text.Contains("engine"))
            return false;

        try
        {
            var table = LuaParser.Parse(text);

            if (!HasGen1RecompMarkers(table))
            {
                result = null;
                return false;
            }

            // Parse + create SAV1
            var sav = LuaSaveConverter.LuaToSAV1(table);

            // Store original Lua text for write-back (keyed by file path)
            if (path is not null)
                LuaSaveRegistry.Store(path, text);

            result = sav;
            return true;
        }
        catch
        {
            result = null;
            return false;
        }
    }

    // The reader is called via ISaveReader.TryRead (synchronous).
    // The Task-returning overload is not defined because ISaveReader is synchronous.

    private static bool HasGen1RecompMarkers(LuaTable table)
    {
        // Must have a "version" field (e.g. "yellow") or a "meta" table with engine info.
        return table.TryGetValue("version", out var v) && v.Type == LuaValue.Kind.String
            || table.TryGetValue("meta", out var m) && m.Type == LuaValue.Kind.Table;
    }
}
