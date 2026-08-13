using PKHeX.Core;
using Xunit;

public class IFBoxLoadTests
{
    [Fact]
    public void IFBoxesPopulated()
    {
        var path = @"C:\Users\salmo\Documents\GitHub\PKVaultInfinite\InfiniteSave\File A.rxdata";
        var data = File.ReadAllBytes(path);
        var ok = SaveUtil.TryGetSaveFile(data, out var save, path);
        Assert.True(ok, "save not detected");
        var sav = Assert.IsType<SAV_InfiniteFusion>(save);

        int total = 0, nonEmpty = 0;
        for (int b = 0; b < sav.BoxCount; b++)
            for (int s = 0; s < sav.BoxSlotCount; s++)
            {
                total++;
                var pk = sav.GetBoxSlotAtIndex(b, s);
                if (pk is { Species: not 0 })
                    nonEmpty++;
            }

        // Sample save: ~373 stored + 6 party; boxes should be far from empty.
        Assert.True(nonEmpty > 300, $"expected many populated box slots, got {nonEmpty}/{total}");
    }
}
