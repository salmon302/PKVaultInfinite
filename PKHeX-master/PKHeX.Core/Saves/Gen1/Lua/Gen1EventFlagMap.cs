using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using static PKHeX.Core.StringConverterOption;

namespace PKHeX.Core.Saves.Gen1.Lua;

/// <summary>
/// Maps Gen1 "Recomp" project event-flag names to PKHeX SAV1 bit-flag numbers.
/// Based on the pokeyellow (Yellow) decompilation's <c>constants/event_flags.asm</c> ordering.
/// Flags not present here are preserved verbatim via <see cref="LuaSaveRegistry"/>.
/// </summary>
internal static class Gen1EventFlagMap
{
    private static readonly Dictionary<string, int> NameToBit = new(StringComparer.Ordinal)
    {
        // Ordered per pokeyellow constants/event_flags.asm (sequential numbering)
        ["EVENT_1ST_ROUTE22_RIVAL_BATTLE"]     = 0,
        ["EVENT_BATTLED_RIVAL_IN_OAKS_LAB"]    = 1,
        ["EVENT_CHOSE_PIKACHU"]                = 2,
        ["EVENT_COMPLETED_CATCH_TRAINING"]     = 3,
        ["EVENT_FOLLOWED_OAK_INTO_LAB"]        = 4,
        ["EVENT_FOLLOWED_OAK_INTO_LAB_2"]      = 5,
        ["EVENT_GOT_OAKS_PARCEL"]              = 6,
        ["EVENT_GOT_POKEDEX"]                  = 7,
        ["EVENT_GOT_STARTER"]                  = 8,
        ["EVENT_OAK_ASKED_TO_CHOOSE_MON"]      = 9,
        ["EVENT_OAK_GOT_PARCEL"]               = 10,
        ["EVENT_ROUTE22_RIVAL_WANTS_BATTLE"]   = 11,
    };

    public static bool TryGetFlagNumber(string name, out int bit) => NameToBit.TryGetValue(name, out bit);

    // For V1 write-back: iterate the known flags.
    public static IEnumerable<(string name, int bit)> GetAllFlags()
    {
        foreach (var kvp in NameToBit)
            yield return (kvp.Key, kvp.Value);
    }
}
