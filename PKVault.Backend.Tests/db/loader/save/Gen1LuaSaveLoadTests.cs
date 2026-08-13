using PKHeX.Core;
using PKHeX.Core.Saves.Gen1.Lua;
using Xunit;

/// <summary>
/// Tests that the Gen1 "Recomp" Lua save format (DevSaves/slot1.lua) loads correctly
/// as a SAV1 through the LuaSaveReader custom reader.
/// </summary>
public class Gen1LuaSaveLoadTests
{
    private const string Slot1Path = @"C:\Users\salmo\Documents\GitHub\PKVaultInfinite\DevSaves\slot1.lua";

    [Fact]
    public void LuaSaveDetectedAsSAV1()
    {
        if (!File.Exists(Slot1Path))
        {
            // Skip the test if the dev save is not present on this machine
            return;
        }

        var data = File.ReadAllBytes(Slot1Path);
        var ok = SaveUtil.TryGetSaveFile(data, out var save, Slot1Path);
        Assert.True(ok, "slot1.lua was not detected as a save file");
        var sav = Assert.IsType<SAV1>(save);

        // Yellow version
        Assert.Equal(GameVersion.YW, sav.Version);
        Assert.Equal(1, sav.Generation);
        Assert.True(sav.HasPokeDex);
    }

    [Fact]
    public void LuaSaveMoneyAndOT()
    {
        if (!File.Exists(Slot1Path)) return;

        var data = File.ReadAllBytes(Slot1Path);
        SaveUtil.TryGetSaveFile(data, out var save, Slot1Path);
        var sav = Assert.IsType<SAV1>(save);

        Assert.Equal(125u, sav.Money);
        Assert.Equal("YELLOW", sav.OT);
        Assert.Equal(63884, sav.TID16);
    }

    [Fact]
    public void LuaSavePartyPokemon()
    {
        if (!File.Exists(Slot1Path)) return;

        var data = File.ReadAllBytes(Slot1Path);
        SaveUtil.TryGetSaveFile(data, out var save, Slot1Path);
        var sav = Assert.IsType<SAV1>(save);

        Assert.True(sav.HasParty, "SAV1 should have party");
        Assert.Equal(1, sav.PartyCount);

        var pk = sav.PartyData[0] as PK1;
        Assert.NotNull(pk);
        Assert.Equal(25, pk!.Species); // Pikachu = #25
        Assert.Equal(6, pk.Stat_Level);
        Assert.Equal("PEEPER", StringConverter1.GetString(pk.NicknameTrash, false));

        // Moves: THUNDERSHOCK, GROWL, TAIL_WHIP
        Assert.NotEqual(0, pk.Move1);
        Assert.NotEqual(0, pk.Move2);
        Assert.NotEqual(0, pk.Move3);
        Assert.Equal(0, pk.Move4);
    }

    [Fact]
    public void LuaSavePokedex()
    {
        if (!File.Exists(Slot1Path)) return;

        var data = File.ReadAllBytes(Slot1Path);
        SaveUtil.TryGetSaveFile(data, out var save, Slot1Path);
        var sav = Assert.IsType<SAV1>(save);

        // PIKACHU should be caught, others seen but not caught
        Assert.True(sav.GetCaught(25), "PIKACHU should be caught");
        Assert.True(sav.GetSeen(25), "PIKACHU should be seen");
        Assert.True(sav.GetSeen(16), "Rattata (#16, first in seen list) should be seen");
        Assert.False(sav.GetCaught(16), "Rattata should not be caught");
    }

    [Fact]
    public void LuaSaveRoundTrip()
    {
        if (!File.Exists(Slot1Path)) return;

        var original = File.ReadAllBytes(Slot1Path);
        SaveUtil.TryGetSaveFile(original, out var save, Slot1Path);
        var sav = Assert.IsType<SAV1>(save);

        // Add money and re-serialize via LuaSaveWrapper write-back path
        sav.Money = 999;

        var result = LuaSaveConverter.SAV1ToLua(sav);
        Assert.True(result.Length > 0, "Lua output should not be empty");

        var text = System.Text.Encoding.UTF8.GetString(result);
        Assert.Contains("return {", text, StringComparison.Ordinal);
        Assert.Contains("money = 999", text, StringComparison.Ordinal);
        Assert.Contains("version = \"yellow\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void LuaSaveEventFlags()
    {
        if (!File.Exists(Slot1Path)) return;

        var data = File.ReadAllBytes(Slot1Path);
        SaveUtil.TryGetSaveFile(data, out var save, Slot1Path);
        var sav = Assert.IsType<SAV1>(save);

        // The sample has 12 EVENT_* flags set; verify at least some are set
        // EVENT_GOT_POKEDEX maps to bit 7 in our map
        Assert.True(sav.GetEventFlag(7), "EVENT_GOT_POKEDEX should be set");
    }

    [Fact]
    public void LuaSaveInventory()
    {
        if (!File.Exists(Slot1Path)) return;

        var data = File.ReadAllBytes(Slot1Path);
        SaveUtil.TryGetSaveFile(data, out var save, Slot1Path);
        var sav = Assert.IsType<SAV1>(save);

        var bag = sav.Inventory;
        var pouch = bag.Pouches[0]; // Items pouch
        int itemCount = pouch.Count;
        Assert.True(itemCount >= 4, $"Expected at least 4 bag items, got {itemCount}");

        // Check for Poke Ball (item ID 4 in Gen1 = POKE_BALL)
        var hasPokeBall = pouch.Items.Any(i => i.Index == 4 && i.Count > 0);
        Assert.True(hasPokeBall, "Should have Poke Balls in bag");
    }
}
