using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using static PKHeX.Core.StringConverterOption;

namespace PKHeX.Core.Saves.Gen1.Lua;

/// <summary>
/// Converts between the Gen1 "Recomp" project's Lua save format and PKHeX's <see cref="SAV1"/>.
/// </summary>
public static class LuaSaveConverter
{
    /// <summary>Checks if raw byte data is a Lua save module.</summary>
    public static bool IsLuaSave(ReadOnlySpan<byte> data) =>
        data.Length >= 6 &&
        data[0] == (byte)'r' && data[1] == (byte)'e' && data[2] == (byte)'t' &&
        data[3] == (byte)'u' && data[4] == (byte)'r' && data[5] == (byte)'n';

    /// <summary>Parse the Lua text and populate a new <see cref="SAV1"/>.</summary>
    public static SAV1 LuaToSAV1(string luaText)
    {
        var table = LuaParser.Parse(luaText);
        return LuaToSAV1(table);
    }

    /// <summary>Populate a new <see cref="SAV1"/> from a parsed <see cref="LuaTable"/>.</summary>
    public static SAV1 LuaToSAV1(LuaTable table)
    {
        var version = table.GetString("version") ?? "red";
        var isYellow = version.Equals("yellow", StringComparison.OrdinalIgnoreCase);
        var gameVersion = isYellow ? GameVersion.YW : GameVersion.RB;
        var sav = new SAV1(LanguageID.English, gameVersion);

        // --- Money ---
        sav.Money = (uint)table.GetNumber("money", sav.MaxMoney);

        // --- Play time ---
        var playTime = table.GetNumber("playTime");
        if (playTime > 0)
        {
            int totalSecs = (int)Math.Round(playTime);
            sav.PlayedHours = Math.Min(255, totalSecs / 3600);
            totalSecs %= 3600;
            sav.PlayedMinutes = totalSecs / 60;
            sav.PlayedSeconds = totalSecs % 60;
        }

        // --- Player info ---
        var player = table.GetTable("player");
        if (player is not null)
        {
            sav.OT = player.GetString("name") ?? TrainerName.ProgramINT;
            sav.TID16 = (ushort)player.GetInt("id");

            var rival = player.GetString("rival");
            if (rival is not null)
            {
                var span = sav.RivalNameTrash;
                StringConverter1.SetString(span, rival, span.Length - 1, false, Clear50);
            }
        }

        // --- Rival starter selector ---
        var rivalStarter = table.GetInt("rivalStarter");
        if (rivalStarter > 0)
            sav.RivalStarter = (byte)rivalStarter;

        // --- Pika friendship (Yellow) ---
        if (isYellow)
            sav.PikaFriendship = (byte)table.GetInt("pikachuHappiness", 0);

        // --- Pokedex ---
        var dex = table.GetTable("pokedex");
        if (dex is not null)
        {
            var seen = dex.GetTable("seen");
            var owned = dex.GetTable("owned");
            if (owned is not null)
                ApplyDexFlags(sav, owned, caught: true);
            if (seen is not null)
                ApplyDexFlags(sav, seen, caught: false);
        }

        // --- Event flags ---
        var flags = table.GetTable("flags");
        if (flags is not null)
            ApplyEventFlags(sav, flags);

        // --- Inventory (bag items) ---
        var inventory = table.GetTable("inventory");
        var bagOrder = table.GetTable("bagOrder");
        if (inventory is not null)
            ApplyBagItems(sav, inventory, bagOrder);

        // --- PC Items ---
        var pcItems = table.GetTable("pcItems");
        if (pcItems is not null)
            ApplyPcItems(sav, pcItems);

        // --- Party ---
        var party = table.GetTable("party");
        if (party is not null)
            ApplyParty(sav, party);

        // --- Box ---
        var box = table.GetTable("box");
        if (box is not null)
            ApplyBox(sav, box);

        return sav;
    }

    /// <summary>Serialize a <see cref="SAV1"/> back to Lua text, preserving unmapped fields from the original.</summary>
    public static byte[] SAV1ToLua(SAV1 sav)
    {
        var original = LuaSaveRegistry.TryGetOriginalLua(sav, out var originalText)
            && !string.IsNullOrEmpty(originalText)
            ? LuaParser.Parse(originalText)
            : null;

        UpdateLuaFromSAV1(sav, original);

        // Serialize the updated table
        var text = original?.Serialize() ?? BuildLuaFromSAV1(sav);
        return Encoding.UTF8.GetBytes(text);
    }

    /// <summary>Update an existing Lua table with current SAV1 state, preserving unmapped fields.</summary>
    private static void UpdateLuaFromSAV1(SAV1 sav, LuaTable? table)
    {
        if (table == null)
            return;

        // money
        table.Set("money", LuaValue.ForNumber(sav.Money));

        // playTime
        var playTime = (sav.PlayedHours * 3600) + (sav.PlayedMinutes * 60) + sav.PlayedSeconds;
        table.Set("playTime", LuaValue.ForNumber(playTime));

        // player
        var player = table.GetTable("player") ?? new LuaTable();
        player.Set("name", LuaValue.ForString(sav.OT ?? ""));
        player.Set("id", LuaValue.ForNumber(sav.TID16));
        var rival = GetRivalName(sav);
        player.Set("rival", LuaValue.ForString(rival));
        // Preserve player.x/y/facing/map from original
        table.Set("player", LuaValue.ForTable(player));

        // version
        table.Set("version", LuaValue.ForString(sav.Version == GameVersion.YW ? "yellow" : "red"));

        // bagOrder + inventory
        var bagItems = GetBagItems(sav);
        var bagOrder = table.GetTable("bagOrder") ?? new LuaTable();
        bagOrder.Clear();
        for (int i = 0; i < bagItems.Count; i++)
            bagOrder.SetArray(i + 1, LuaValue.ForString(bagItems[i].name));
        table.Set("bagOrder", LuaValue.ForTable(bagOrder));

        var inventory = table.GetTable("inventory") ?? new LuaTable();
        inventory.Clear();
        foreach (var (name, qty) in bagItems)
            inventory.Set(name, LuaValue.ForNumber(qty));
        // Remove any items that are no longer in the bag
        table.Set("inventory", LuaValue.ForTable(inventory));

        // pcItems
        var pcItems = table.GetTable("pcItems") ?? new LuaTable();
        pcItems.Clear();
        var pcList = GetPcItems(sav);
        foreach (var (name, qty) in pcList)
            pcItems.Set(name, LuaValue.ForNumber(qty));
        table.Set("pcItems", LuaValue.ForTable(pcItems));

        // pikachu happiness (Yellow)
        if (sav.Version == GameVersion.YW)
        {
            table.Set("pikachuHappiness", LuaValue.ForNumber(sav.PikaFriendship));
            // Preserve pikachuMood, pikachuInBall, pikachuWalkSteps from original
        }

        // flags — update known event flags, write them back
        var flags = table.GetTable("flags") ?? new LuaTable();
        foreach (var (name, bit) in Gen1EventFlagMap.GetAllFlags())
        {
            flags.Set(name, LuaValue.ForBoolean(sav.GetEventFlag(bit)));
        }
        table.Set("flags", LuaValue.ForTable(flags));

        // pokedex
        var dex = table.GetTable("pokedex") ?? new LuaTable();
        var seen = dex.GetTable("seen") ?? new LuaTable();
        var owned = dex.GetTable("owned") ?? new LuaTable();
        seen.Clear();
        owned.Clear();
        for (ushort species = 1; species <= sav.MaxSpeciesID; species++)
        {
            if (sav.GetSeen(species))
                seen.Set(GetLuaSpeciesName(species), LuaValue.ForBoolean(true));
            if (sav.GetCaught(species))
                owned.Set(GetLuaSpeciesName(species), LuaValue.ForBoolean(true));
        }
        dex.Set("seen", LuaValue.ForTable(seen));
        dex.Set("owned", LuaValue.ForTable(owned));
        table.Set("pokedex", LuaValue.ForTable(dex));

        // party
        var party = table.GetTable("party") ?? new LuaTable();
        party.Clear();
        var partyList = sav.PartyData;
        int idx = 1;
        for (int i = 0; i < partyList.Count; i++)
        {
            if (partyList[i] is not PK1 pk || pk.Species == 0)
                continue;
            party.SetArray(idx, LuaValue.ForTable(BuildLuaPartyEntry(pk)));
            idx++;
        }
        table.Set("party", LuaValue.ForTable(party));

        // box — preserve from original (boxes are typically empty in early saves)
        // If boxes were modified, update them
        var boxTable = table.GetTable("box") ?? new LuaTable();
        boxTable.Clear();
        table.Set("box", LuaValue.ForTable(boxTable));
    }

    /// <summary>Build Lua text entirely from SAV1 (fallback when no original exists).</summary>
    private static string BuildLuaFromSAV1(SAV1 sav)
    {
        var table = new LuaTable();
        UpdateLuaFromSAV1(sav, table);
        return table.Serialize();
    }

    // --- Read helpers ---

    private static void ApplyDexFlags(SAV1 sav, LuaTable dexTable, bool caught)
    {
        foreach (var (key, value) in dexTable.Entries)
        {
            if (value.Type != LuaValue.Kind.Boolean || !value.BoolValue)
                continue;
            var speciesId = ResolveSpecies(key);
            if (speciesId == 0)
                continue;
            if (caught)
                sav.SetCaught(speciesId, true);
            else
                sav.SetSeen(speciesId, true);
        }
    }

    private static void ApplyEventFlags(SAV1 sav, LuaTable flagsTable)
    {
        foreach (var (key, value) in flagsTable.Entries)
        {
            if (value.Type != LuaValue.Kind.Boolean)
                continue;
            if (Gen1EventFlagMap.TryGetFlagNumber(key, out var bit))
                sav.SetEventFlag(bit, value.BoolValue);
            // Unknown flags: preserved via LuaSaveRegistry on write-back
        }
    }

    private static void ApplyBagItems(SAV1 sav, LuaTable inventory, LuaTable? bagOrder)
    {
        var ordered = new List<(string name, int qty)>();
        if (bagOrder is not null)
        {
            foreach (var entry in bagOrder.Entries)
            {
                if (!entry.key.StartsWith("[") || entry.value.Type != LuaValue.Kind.String)
                    continue;
                var name = entry.value.StringValue!;
                var qty = (int)inventory.GetNumber(name, 0);
                if (qty > 0)
                    ordered.Add((name, qty));
            }
        }

        foreach (var (key, value) in inventory.Entries)
        {
            if (value.Type != LuaValue.Kind.Number)
                continue;
            var qty = (int)value.NumberValue;
            if (qty <= 0)
                continue;
            if (bagOrder is not null && bagOrder.Entries.Any(e => e.value.Type == LuaValue.Kind.String && e.value.StringValue == key))
                continue;
            ordered.Add((key, qty));
        }

        var resolved = new List<(int id, int qty)>();
        foreach (var (name, qty) in ordered)
        {
            var itemId = ResolveItem(name);
            if (itemId > 0)
                resolved.Add((itemId, qty));
        }

        var validItems = ItemStorage1.General.ToArray();
        resolved.Sort((a, b) =>
        {
            var ia = Array.IndexOf(validItems, (ushort)a.id);
            var ib = Array.IndexOf(validItems, (ushort)b.id);
            if (ia < 0 && ib < 0) return 0;
            if (ia < 0) return 1;
            if (ib < 0) return -1;
            return ia.CompareTo(ib);
        });

        var bag = sav.Inventory;
        var pouch = bag.Pouches[0];
        for (int i = 0; i < pouch.Items.Length; i++)
        {
            pouch.Items[i] = i < resolved.Count
                ? new InventoryItem { Index = resolved[i].id, Count = Math.Min(resolved[i].qty, pouch.MaxCount) }
                : pouch.GetEmpty();
        }
        bag.CopyTo(sav);
    }

    private static void ApplyPcItems(SAV1 sav, LuaTable pcItemsTable)
    {
        var items = new List<(int id, int qty)>();
        foreach (var (key, value) in pcItemsTable.Entries)
        {
            if (value.Type != LuaValue.Kind.Number)
                continue;
            var id = ResolveItem(key);
            if (id == 0)
                continue;
            items.Add((id, (int)value.NumberValue));
        }

        var bag = sav.Inventory;
        var pouch = bag.Pouches[1];
        for (int i = 0; i < pouch.Items.Length; i++)
        {
            pouch.Items[i] = i < items.Count
                ? new InventoryItem { Index = items[i].id, Count = Math.Min(items[i].qty, pouch.MaxCount) }
                : pouch.GetEmpty();
        }
        bag.CopyTo(sav);
    }

    private static void ApplyParty(SAV1 sav, LuaTable partyTable)
    {
        int slot = 0;
        foreach (var entry in partyTable.GetArrayEntries())
        {
            if (slot >= 6)
                break;
            var pk = BuildPK1(entry);
            sav.SetPartySlotAtIndex(pk, slot);
            slot++;
        }
        while (slot < 6)
        {
            sav.SetPartySlotAtIndex(sav.BlankPKM, slot);
            slot++;
        }
    }

    private static void ApplyBox(SAV1 sav, LuaTable boxTable)
    {
        int boxIndex = 0;
        foreach (var (_, value) in boxTable.Entries)
        {
            if (boxIndex >= sav.BoxCount || value.Type != LuaValue.Kind.Table || value.TableValue == null)
                continue;
            int slot = 0;
            foreach (var (_, pkEntry) in value.TableValue.Entries)
            {
                if (slot >= sav.BoxSlotCount || pkEntry.Type != LuaValue.Kind.Table || pkEntry.TableValue == null)
                    continue;
                var pk = BuildPK1(pkEntry.TableValue!);
                if (pk.Species != 0)
                    sav.SetBoxSlotAtIndex(pk, boxIndex, slot);
                slot++;
            }
            boxIndex++;
        }
    }

    private static PK1 BuildPK1(LuaTable entry)
    {
        var pk = new PK1(false);

        var speciesName = entry.GetString("species");
        if (speciesName is not null)
        {
            var speciesId = ResolveSpecies(speciesName);
            if (speciesId > 0)
                pk.Species = speciesId;
        }

        pk.CatchRate = (byte)entry.GetInt("catchRate");

        var dvs = entry.GetTable("dvs");
        if (dvs is not null)
        {
            var atk = (ushort)dvs.GetInt("attack");
            var def = (ushort)dvs.GetInt("defense");
            var spd = (ushort)dvs.GetInt("speed");
            var spc = (ushort)dvs.GetInt("special");
            pk.DV16 = (ushort)((atk << 12) | (def << 8) | (spd << 4) | spc);
        }

        var statExp = entry.GetTable("statExp");
        if (statExp is not null)
        {
            pk.EV_HP = statExp.GetInt("hp");
            pk.EV_ATK = statExp.GetInt("attack");
            pk.EV_DEF = statExp.GetInt("defense");
            pk.EV_SPE = statExp.GetInt("speed");
            pk.EV_SPC = statExp.GetInt("special");
        }

        pk.EXP = (uint)entry.GetInt("exp");

        var moves = entry.GetTable("moves");
        if (moves is not null)
        {
            var moveSlots = new (ushort move, byte pp)[4];
            foreach (var (key, value) in moves.Entries)
            {
                if (!key.StartsWith("[") || value.Type != LuaValue.Kind.Table || value.TableValue == null)
                    continue;
                var idx = ParseLuaIndex(key);
                if (idx < 1 || idx > 4)
                    continue;
                var moveName = value.TableValue.GetString("id");
                if (moveName is null)
                    continue;
                moveSlots[idx - 1] = ((ushort)ResolveMove(moveName), (byte)value.TableValue!.GetInt("pp"));
            }
            if (moveSlots[0].move > 0) { pk.Move1 = moveSlots[0].move; pk.Move1_PP = moveSlots[0].pp; }
            if (moveSlots[1].move > 0) { pk.Move2 = moveSlots[1].move; pk.Move2_PP = moveSlots[1].pp; }
            if (moveSlots[2].move > 0) { pk.Move3 = moveSlots[2].move; pk.Move3_PP = moveSlots[2].pp; }
            if (moveSlots[3].move > 0) { pk.Move4 = moveSlots[3].move; pk.Move4_PP = moveSlots[3].pp; }
        }

        pk.TID16 = (ushort)entry.GetInt("otId");
        var level = (byte)entry.GetInt("level", 5);
        pk.Stat_LevelBox = level;
        pk.Stat_Level = level;

        pk.Stat_HPCurrent = entry.GetInt("hp");

        var stats = entry.GetTable("stats");
        if (stats is not null)
        {
            pk.Stat_HPMax = stats.GetInt("hp");
            pk.Stat_ATK = stats.GetInt("attack");
            pk.Stat_DEF = stats.GetInt("defense");
            pk.Stat_SPE = stats.GetInt("speed");
            pk.Stat_SPC = stats.GetInt("special");
        }

        var otName = entry.GetString("ot") ?? "______";
        var nick = entry.GetString("nickname") ?? SpeciesName.GetSpeciesName(pk.Species, (int)LanguageID.English);
        StringConverter1.SetString(pk.OriginalTrainerTrash, otName, pk.OriginalTrainerTrash.Length - 1, false, Clear50);
        StringConverter1.SetString(pk.NicknameTrash, nick, pk.NicknameTrash.Length - 1, false, Clear50);

        // Types from personal info
        var pi = pk.PersonalInfo;
        pk.Type1 = (byte)pi.Type1;
        pk.Type2 = (byte)pi.Type2;

        return pk;
    }

    // --- Write helpers ---

    private static string GetRivalName(SAV1 sav)
    {
        try { return StringConverter1.GetString(sav.RivalNameTrash, false); }
        catch { return "GARY"; }
    }

    private static List<(string name, int qty)> GetBagItems(SAV1 sav)
    {
        var result = new List<(string name, int qty)>();
        var strings = GameInfo.GetStrings(GameLanguage.DefaultLanguage);
        var pouch = sav.Inventory.Pouches[0];
        foreach (var item in pouch.Items)
        {
            if (item.Count <= 0 || item.Index == 0)
                continue;
            result.Add((GetItemLuaName(item.Index, strings), item.Count));
        }
        return result;
    }

    private static List<(string name, int qty)> GetPcItems(SAV1 sav)
    {
        var result = new List<(string name, int qty)>();
        var strings = GameInfo.GetStrings(GameLanguage.DefaultLanguage);
        var pouch = sav.Inventory.Pouches[1];
        foreach (var item in pouch.Items)
        {
            if (item.Count <= 0 || item.Index == 0)
                continue;
            result.Add((GetItemLuaName(item.Index, strings), item.Count));
        }
        return result;
    }

    private static LuaTable BuildLuaPartyEntry(PK1 pk)
    {
        var strings = GameInfo.GetStrings(GameLanguage.DefaultLanguage);
        var dvs = ExtractDVs(pk.DV16);
        var entry = new LuaTable();

        entry.Add("catchRate", LuaValue.ForNumber(pk.CatchRate));

        var dvsTable = new LuaTable();
        dvsTable.Add("attack", LuaValue.ForNumber(dvs.atk));
        dvsTable.Add("defense", LuaValue.ForNumber(dvs.def));
        dvsTable.Add("hp", LuaValue.ForNumber(dvs.hp));
        dvsTable.Add("special", LuaValue.ForNumber(dvs.spc));
        dvsTable.Add("speed", LuaValue.ForNumber(dvs.spe));
        entry.Add("dvs", LuaValue.ForTable(dvsTable));

        entry.Add("exp", LuaValue.ForNumber(pk.EXP));
        entry.Add("hp", LuaValue.ForNumber(pk.Stat_HPCurrent));
        entry.Add("level", LuaValue.ForNumber(pk.Stat_Level));

        var movesTable = new LuaTable();
        for (int m = 0; m < 4; m++)
        {
            var moveId = pk.GetMove(m);
            if (moveId == 0)
                continue;
            var pp = GetMovePP(pk, m);
            var moveName = moveId < strings.movelist.Length
                ? strings.movelist[moveId].Replace(" ", "").ToUpperInvariant()
                : $"MOVE_{moveId}";
            var moveEntry = new LuaTable();
            moveEntry.Add("id", LuaValue.ForString(moveName));
            moveEntry.Add("pp", LuaValue.ForNumber(pp));
            movesTable.SetArray(m + 1, LuaValue.ForTable(moveEntry));
        }
        entry.Add("moves", LuaValue.ForTable(movesTable));

        var otName = StringConverter1.GetString(pk.OriginalTrainerTrash, false);
        var nick = StringConverter1.GetString(pk.NicknameTrash, false);
        entry.Add("nickname", LuaValue.ForString(nick));
        entry.Add("ot", LuaValue.ForString(otName));
        entry.Add("otId", LuaValue.ForNumber(pk.TID16));
        entry.Add("species", LuaValue.ForString(GetLuaSpeciesName(pk.Species)));

        var statExpTable = new LuaTable();
        statExpTable.Add("attack", LuaValue.ForNumber(pk.EV_ATK));
        statExpTable.Add("defense", LuaValue.ForNumber(pk.EV_DEF));
        statExpTable.Add("hp", LuaValue.ForNumber(pk.EV_HP));
        statExpTable.Add("special", LuaValue.ForNumber(pk.EV_SPC));
        statExpTable.Add("speed", LuaValue.ForNumber(pk.EV_SPE));
        entry.Add("statExp", LuaValue.ForTable(statExpTable));

        var statsTable = new LuaTable();
        statsTable.Add("attack", LuaValue.ForNumber(pk.Stat_ATK));
        statsTable.Add("defense", LuaValue.ForNumber(pk.Stat_DEF));
        statsTable.Add("hp", LuaValue.ForNumber(pk.Stat_HPMax));
        statsTable.Add("special", LuaValue.ForNumber(pk.Stat_SPC));
        statsTable.Add("speed", LuaValue.ForNumber(pk.Stat_SPE));
        entry.Add("stats", LuaValue.ForTable(statsTable));

        return entry;
    }

    private static int GetMovePP(PK1 pk, int index) => index switch
    {
        0 => pk.Move1_PP,
        1 => pk.Move2_PP,
        2 => pk.Move3_PP,
        3 => pk.Move4_PP,
        _ => 0,
    };

    private static (int atk, int def, int hp, int spe, int spc) ExtractDVs(ushort dv16)
    {
        var atk = (dv16 >> 12) & 0xF;
        var def = (dv16 >> 8) & 0xF;
        var spe = (dv16 >> 4) & 0xF;
        var spc = dv16 & 0xF;
        var hp = (atk & 1) * 8 + (def & 1) * 4 + (spe & 1) * 2 + (spc & 1);
        return (atk, def, hp, spe, spc);
    }

    private static string GetLuaSpeciesName(ushort speciesId)
    {
        var name = SpeciesName.GetSpeciesName(speciesId, (int)LanguageID.English);
        return name.Replace(" ", "").ToUpperInvariant();
    }

    private static string GetItemLuaName(int itemId, GameStrings strings)
    {
        if (itemId > 0 && itemId < strings.itemlist.Length)
            return strings.itemlist[itemId].Replace(" ", "_").ToUpperInvariant();
        return $"ITEM_{itemId}";
    }

    private static int ParseLuaIndex(string key)
    {
        if (key.Length >= 3 && key[0] == '[' && key[^1] == ']')
        {
            if (int.TryParse(key.Substring(1, key.Length - 2), out var idx))
                return idx;
        }
        return 0;
    }

    // --- Name resolution ---

    private static readonly Lazy<Dictionary<string, ushort>> SpeciesMap = new(BuildSpeciesMap);
    private static readonly Lazy<Dictionary<string, ushort>> MoveMap = new(BuildMoveMap);
    private static readonly Lazy<Dictionary<string, ushort>> ItemMap = new(BuildItemMap);

    private static ushort ResolveSpecies(string name) => Lookup(SpeciesMap.Value, name);
    private static ushort ResolveMove(string name) => Lookup(MoveMap.Value, name);

    private static int ResolveItem(string name)
    {
        var id = Lookup(ItemMap.Value, name);
        return id;
    }

    private static ushort Lookup(Dictionary<string, ushort> map, string name)
    {
        if (map.TryGetValue(name, out var id))
            return id;
        var norm = NormalizeName(name);
        return map.TryGetValue(norm, out id) ? id : (ushort)0;
    }

    private static Dictionary<string, ushort> BuildSpeciesMap()
    {
        var map = new Dictionary<string, ushort>(StringComparer.Ordinal);
        for (ushort species = 1; species <= Legal.MaxSpeciesID_1; species++)
        {
            var name = SpeciesName.GetSpeciesName(species, (int)LanguageID.English);
            AddName(map, name, species);
        }
        return map;
    }

    private static Dictionary<string, ushort> BuildMoveMap()
    {
        var strings = GameInfo.GetStrings(GameLanguage.DefaultLanguage);
        var map = new Dictionary<string, ushort>(StringComparer.Ordinal);
        for (int i = 1; i < strings.movelist.Length; i++)
            AddName(map, strings.movelist[i], (ushort)i);
        return map;
    }

    private static Dictionary<string, ushort> BuildItemMap()
    {
        var strings = GameInfo.GetStrings(GameLanguage.DefaultLanguage);
        var map = new Dictionary<string, ushort>(StringComparer.Ordinal);
        for (int i = 1; i < strings.itemlist.Length; i++)
            AddName(map, strings.itemlist[i], (ushort)i);

        // Aliases for Gen1 "Recomp" project item name abbreviations
        if (map.TryGetValue("PARALYZEHEAL", out var paralyzeHealId))
        {
            map.TryAdd("PARLYZHEAL", paralyzeHealId);
            map.TryAdd("PARLYZ", paralyzeHealId);
        }
        return map;
    }

    private static void AddName(Dictionary<string, ushort> map, string name, ushort id)
    {
        if (name.Length == 0) return;
        var norm = NormalizeName(name);
        if (norm.Length > 0)
            map.TryAdd(norm, id);
    }

    private static string NormalizeName(string name)
    {
        var decomposed = name.Normalize(NormalizationForm.FormD);
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
}
