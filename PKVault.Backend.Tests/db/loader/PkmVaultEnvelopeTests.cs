using System.Text;
using PKHeX.Core;
using Xunit;

public class PkmVaultEnvelopeTests
{
    [Fact]
    public void Wrap_Unwrap_RoundTripsRaw()
    {
        var pkm = BlankSaveFile.Get(EntityContext.Gen9a).BlankPKM;
        var raw = new byte[pkm.SIZE_PARTY];
        pkm.WriteDecryptedDataParty(raw);

        var wrapped = PkmVaultEnvelope.Wrap(raw, EntityContext.Gen9a, "26.08.12");

        Assert.True(PkmVaultEnvelope.IsEnveloped(wrapped));

        var ok = PkmVaultEnvelope.TryUnwrap(wrapped, out var outRaw, out var ctx, out var ver, out var err);

        Assert.True(ok);
        Assert.Null(err);
        Assert.Equal(EntityContext.Gen9a, ctx);
        Assert.Equal("26.08.12", ver);
        Assert.Equal(raw, outRaw);
    }

    [Fact]
    public void Wrap_Unwrap_PreservesRawContainingDelimiters()
    {
        // Raw binary may contain the delimiter byte; ensure the header boundary is still found.
        var raw = new byte[] { 1, (byte)'|', 2, (byte)'|', 3, 0, 255 };

        var wrapped = PkmVaultEnvelope.Wrap(raw, EntityContext.Gen3, "26.08.12");
        var ok = PkmVaultEnvelope.TryUnwrap(wrapped, out var outRaw, out var ctx, out var ver, out var err);

        Assert.True(ok);
        Assert.Null(err);
        Assert.Equal(EntityContext.Gen3, ctx);
        Assert.Equal("26.08.12", ver);
        Assert.Equal(raw, outRaw);
    }

    [Fact]
    public void Unwrap_Legacy_ReturnsBytesUntouched()
    {
        var legacy = new byte[] { 1, 2, 3, 4 };

        var ok = PkmVaultEnvelope.TryUnwrap(legacy, out var raw, out var ctx, out var ver, out var err);

        Assert.True(ok);
        Assert.Null(err);
        Assert.Null(ctx);
        Assert.Null(ver);
        Assert.Equal(legacy, raw);
    }

    [Fact]
    public void Unwrap_MalformedHeader_Fails()
    {
        var bad = Encoding.UTF8.GetBytes("PKVAULT:1|Garbage");

        var ok = PkmVaultEnvelope.TryUnwrap(bad, out _, out _, out _, out var err);

        Assert.False(ok);
        Assert.NotNull(err);
    }

    [Fact]
    public void Unwrap_UnsupportedFormatVersion_Fails()
    {
        var raw = new byte[] { 9, 9, 9 };
        var wrapped = PkmVaultEnvelope.Wrap(raw, EntityContext.Gen9a, "26.08.12");
        wrapped[8] = (byte)'9'; // bump "PKVAULT:1" -> "PKVAULT:9"

        var ok = PkmVaultEnvelope.TryUnwrap(wrapped, out _, out _, out _, out var err);

        Assert.False(ok);
        Assert.Contains("format version", err!);
    }
}
