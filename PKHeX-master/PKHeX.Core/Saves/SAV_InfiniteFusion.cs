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
/// Writing is currently non-destructive: <see cref="GetFinalData"/> returns the untouched original bytes.
/// Re-marshaling edits back into the object graph is a later milestone.
/// </remarks>
public sealed class SAV_InfiniteFusion : SaveFile, IBoxDetailName
{
    public const int PartySlots = 6;
    public const int SlotsPerBox = 30;

    private readonly byte[] _rawData;
    private readonly int _boxCount;
    private readonly string[] _boxNames;

    /// <summary>Fusion head/body pairs, keyed by absolute storage index (see <see cref="GetBoxSlotFromIndex"/>).</summary>
    public IReadOnlyDictionary<int, FusionPair> Fusions { get; }

    /// <summary>Fusion head/body pairs for party slots, keyed by party index.</summary>
    public IReadOnlyDictionary<int, FusionPair> PartyFusions { get; }

    /// <summary>A fused Pokémon's two component species (PKHeX species IDs, 0 when unmapped).</summary>
    public readonly record struct FusionPair(ushort Head, ushort Body, string HeadName, string BodyName)
    {
        public override string ToString() => $"{HeadName}/{BodyName}";
    }

    public SAV_InfiniteFusion(Memory<byte> data) : this(Parse(data.Span)) { }

    private SAV_InfiniteFusion(SaveState state) : base(new byte[GetBufferSize(state.Boxes.Count)])
    {
        _rawData = state.Raw;
        _boxCount = Math.Max(1, state.Boxes.Count);
        _boxNames = state.BoxNames;
        Fusions = state.BoxFusions;
        PartyFusions = state.PartyFusions;

        Party = 0;
        Box = PartySlots * PokeCrypto.SIZE_8PARTY;

        OT = state.OTName;
        ID32 = state.ID32;
        Gender = state.TrainerGender;
        Money = state.Money;
        PlayedHours = state.PlayedHours;
        PlayedMinutes = state.PlayedMinutes;
        PlayedSeconds = state.PlayedSeconds;

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
        public List<PK9> Party = [];
        public List<List<PK9?>> Boxes = [];
        public string[] BoxNames = [];
        public Dictionary<int, FusionPair> BoxFusions = [];
        public Dictionary<int, FusionPair> PartyFusions = [];
        public string OTName = PKHeX.Core.TrainerName.ProgramINT;
        public uint ID32;
        public byte TrainerGender;
        public uint Money;
        public int PlayedHours;
        public int PlayedMinutes;
        public int PlayedSeconds;
    }

    private static SaveState Parse(ReadOnlySpan<byte> data)
    {
        var state = new SaveState { Raw = data.ToArray() };
        if (RubyMarshal.Load(data) is not RbHash top)
            throw new InvalidDataException("Infinite Fusion save root is not a Hash.");

        if (top["player"] is RbObject player)
        {
            ParseTrainer(state, player);
            ParseParty(state, player);
        }
        if (top["PokemonStorage"] is RbObject storage)
            ParseBoxes(state, storage);
        if (top["framecount"] is RbFixnum frames)
            SetPlayTime(state, frames.Value / 40); // RGSS runs at 40 fps

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
        if (ReadInt(player["@id"]) is > 0 and var id)
            state.ID32 = (uint)id;
        state.TrainerGender = (byte)Math.Clamp(ReadInt(player["@gender"]), 0, 1);
        if (ReadInt(player["@money"]) is > 0 and var money)
            state.Money = (uint)money;
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
            var slots = new List<PK9?>();
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
    }

    private static IEnumerable<RbValue?> EnumerateBoxSlots(RbValue? box) => box switch
    {
        // PokemonStorage stores either a bare Array of slots or a PokemonBox object wrapping one.
        RbArray arr => arr.Items,
        RbObject obj when obj["@pokemon"] is RbArray inner => inner.Items,
        _ => [],
    };

    private static string ReadBoxName(RbValue? box, int index) => box switch
    {
        RbObject obj when obj["@name"] is RbString s && s.Text.Length != 0 => s.Text,
        _ => $"Box {index + 1}",
    };
    #endregion

    #region Pokémon conversion
    private static PK9 ConvertPokemon(RbObject poke, out FusionPair? fusion)
    {
        fusion = null;
        var pk = new PK9();
        try
        {
            var speciesData = poke["@species_data"] as RbObject;
            bool isFused = speciesData?.ClassName.Name == "GameData::FusedSpecies";

            ushort species;
            if (isFused && speciesData is not null)
            {
                ushort head = ReadSpecies(speciesData["@head_pokemon"]);
                ushort body = ReadSpecies(speciesData["@body_pokemon"]);
                fusion = new FusionPair(head, body, SpeciesLabel(head), SpeciesLabel(body));
                species = head != 0 ? head : body;
            }
            else
            {
                species = ReadSpecies(speciesData) is var direct and not 0 ? direct : ReadSpecies(poke["@species"]);
            }

            pk.Species = species;
            if (species == 0)
                return pk;

            pk.Form = (byte)Math.Clamp(ReadInt(poke["@form"]), 0, byte.MaxValue);
            pk.Gender = (byte)Math.Clamp(ReadInt(poke["@gender"]), 0, 2);

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

            int abilityIndex = ReadInt(poke["@ability_index"]);
            pk.AbilityNumber = abilityIndex switch { 0 => 1, 1 => 2, _ => 4 };
            pk.RefreshAbility(abilityIndex);

            pk.CurrentFriendship = (byte)Math.Clamp(ReadInt(poke["@happiness"]), 0, byte.MaxValue);
            pk.Ball = (byte)Ball.Poke;

            ReadMoves(pk, poke["@moves"]);
            ReadLevel(pk, poke);

            if (poke["@owner"] is RbObject owner)
                ReadOwner(pk, owner);

            pk.Nickname = ReadNickname(poke, pk, fusion);
            pk.IsNicknamed = poke["@name"] is RbString { Text.Length: > 0 };
            pk.Language = (int)LanguageID.English;
            pk.Version = GameVersion.SV;
            pk.MetLevel = pk.CurrentLevel;
            pk.CurrentHandler = 0;
            pk.HealPP();
            pk.ResetPartyStats();
        }
        catch (Exception)
        {
            // A single malformed Pokémon must never break the whole save load.
            return new PK9();
        }
        return pk;
    }

    private static void ReadLevel(PK9 pk, RbObject poke)
    {
        int exp = ReadInt(poke["@exp"]);
        if (exp > 0)
        {
            pk.EXP = (uint)exp;
            // Clamp to the growth curve so PKHeX doesn't report an out-of-range level.
            byte level = Experience.GetLevel(pk.EXP, pk.PersonalInfo.EXPGrowth);
            if (level is 0 or > 100)
                pk.CurrentLevel = 1;
            return;
        }
        int level2 = ReadInt(poke["@level"]);
        pk.CurrentLevel = (byte)Math.Clamp(level2 is 0 ? 1 : level2, 1, 100);
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
            var id = ReadMove(move["@id"]);
            if (id == 0)
                continue;
            pk.SetMove(i, id);
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
        if (id > 0)
            pk.ID32 = (uint)id;
    }

    private static string ReadNickname(RbObject poke, PK9 pk, FusionPair? fusion)
    {
        if (poke["@name"] is RbString s && s.Text.Length != 0)
            return Truncate(s.Text);
        if (fusion is { } pair)
            return Truncate(pair.ToString());
        return SpeciesName.GetSpeciesNameGeneration(pk.Species, (int)LanguageID.English, 9);
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
    public override GameVersion Version { get; set; } = GameVersion.SV;
    public override byte Generation => 9;
    public override EntityContext Context => EntityContext.Gen9; // entities are emitted as PK9
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

    public override Type PKMType => typeof(PK9);
    public override PKM BlankPKM => new PK9();
    public override int SIZE_STORED => PokeCrypto.SIZE_8STORED;
    public override int SIZE_PARTY => PokeCrypto.SIZE_8PARTY;
    public override int MaxEV => EffortValues.Max252;
    protected override PKM GetPKM(Memory<byte> data) => new PK9(data);
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
    protected internal override string ShortSummary => $"{OT} - Infinite Fusion";

    /// <summary>Infinite Fusion saves are read-only for now; export the original Marshal stream untouched.</summary>
    protected override Memory<byte> GetFinalData() => _rawData;
    #endregion
}
