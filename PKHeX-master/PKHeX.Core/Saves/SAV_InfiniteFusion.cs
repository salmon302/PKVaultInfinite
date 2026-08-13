using System;
using System.Collections.Generic;
using System.IO;
using PKHeX.Core.Ruby;

namespace PKHeX.Core;

/// <summary>
/// Pokémon Infinite Fusion (Pokémon Essentials / RGSS) save file support.
/// </summary>
/// <remarks>
/// The save is a raw Ruby <c>Marshal.dump</c> object graph (see <see cref="RubyMarshal"/>) — not a flat
/// byte layout. This class walks that graph once at load time and projects the party/storage Pokémon into
/// a synthetic <see cref="PK9"/> buffer so the rest of PKHeX (and PKVault) can treat it like any other save.
///
/// Non-fused Pokémon carry a plain <c>GameData::Species</c> and map 1:1 onto an official species.
/// Fused Pokémon carry a <c>GameData::FusedSpecies</c> with a head and a body; there is no official species
/// for the pair, so the <b>head</b> is used as the visible species and the pair is recorded in
/// <see cref="Fusions"/> (compat spec §5 — proper fusion entities are a later milestone).
///
    /// Writing re-marshals the object graph via <see cref="RubyMarshal.Save"/>, so edits that mutate the
    /// in-memory graph (e.g. Pokédex <see cref="SetSeen"/>/<see cref="SetCaught"/>) are persisted.
    /// </remarks>
public sealed class SAV_InfiniteFusion : SaveFile, IBoxDetailName
{
    public const int PartySlots = 6;
    public const int SlotsPerBox = 30;

    /// <summary>RGSS/Essentials runs its frame counter at 40 fps.</summary>
    private const int FramesPerSecond = 40;

    private readonly byte[] _rawData;
    private readonly RbHash? _root;
    private readonly int _boxCount;
    private readonly string[] _boxNames;
    private readonly int _language;

    /// <summary>Per-save <c>@id_number</c> → Essentials symbol map, harvested from the save's own
    /// <c>GameData::Species</c> objects (covers encountered species). Merged with the static
    /// <see cref="IFSpeciesOrder"/> table for <see cref="GetIfIndex"/>.</summary>
    private readonly Dictionary<ushort, int> _pkToIf;

    private readonly RbArray? _seenStandard;
    private readonly RbArray? _ownedStandard;

    // Fusion-matrix Pokédex (head × body), indexed by IF dex number (1..577). See §9.5.
    private readonly RbArray? _seenFusion;
    private readonly RbArray? _ownedFusion;

    /// <summary>Pokémon Essentials engine version the save was produced with (e.g. <c>19.1.dev</c>).</summary>
    public string EssentialsVersion { get; }

    /// <summary>Infinite Fusion game version the save was produced with (e.g. <c>6.8.2</c>).</summary>
    public string GameVersionText { get; }

    /// <summary>Fusion head/body pairs, keyed by absolute storage index (see <see cref="GetBoxSlotFromIndex"/>).</summary>
    public IReadOnlyDictionary<int, FusionPair> Fusions { get; }

    /// <summary>Fusion head/body pairs for party slots, keyed by party index.</summary>
    public IReadOnlyDictionary<int, FusionPair> PartyFusions { get; }

    /// <summary>A fused Pokémon's two component species (PKHeX species IDs, 0 when unmapped).</summary>
    public readonly record struct FusionPair(ushort Head, ushort Body, string HeadName, string BodyName, string FusionName, byte[] Types)
    {
        public override string ToString() => string.IsNullOrEmpty(FusionName) ? $"{HeadName}/{BodyName}" : FusionName;
    }

    public SAV_InfiniteFusion(Memory<byte> data) : this(Parse(data.Span)) { }

    private SAV_InfiniteFusion(SaveState state) : base(new byte[GetBufferSize(state.Boxes.Count)])
    {
        _rawData = state.Raw;
        _root = state.Root;
        _boxCount = Math.Max(1, state.Boxes.Count);
        _boxNames = state.BoxNames;
        _language = state.Language;
        EssentialsVersion = state.EssentialsVersion;
        GameVersionText = state.GameVersionText;
        Fusions = state.BoxFusions;
        PartyFusions = state.PartyFusions;
        _seenStandard = state.SeenStandard;
        _ownedStandard = state.OwnedStandard;
        _seenFusion = state.SeenFusion;
        _ownedFusion = state.OwnedFusion;
        _pkToIf = BuildReverseMap(state.SaveSpeciesOrder);

        Party = 0;
        Box = PartySlots * PokeCrypto.SIZE_8PARTY;

        OT = state.OTName;
        ID32 = state.ID32;
        Gender = state.TrainerGender;
        Money = state.Money;
        PlayedHours = state.PlayedHours;
        PlayedMinutes = state.PlayedMinutes;
        PlayedSeconds = state.PlayedSeconds;
        CurrentBox = Math.Clamp(state.CurrentBox, 0, _boxCount - 1);

        WriteEntities(state);
        PartyCount = Math.Min(state.Party.Count, PartySlots);
    }

    private static int GetBufferSize(int boxCount)
        => (PartySlots * PokeCrypto.SIZE_8PARTY) + (Math.Max(1, boxCount) * SlotsPerBox * PokeCrypto.SIZE_8STORED);

    private void WriteEntities(SaveState state)
    {
        for (int i = 0; i < state.Party.Count && i < PartySlots; i++)
            state.Party[i].WriteEncryptedDataParty(Data.Slice(GetPartyOffset(i), SIZE_PARTY));

        for (int b = 0; b < state.Boxes.Count && b < _boxCount; b++)
        {
            var box = state.Boxes[b];
            for (int s = 0; s < box.Count && s < SlotsPerBox; s++)
            {
                if (box[s] is not { } pk)
                    continue;
                pk.WriteEncryptedDataStored(Data.Slice(GetBoxSlotOffset(b, s), SIZE_STORED));
            }
        }
    }

    #region Detection
    /// <summary>Returns true when the data looks like an Infinite Fusion (RGSS) save: a Ruby Marshal 4.8 stream whose root is a Hash.</summary>
    public static bool IsMatch(ReadOnlySpan<byte> data)
    {
        if (data.Length < 0x10000) // a real IF save is >1 MB; guard against tiny marshal blobs
            return false;
        if (data[0] != 0x04 || data[1] != 0x08)
            return false;
        if (data[2] != (byte)'{') // root must be a Hash of globals
            return false;
        // The first key of an Essentials save graph is a symbol.
        return data[4] == (byte)':';
    }
    #endregion

    #region Parsing
    private sealed class SaveState
    {
        public byte[] Raw = [];
        public RbHash? Root;
        public List<PKF> Party = [];
        public List<List<PKF?>> Boxes = [];
        public string[] BoxNames = [];
        public Dictionary<int, FusionPair> BoxFusions = [];
        public Dictionary<int, FusionPair> PartyFusions = [];
        public RbArray? SeenStandard;
        public RbArray? OwnedStandard;
        public RbArray? SeenFusion;
        public RbArray? OwnedFusion;
        public Dictionary<int, string> SaveSpeciesOrder = [];
        public string OTName = PKHeX.Core.TrainerName.ProgramINT;
        public uint ID32;
        public byte TrainerGender;
        public uint Money;
        public int Language = (int)LanguageID.English;
        public int CurrentBox;
        public string EssentialsVersion = string.Empty;
        public string GameVersionText = string.Empty;
        public int PlayedHours;
        public int PlayedMinutes;
        public int PlayedSeconds;
    }

    private static SaveState Parse(ReadOnlySpan<byte> data)
    {
        var state = new SaveState { Raw = data.ToArray() };
        if (RubyMarshal.Load(data) is not RbHash top)
            throw new InvalidDataException("Infinite Fusion save root is not a Hash.");
        state.Root = top;

        if (top["essentials_version"] is RbString ev)
            state.EssentialsVersion = ev.Text;
        if (top["game_version"] is RbString gv)
            state.GameVersionText = gv.Text;

        if (top["player"] is RbObject player)
        {
            ParseTrainer(state, player);
            ParseParty(state, player);
            if (player["@pokedex"] is RbObject dex)
            {
                state.SeenStandard = dex["@seen_standard"] as RbArray;
                state.OwnedStandard = dex["@owned_standard"] as RbArray;
                state.SeenFusion = dex["@seen_fusion"] as RbArray;
                state.OwnedFusion = dex["@owned_fusion"] as RbArray;
            }
        }
        if (top["storage_system"] is RbObject storage)
            ParseBoxes(state, storage);
        if (top["frame_count"] is { } frames)
            SetPlayTime(state, ReadLong(frames) / FramesPerSecond);

        // The save carries a GameData::Species (with @id_number + @id) for every species the
        // player has encountered; harvest that id_number -> symbol map so the Pokédex flags can be
        // decoded without the game's (encrypted) Data/species.dat. See IFSpeciesOrder / §9.8.
        HarvestSpeciesOrder(top, state.SaveSpeciesOrder);

        if (state.Boxes.Count == 0)
            state.Boxes.Add([]);
        if (state.BoxNames.Length < state.Boxes.Count)
            state.BoxNames = CreateDefaultBoxNames(state.Boxes.Count);
        return state;
    }

    private static string[] CreateDefaultBoxNames(int count)
    {
        var names = new string[count];
        for (int i = 0; i < count; i++)
            names[i] = $"Box {i + 1}";
        return names;
    }

    private static void SetPlayTime(SaveState state, long totalSeconds)
    {
        if (totalSeconds <= 0)
            return;
        state.PlayedHours = (int)(totalSeconds / 3600);
        state.PlayedMinutes = (int)(totalSeconds / 60 % 60);
        state.PlayedSeconds = (int)(totalSeconds % 60);
    }

    private static void ParseTrainer(SaveState state, RbObject player)
    {
        if (player["@name"] is RbString name && name.Text.Length != 0)
            state.OTName = name.Text;
        long id = ReadLong(player["@id"]);
        if (id is > 0 and <= uint.MaxValue)
            state.ID32 = (uint)id;
        state.TrainerGender = ReadTrainerGender(player);
        long money = ReadLong(player["@money"]);
        if (money > 0)
            state.Money = (uint)Math.Min(money, uint.MaxValue);
        int language = ReadInt(player["@language"]);
        if (language is > 0 and <= 10)
            state.Language = language;
    }

    /// <summary>
    /// Essentials does not store the player's gender directly; it is a property of the assigned trainer type
    /// (<c>:POKEMONTRAINER_Red</c> / <c>:POKEMONTRAINER_Leaf</c>), whose PBS table is not part of the save.
    /// </summary>
    private static byte ReadTrainerGender(RbObject player)
    {
        if (player["@gender"] is { } explicitGender && explicitGender is not RbNil)
            return (byte)Math.Clamp(ReadInt(explicitGender), 0, 1);
        if (player["@trainer_type"] is RbSymbol type && type.Name.Contains("Leaf", StringComparison.OrdinalIgnoreCase))
            return 1;
        return 0;
    }

    private static void ParseParty(SaveState state, RbObject player)
    {
        if (player["@party"] is not RbArray party)
            return;
        foreach (var entry in party.Items)
        {
            if (entry is not RbObject poke)
                continue;
            int index = state.Party.Count;
            if (index >= PartySlots)
                break;
            state.Party.Add(ConvertPokemon(poke, out var fusion));
            if (fusion is { } pair)
                state.PartyFusions[index] = pair;
        }
    }

    private static void ParseBoxes(SaveState state, RbObject storage)
    {
        if (storage["@boxes"] is not RbArray boxes)
            return;

        var names = new List<string>();
        foreach (var entry in boxes.Items)
        {
            int boxIndex = state.Boxes.Count;
            var slots = new List<PKF?>();
            names.Add(ReadBoxName(entry, boxIndex));

            foreach (var mon in EnumerateBoxSlots(entry))
            {
                int slotIndex = slots.Count;
                if (slotIndex >= SlotsPerBox)
                    break;
                if (mon is not RbObject poke)
                {
                    slots.Add(null);
                    continue;
                }
                slots.Add(ConvertPokemon(poke, out var fusion));
                if (fusion is { } pair)
                    state.BoxFusions[(boxIndex * SlotsPerBox) + slotIndex] = pair;
            }
            state.Boxes.Add(slots);
        }
        state.BoxNames = [.. names];
        state.CurrentBox = Math.Max(0, ReadInt(storage["@currentBox"]));
    }

    private static IEnumerable<RbValue?> EnumerateBoxSlots(RbValue? box) => box switch
    {
        // #PokemonBox / #StorageTransferBox wrap the slot array; a bare Array is also accepted.
        RbObject obj when obj["@pokemon"] is RbArray inner => inner.Items,
        RbArray arr => arr.Items,
        _ => [],
    };

    private static string ReadBoxName(RbValue? box, int index) => box switch
    {
        RbObject obj when obj["@name"] is RbString s && s.Text.Length != 0 => s.Text,
        _ => $"Box {index + 1}",
    };
    #endregion

    #region Pokémon conversion
    private static PKF ConvertPokemon(RbObject poke, out FusionPair? fusion)
    {
        fusion = null;
        var pk = new PKF();
        try
        {
            var speciesData = poke["@species_data"] as RbObject;
            bool isFused = speciesData?.ClassName.Name == "GameData::FusedSpecies";

            ushort species;
            ushort head = 0, body = 0;
            if (isFused && speciesData is not null)
            {
                head = ReadSpecies(speciesData["@head_pokemon"]);
                body = ReadSpecies(speciesData["@body_pokemon"]);
                string fusionName = speciesData["@real_name"] is RbString real && real.Text.Length != 0
                    ? Truncate(real.Text)
                    : string.Empty;
                byte[] fusionTypes = GetFusionTypes(speciesData);
                fusion = new FusionPair(head, body, SpeciesLabel(head), SpeciesLabel(body), fusionName, fusionTypes);
                species = head != 0 ? head : body;
            }
            else
            {
                species = ReadSpecies(speciesData) is var direct and not 0 ? direct : ReadSpecies(poke["@species"]);
            }

            pk.Species = species;
            if (species == 0)
                return pk;

            pk.HeadSpecies = species;
            pk.BodySpecies = isFused ? body : (ushort)0;

            pk.Form = (byte)Math.Clamp(ReadInt(poke["@form"]), 0, byte.MaxValue);

            // Gender must follow the projected species' gender ratio. Essentials stores the fusion's
            // own gender, but a genderless/fixed-gender head (e.g. Regigigas) cannot legitimately
            // take it, so the head species' inherent gender wins for display consistency.
            var genderRatio = pk.PersonalInfo.Gender;
            pk.Gender = genderRatio switch
            {
                255 => 2, // genderless
                0 => 0,   // male-only
                254 => 1, // female-only
                _ => (byte)Math.Clamp(ReadInt(poke["@gender"]), 0, 1),
            };

            uint pid = (uint)ReadLong(poke["@personalID"]);
            if (pid != 0)
                pk.PID = pid;
            pk.EncryptionConstant = pk.PID;

            if (poke["@nature"] is RbSymbol nat && Enum.TryParse<Nature>(nat.Name, true, out var nature) && nature.IsFixed)
                pk.Nature = nature;

            ReadStats(poke["@iv"], out var ivs);
            pk.SetIVs(ivs);
            ReadStats(poke["@ev"], out var evs);
            pk.SetEVs(evs);

            int abilityIndex = Math.Clamp(ReadInt(poke["@ability_index"]), 0, 2);
            pk.AbilityNumber = abilityIndex switch { 0 => 1, 1 => 2, _ => 4 };
            pk.RefreshAbility(abilityIndex);

            pk.CurrentFriendship = (byte)Math.Clamp(ReadInt(poke["@happiness"]), 0, byte.MaxValue);
            pk.Ball = (byte)(poke["@poke_ball"] is RbSymbol ball ? IFNameLookup.GetBall(ball.Name) : Ball.Poke);
            if (poke["@item"] is RbSymbol item)
            {
                var itemId = IFNameLookup.GetItem(item.Name);
                // Keep only items that are legal held items in the SV context; fan-game / unreleased
                // items would otherwise trip the "Held item is unreleased" legality check.
                if (itemId != 0 && Array.IndexOf(Legal.HeldItems_SV, itemId) >= 0)
                    pk.HeldItem = itemId;
            }

            ReadMoves(pk, poke["@moves"]);
            ReadLevel(pk, poke);

            if (poke["@owner"] is RbObject owner)
                ReadOwner(pk, owner);
            else
                pk.Language = (int)LanguageID.English;

            pk.Nickname = ReadNickname(poke, pk, speciesData, fusion, out bool nicknamed);
            pk.IsNicknamed = nicknamed;
            pk.Version = GameVersion.SV;
            pk.MetLevel = (byte)Math.Clamp(ReadInt(poke["@obtain_level"]), 1, pk.CurrentLevel);
            pk.IsEgg = ReadInt(poke["@steps_to_hatch"]) > 0;
            pk.CurrentHandler = 0;

            // Essentials stores an explicit shiny flag; nil means "derive from the personal ID".
            // PKHeX derives shininess from PID+ID32, which will not agree, so force the recorded state.
            if (poke["@shiny"] is RbBool { Value: true })
                pk.SetShiny();
            else if (pk.IsShiny)
                pk.SetUnshiny();

            pk.HealPP();
            pk.ResetPartyStats();
        }
        catch (Exception)
        {
            // A single malformed Pokémon must never break the whole save load.
            return new PKF();
        }
        return pk;
    }

    private static void ReadLevel(PK9 pk, RbObject poke)
    {
        byte level = (byte)Math.Clamp(ReadInt(poke["@level"]), 0, 100);
        uint exp = (uint)Math.Max(0, ReadLong(poke["@exp"]));
        if (exp != 0)
        {
            pk.EXP = exp;
            // Fusions use a blended growth rate, so the head's curve may disagree with the stored level.
            var derived = Experience.GetLevel(exp, pk.PersonalInfo.EXPGrowth);
            if (level == 0 || derived == level)
                return;
        }
        pk.CurrentLevel = level == 0 ? (byte)1 : level;
    }

    private static void ReadMoves(PK9 pk, RbValue? moves)
    {
        if (moves is not RbArray arr)
            return;
        int i = 0;
        foreach (var entry in arr.Items)
        {
            if (i >= 4)
                break;
            if (entry is not RbObject move)
                continue;
            // Keep the slot even when the move is unknown, so the remaining moves stay in their original slots.
            var moveId = ReadMove(move["@id"]);
            if (moveId != 0 && moveId <= Legal.MaxMoveID_9)
                pk.SetMove(i, moveId);
            SetMovePPUps(pk, i, Math.Clamp(ReadInt(move["@ppup"]), 0, 3));
            i++;
        }
    }

    private static void SetMovePPUps(PK9 pk, int index, int value)
    {
        switch (index)
        {
            case 0: pk.Move1_PPUps = value; break;
            case 1: pk.Move2_PPUps = value; break;
            case 2: pk.Move3_PPUps = value; break;
            case 3: pk.Move4_PPUps = value; break;
        }
    }

    private static void ReadOwner(PK9 pk, RbObject owner)
    {
        if (owner["@name"] is RbString s && s.Text.Length != 0)
            pk.OriginalTrainerName = s.Text;
        pk.OriginalTrainerGender = (byte)Math.Clamp(ReadInt(owner["@gender"]), 0, 1);
        long id = ReadLong(owner["@id"]);
        if (id is > 0 and <= uint.MaxValue)
            pk.ID32 = (uint)id;
        int language = ReadInt(owner["@language"]);
        pk.Language = language is > 0 and <= 10 ? language : (int)LanguageID.English;
    }

    private static string ReadNickname(RbObject poke, PK9 pk, RbObject? speciesData, FusionPair? fusion, out bool nicknamed)
    {
        nicknamed = true;
        if (poke["@name"] is RbString s && s.Text.Length != 0)
            return Truncate(s.Text);
        if (fusion is not null)
        {
            // Infinite Fusion generates a portmanteau name ("Scrafsharp") for every fusion; it is the closest
            // thing to a real species name the pair has, so surface it instead of the head species' name.
            if (speciesData?["@real_name"] is RbString real && real.Text.Length != 0)
                return Truncate(real.Text);
            return Truncate(fusion.Value.ToString());
        }
        nicknamed = false;
        return SpeciesName.GetSpeciesNameGeneration(pk.Species, pk.Language, 9);
    }

    private static string Truncate(string value) => value.Length <= 12 ? value : value[..12];

    private static void ReadStats(RbValue? value, out int[] stats)
    {
        stats = new int[6];
        if (value is not RbHash h)
            return;
        // Essentials stat symbols, ordered to PKHeX's HP/ATK/DEF/SPE/SPA/SPD slot order.
        stats[0] = ReadInt(h["HP"]);
        stats[1] = ReadInt(h["ATTACK"]);
        stats[2] = ReadInt(h["DEFENSE"]);
        stats[3] = ReadInt(h["SPEED"]);
        stats[4] = ReadInt(h["SPECIAL_ATTACK"]);
        stats[5] = ReadInt(h["SPECIAL_DEFENSE"]);
    }
    #endregion

    #region Value helpers
    private static long ReadLong(RbValue? v) => v switch
    {
        RbFixnum f => f.Value,
        RbBignum b => (long)b.Value,
        RbFloat f => (long)f.Value,
        _ => 0,
    };

    private static int ReadInt(RbValue? v)
    {
        long value = ReadLong(v);
        return value is > int.MaxValue or < int.MinValue ? 0 : (int)value;
    }

    /// <summary>Resolves an Essentials <c>GameData::Species</c> (or a bare <c>:SYMBOL</c>) to a PKHeX species ID.</summary>
    private static ushort ReadSpecies(RbValue? value) => value switch
    {
        RbSymbol sym => IFNameLookup.GetSpecies(sym.Name),
        RbString str => IFNameLookup.GetSpecies(str.Text),
        RbObject obj => ReadSpeciesObject(obj),
        _ => 0,
    };

    private static byte GetFusionType(RbValue? value) => value switch
    {
        RbSymbol sym => IFNameLookup.GetType(sym.Name),
        RbString str => IFNameLookup.GetType(str.Text),
        _ => 0,
    };

    /// <summary>Reads the merged <c>@type1</c>/<c>@type2</c> of a <c>GameData::FusedSpecies</c> as PKHeX display type ids.</summary>
    private static byte[] GetFusionTypes(RbObject speciesData)
    {
        var t = new List<byte>();
        byte t1 = GetFusionType(speciesData["@type1"]);
        byte t2 = GetFusionType(speciesData["@type2"]);
        if (t1 != 0) t.Add(t1);
        if (t2 != 0 && t2 != t1) t.Add(t2);
        return [.. t];
    }

    private static ushort ReadSpeciesObject(RbObject obj)
    {
        // Prefer the symbolic id (:PIKACHU); it survives Infinite Fusion's custom dex numbering.
        if (ReadSpecies(obj["@id"]) is var byId and not 0)
            return byId;
        if (ReadSpecies(obj["@species"]) is var bySpecies and not 0)
            return bySpecies;
        if (obj["@real_name"] is RbString name && IFNameLookup.GetSpecies(name.Text) is var byName and not 0)
            return byName;
        return 0;
    }

    private static ushort ReadMove(RbValue? value) => value switch
    {
        RbSymbol sym => IFNameLookup.GetMove(sym.Name),
        RbString str => IFNameLookup.GetMove(str.Text),
        RbObject obj => ReadMove(obj["@id"]),
        _ => 0,
    };

    private static string SpeciesLabel(ushort species)
        => species == 0 ? "?" : SpeciesName.GetSpeciesNameGeneration(species, (int)LanguageID.English, 9);
    #endregion

    #region SaveFile contract
    public override string Extension => ".rxdata";
    public override GameVersion Version { get; set; } = GameVersion.InfiniteFusion;
    public override byte Generation => 9;
    public override EntityContext Context => EntityContext.Gen9; // entities are emitted as PKF
    public override bool ChecksumsValid => true;
    public override string ChecksumInfo => "Ruby Marshal stream (no PKHeX checksum).";
    protected override void SetChecksums() { }

    public override IPersonalTable Personal => PersonalTable.SV;
    public override int MaxStringLengthTrainer => 12;
    public override int MaxStringLengthNickname => 12;
    public override ushort MaxMoveID => Legal.MaxMoveID_9;
    public override ushort MaxSpeciesID => Legal.MaxSpeciesID_9;
    public override int MaxAbilityID => Legal.MaxAbilityID_9;
    public override int MaxItemID => Legal.MaxItemID_9;
    public override int MaxBallID => Legal.MaxBallID_9;
    public override GameVersion MaxGameID => Legal.MaxGameID_HOME;
    public override ReadOnlySpan<ushort> HeldItems => Legal.HeldItems_SV;

    public override Type PKMType => typeof(PKF);
    public override PKM BlankPKM => new PKF();
    public override int SIZE_STORED => PokeCrypto.SIZE_8STORED;
    public override int SIZE_PARTY => PokeCrypto.SIZE_8PARTY;
    public override int MaxEV => EffortValues.Max252;
    protected override PKM GetPKM(Memory<byte> data) => new PKF(data);
    protected override void DecryptPKM(Span<byte> data) => PokeCrypto.DecryptIfEncrypted8(data);

    public override int BoxCount => _boxCount;
    public override int BoxSlotCount => SlotsPerBox;
    public override int GetPartyOffset(int slot) => Party + (slot * SIZE_PARTY);
    public override int GetBoxOffset(int box) => Box + (box * SlotsPerBox * SIZE_STORED);
    public string GetBoxName(int box) => (uint)box < (uint)_boxNames.Length ? _boxNames[box] : $"Box {box + 1}";
    public void SetBoxName(int box, ReadOnlySpan<char> value)
    {
        if ((uint)box < (uint)_boxNames.Length)
            _boxNames[box] = value.ToString();
    }

    public override string GetString(ReadOnlySpan<byte> data) => StringConverter8.GetString(data);
    public override int LoadString(ReadOnlySpan<byte> data, Span<char> text) => StringConverter8.LoadString(data, text);
    public override int SetString(Span<byte> destBuffer, ReadOnlySpan<char> value, int maxLength, StringConverterOption option)
        => StringConverter8.SetString(destBuffer, value, maxLength, option);

    protected override SaveFile CloneInternal() => new SAV_InfiniteFusion(_rawData);
    protected internal override string ShortSummary => $"{OT} - Infinite Fusion {GameVersionText}";
    public override string MiscSaveInfo() => $"Pokémon Infinite Fusion {GameVersionText} (Essentials {EssentialsVersion})";
    public override int Language { get => _language; set { } }

    // Essentials stores one 32-bit trainer ID; keep the 16-bit halves in sync with it.
    public override uint ID32 { get; set; }
    public override ushort TID16
    {
        get => (ushort)ID32;
        set => ID32 = (uint)((SID16 << 16) | value);
    }

    public override ushort SID16
    {
        get => (ushort)(ID32 >> 16);
        set => ID32 = (uint)((value << 16) | TID16);
    }

    /// <summary>Infinite Fusion saves expose a Pokédex once the species-order table is available (§9.5/§9.8).</summary>
    public override bool HasPokeDex => true;

    public override bool GetSeen(ushort species)
    {
        int idx = GetIfIndex(species);
        return idx > 0 && _seenStandard is { } arr && idx < arr.Count && arr[idx] is RbBool b && b.Value;
    }

    public override bool GetCaught(ushort species)
    {
        int idx = GetIfIndex(species);
        return idx > 0 && _ownedStandard is { } arr && idx < arr.Count && arr[idx] is RbBool b && b.Value;
    }

    public override void SetSeen(ushort species, bool seen)
    {
        int idx = GetIfIndex(species);
        if (idx > 0 && _seenStandard is { } arr && idx < arr.Count)
            arr.Items[idx] = new RbBool(seen);
    }

    public override void SetCaught(ushort species, bool caught)
    {
        int idx = GetIfIndex(species);
        if (idx > 0 && _ownedStandard is { } arr && idx < arr.Count)
            arr.Items[idx] = new RbBool(caught);
    }

    // ---- Fusion-matrix Pokédex (head × body) — §9.5 ----

    /// <summary>True when the fusion of <paramref name="head"/> over <paramref name="body"/> (IF dex numbers 1..577) has been seen.</summary>
    public bool GetFusionSeen(int head, int body)
    {
        var row = GetFusionRow(_seenFusion, head);
        return row is { } r && body >= 1 && body < r.Count && r.Items[body] is RbBool b && b.Value;
    }

    /// <summary>True when the fusion of <paramref name="head"/> over <paramref name="body"/> (IF dex numbers 1..577) has been caught.</summary>
    public bool GetFusionCaught(int head, int body)
    {
        var row = GetFusionRow(_ownedFusion, head);
        return row is { } r && body >= 1 && body < r.Count && r.Items[body] is RbBool b && b.Value;
    }

    public void SetFusionSeen(int head, int body, bool seen)
    {
        if (GetFusionRow(_seenFusion, head) is { } r && body >= 1 && body < r.Count)
            r.Items[body] = new RbBool(seen);
    }

    public void SetFusionCaught(int head, int body, bool caught)
    {
        if (GetFusionRow(_ownedFusion, head) is { } r && body >= 1 && body < r.Count)
            r.Items[body] = new RbBool(caught);
    }

    private static RbArray? GetFusionRow(RbArray? matrix, int head)
    {
        if (matrix is null || head < 1 || head >= matrix.Count)
            return null;
        return matrix.Items[head] as RbArray;
    }

    /// <summary>Maps a PKHeX species id to this save's IF dex index (1..577), or -1 when the species
    /// is not part of Infinite Fusion. Merges the static <see cref="IFSpeciesOrder"/> with the per-save
    /// species order harvested at parse time.</summary>
    public int GetIfIndex(ushort species)
    {
        if (_pkToIf.TryGetValue(species, out var v))
            return v;
        return IFSpeciesOrder.GetIndex(species);
    }

    private static Dictionary<ushort, int> BuildReverseMap(Dictionary<int, string> saveOrder)
    {
        var d = new Dictionary<ushort, int>();
        // Static canonical table first.
        for (int i = 1; i <= IFSpeciesOrder.Count; i++)
        {
            var pk = IFSpeciesOrder.GetSpecies(i);
            if (pk != 0)
                d.TryAdd(pk, i);
        }
        // Per-save harvested species (encountered) override/extend the static table.
        foreach (var kv in saveOrder)
        {
            var pk = IFNameLookup.GetSpecies(kv.Value);
            if (pk != 0)
                d.TryAdd(pk, kv.Key);
        }
        return d;
    }

    /// <summary>Walks the RGSS object graph and records every <c>GameData::Species</c>'s
    /// <c>@id_number</c> → <c>@id</c> symbol (the save carries one per encountered species).</summary>
    private static void HarvestSpeciesOrder(RbValue? root, Dictionary<int, string> order)
    {
        var visited = new HashSet<RbValue>(ReferenceEqualityComparer.Instance);
        WalkSpecies(root, order, visited);
    }

    private static void WalkSpecies(RbValue? v, Dictionary<int, string> order, HashSet<RbValue> visited)
    {
        if (v is null || !visited.Add(v))
            return;
        switch (v)
        {
            case RbObject o:
                if (o.ClassName.Name == "GameData::Species")
                {
                    int id = (int)ReadLong(o["@id_number"]);
                    string? sym = o["@id"] switch
                    {
                        RbSymbol s => s.Name,
                        RbString s => s.Text,
                        _ => null,
                    };
                    if (id is >= 1 and <= 578 && sym is not null && !order.ContainsKey(id))
                        order[id] = sym;
                }
                foreach (var kv in o.IVariables)
                {
                    WalkSpecies(kv.Key, order, visited);
                    WalkSpecies(kv.Value, order, visited);
                }
                break;
            case RbArray a:
                foreach (var it in a.Items)
                    WalkSpecies(it, order, visited);
                break;
            case RbHash h:
                foreach (var kv in h.Pairs)
                {
                    WalkSpecies(kv.Key, order, visited);
                    WalkSpecies(kv.Value, order, visited);
                }
                break;
        }
    }

        /// <summary>Re-marshals the (possibly edited) object graph back into a Ruby Marshal stream so
        /// mutations such as <see cref="SetSeen"/>/<see cref="SetCaught"/> survive <see cref="Write"/>.</summary>
        protected override Memory<byte> GetFinalData() => _root is { } r ? RubyMarshal.Save(r) : _rawData;
    #endregion
}
