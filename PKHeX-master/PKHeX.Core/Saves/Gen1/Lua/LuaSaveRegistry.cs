using System;
using System.Collections.Concurrent;
using PKHeX.Core;

namespace PKHeX.Core.Saves.Gen1.Lua;

/// <summary>
/// Stores the original Lua save text for a SAV1 so that write-back can
/// preserve fields that SAV1 does not expose (object toggles, player position,
/// pikachu mood/walk steps, meta, modData, etc.).
/// </summary>
public static class LuaSaveRegistry
{
    private static readonly ConcurrentDictionary<string, string> _originalLua = new();

    /// <summary>Store the original Lua text keyed by file path.</summary>
    public static void Store(string filePath, string luaText)
    {
        if (!string.IsNullOrEmpty(filePath))
            _originalLua[filePath] = luaText;
    }

    /// <summary>Store the original Lua text keyed by the save's file path metadata.</summary>
    public static void Store(SaveFile sav, string luaText)
    {
        var path = sav.Metadata.FilePath;
        if (!string.IsNullOrEmpty(path))
            _originalLua[path] = luaText;
    }

    /// <summary>Retrieve the original Lua text for the given save's file path.</summary>
    public static bool TryGetOriginalLua(SaveFile sav, out string? luaText)
    {
        var path = sav.Metadata.FilePath;
        if (string.IsNullOrEmpty(path))
        {
            luaText = null;
            return false;
        }
        return _originalLua.TryGetValue(path, out luaText);
    }

    /// <summary>Remove the stored Lua text for the given file path.</summary>
    public static void Remove(string filePath) => _originalLua.TryRemove(filePath, out _);
}
