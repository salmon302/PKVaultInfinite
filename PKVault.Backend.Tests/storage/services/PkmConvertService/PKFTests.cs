using System;
using Moq;
using PKHeX.Core;
using Xunit;

public class PKFTests
{
    private static PkmConvertService GetService()
    {
        Mock<ISettingsService> mockSettingsService = new();
        mockSettingsService.Setup(x => x.GetSettings()).Returns(new SettingsDTO(
            BuildID: default, RuntimeSystem: RuntimeSystem.LINUX, Version: "", PkhexVersion: "", AppDirectory: "", SettingsPath: "", UserId: "",
            CanUpdateSettings: false, CanScanSaves: false, SettingsMutable: new(
                DB_PATH: "", SAVE_GLOBS: [], PKM_EXTERNAL_GLOBS: [], STORAGE_PATH: "", BACKUP_PATH: "",
                LANGUAGE: "en", HIDE_CHEATS: false, SKIP_LEGALITY_CHECKS: false
            )
        ));
        return new(LoggerUtils.GetLogger<PkmConvertService>(), mockSettingsService.Object, new LegalityAnalysisService(mockSettingsService.Object));
    }

    [Fact]
    public void PKF_HeadBodySurviveEncryptRoundTrip()
    {
        var pk = new PKF
        {
            Species = 25, // Pikachu (head)
            HeadSpecies = 25,
            BodySpecies = 494, // Scrafty (body)
        };
        pk.RefreshChecksum();

        var stored = new byte[pk.SIZE_STORED];
        pk.WriteEncryptedDataStored(stored);

        var back = new PKF(stored);
        Assert.Equal((ushort)25, back.HeadSpecies);
        Assert.Equal((ushort)494, back.BodySpecies);
        Assert.Equal((ushort)25, back.Species);
        Assert.True(back.IsFusion);
    }

    [Fact]
    public void PKF_NonFusion_HasZeroBody()
    {
        var pk = new PKF { Species = 25, HeadSpecies = 25, BodySpecies = 0 };
        Assert.False(pk.IsFusion);

        var stored = new byte[pk.SIZE_STORED];
        pk.WriteEncryptedDataStored(stored);

        var back = new PKF(stored);
        Assert.Equal((ushort)0, back.BodySpecies);
        Assert.False(back.IsFusion);
    }

    [Fact]
    public void PKF_IntraContextConvertSucceeds()
    {
        var pk = new PKF { Species = 25, HeadSpecies = 25, BodySpecies = 494 };
        var service = GetService();

        var result = service.ConvertTo(new ImmutablePKM(pk), typeof(PKF), null);

        var outPk = result.GetMutablePkm();
        Assert.IsType<PKF>(outPk);
        var pkf = (PKF)outPk;
        Assert.Equal((ushort)25, pkf.HeadSpecies);
        Assert.Equal((ushort)494, pkf.BodySpecies);
    }

    [Fact]
    public void PKF_CrossContextConvertBlocked()
    {
        var pk = new PKF { Species = 25, HeadSpecies = 25, BodySpecies = 494 };
        var service = GetService();

        Assert.Throws<InvalidOperationException>(() =>
            service.ConvertTo(new ImmutablePKM(pk), typeof(PK9), null));
    }
}
