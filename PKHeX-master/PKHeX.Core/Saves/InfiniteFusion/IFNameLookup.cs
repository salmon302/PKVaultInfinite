using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PKHeX.Core;

/// <summary>
/// Maps Pokémon Essentials / Infinite Fusion internal symbols (<c>:PIKACHU</c>, <c>:THUNDERBOLT</c>, …)
/// onto PKHeX numeric IDs.
/// </summary>
/// <remarks>
/// Essentials identifies everything by a Ruby symbol derived from the English name with all non-alphanumeric
/// characters removed and the rest upper-cased. Rather than shipping a hand-written table (which would rot with
/// every new PKHeX species/move), the lookups are derived from PKHeX's own English string tables using the same
/// normalization, plus a small alias table for the handful of names that do not round-trip.
/// </remarks>
public static class IFNameLookup
{
    private const int English = (int)LanguageID.English;

    private static readonly Lazy<Dictionary<string, ushort>> SpeciesMap = new(BuildSpeciesMap);
    private static readonly Lazy<Dictionary<string, ushort>> MoveMap = new(BuildMoveMap);
    private static readonly Lazy<Dictionary<string, ushort>> ItemMap = new(BuildItemMap);
    private static readonly Lazy<Dictionary<string, ushort>> AbilityMap = new(BuildAbilityMap);

    /// <summary>Resolves an Essentials species symbol to a PKHeX species ID, or 0 when unknown.</summary>
    public static ushort GetSpecies(string symbol) => Get(SpeciesMap.Value, symbol);

    /// <summary>Resolves an Essentials move symbol to a PKHeX move ID, or 0 when unknown.</summary>
    public static ushort GetMove(string symbol) => Get(MoveMap.Value, symbol);

    /// <summary>Resolves an Essentials item symbol to a PKHeX item ID, or 0 when unknown.</summary>
    public static ushort GetItem(string symbol) => Get(ItemMap.Value, symbol);

    /// <summary>Resolves an Essentials ability symbol to a PKHeX ability ID, or 0 when unknown.</summary>
    public static ushort GetAbility(string symbol) => Get(AbilityMap.Value, symbol);

    /// <summary>Resolves an Essentials type symbol (<c>:DARK</c>) to the PKHeX display type id
    /// (PKHeX type index + 1, matching <see cref="DexItemForm.Types"/>), or 0 when unknown.</summary>
    public static byte GetType(string symbol)
    {
        if (string.IsNullOrEmpty(symbol))
            return 0;
        var name = symbol.TrimStart(':');
        var types = GameInfo.Strings.types;
        for (int i = 0; i < types.Length; i++)
            if (types[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return (byte)(i + 1);
        return 0;
    }

    /// <summary>Resolves an Essentials ball symbol (<c>:DUSKBALL</c>) to a <see cref="Ball"/>; falls back to <see cref="Ball.Poke"/>.</summary>
    public static Ball GetBall(string symbol)
    {
        var key = Normalize(symbol);
        if (key.EndsWith("BALL", StringComparison.Ordinal))
            key = key[..^4];
        foreach (var ball in Enum.GetValues<Ball>())
        {
            if (ball is Ball.None)
                continue;
            if (Normalize(ball.ToString()) == key)
                return ball;
        }
        return Ball.Poke;
    }

    private static ushort Get(Dictionary<string, ushort> map, string symbol)
    {
        if (string.IsNullOrEmpty(symbol))
            return 0;

        // Essentials suffixes alternate forms onto the base symbol (:ROTOM_1, :LYCANROC_D, :MELOETTA_A).
        // Forms are read separately from @form, so progressively strip suffixes until something matches.
        if (TryGet(map, symbol, out var value))
            return value;
        int cut = symbol.LastIndexOf('_');
        if (cut > 0 && TryGet(map, symbol.AsSpan(0, cut), out value))
            return value;
        cut = symbol.IndexOf('_');
        if (cut > 0 && TryGet(map, symbol.AsSpan(0, cut), out value))
            return value;
        return 0;
    }

    private static bool TryGet(Dictionary<string, ushort> map, ReadOnlySpan<char> symbol, out ushort value)
    {
        var key = Normalize(symbol);
        if (key.Length == 0)
        {
            value = 0;
            return false;
        }
        if (map.TryGetValue(key, out value))
            return true;
        // Trailing form index without a separator (:UNOWN2).
        int cut = key.Length;
        while (cut > 0 && char.IsAsciiDigit(key[cut - 1]))
            cut--;
        return cut != key.Length && cut != 0 && map.TryGetValue(key[..cut], out value);
    }

    /// <summary>Upper-cases and strips diacritics plus every non-alphanumeric character.</summary>
    internal static string Normalize(ReadOnlySpan<char> value)
    {
        var decomposed = value.ToString().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            if (char.IsAsciiLetterOrDigit(c))
                sb.Append(char.ToUpperInvariant(c));
        }
        return sb.ToString();
    }

    private static Dictionary<string, ushort> BuildSpeciesMap()
    {
        var map = new Dictionary<string, ushort>(Legal.MaxSpeciesID_9 + 32, StringComparer.Ordinal);
        for (ushort species = 1; species <= Legal.MaxSpeciesID_9; species++)
        {
            var name = SpeciesName.GetSpeciesName(species, English);
            Add(map, name, species);
        }

        // Names whose gender symbols / punctuation collapse to the same key, or that Essentials spells differently.
        map[Normalize("NIDORANmA")] = (ushort)Species.NidoranM;
        map["NIDORANM"] = (ushort)Species.NidoranM;
        map["NIDORANMALE"] = (ushort)Species.NidoranM;
        map[Normalize("NIDORANfE")] = (ushort)Species.NidoranF;
        map["NIDORANF"] = (ushort)Species.NidoranF;
        map["NIDORANFEMALE"] = (ushort)Species.NidoranF;
        return map;
    }

    private static Dictionary<string, ushort> BuildMoveMap()
    {
        var strings = GameInfo.GetStrings(GameLanguage.DefaultLanguage);
        var map = BuildFromList(strings.movelist);

        // Essentials keeps a few pre-Gen-VI move spellings that PKHeX has since renamed.
        map["HIJUMPKICK"] = (ushort)Move.HighJumpKick;
        map["VICEGRIP"] = (ushort)Move.ViseGrip;
        map["FAINTATTACK"] = (ushort)Move.FeintAttack;
        map["SMELLINGSALT"] = (ushort)Move.SmellingSalts;
        return map;
    }

    private static Dictionary<string, ushort> BuildItemMap()
    {
        var strings = GameInfo.GetStrings(GameLanguage.DefaultLanguage);
        return BuildFromList(strings.itemlist);
    }

    private static Dictionary<string, ushort> BuildAbilityMap()
    {
        var strings = GameInfo.GetStrings(GameLanguage.DefaultLanguage);
        return BuildFromList(strings.abilitylist);
    }

    private static Dictionary<string, ushort> BuildFromList(ReadOnlySpan<string> names)
    {
        var map = new Dictionary<string, ushort>(names.Length, StringComparer.Ordinal);
        for (int i = 1; i < names.Length; i++)
            Add(map, names[i], (ushort)i);
        return map;
    }

    private static void Add(Dictionary<string, ushort> map, ReadOnlySpan<char> name, ushort value)
    {
        var key = Normalize(name);
        if (key.Length != 0)
            map.TryAdd(key, value); // first (lowest ID) wins; later duplicates are alternate spellings
    }
}
