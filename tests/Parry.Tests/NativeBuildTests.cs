using Fahrenheit.Mods.Parry;
using Xunit;

public sealed class NativeBuildTests
{
    [Fact]
    public void AcceptsExactExecutableIdentity()
    {
        Assert.True(NativeBuild.IsSupportedIdentity(10687744,
            "0537b2a1047f3266e73495cd4e35f63f0777f4231d417699f979954686da686d"));
        Assert.True(NativeBuild.IsSupportedIdentity(NativeBuild.ImageLength, NativeBuild.ImageSha256.ToUpperInvariant()));
    }

    [Theory]
    [InlineData(10687743, "0537b2a1047f3266e73495cd4e35f63f0777f4231d417699f979954686da686d")]
    [InlineData(10687744, "1537b2a1047f3266e73495cd4e35f63f0777f4231d417699f979954686da686d")]
    [InlineData(10687744, "")]
    public void RejectsUnknownExecutableIdentity(long length, string hash)
        => Assert.False(NativeBuild.IsSupportedIdentity(length, hash));

    [Fact]
    public void RejectsTestHostBeforeNativeIntegration()
    {
        Assert.False(NativeBuild.TryValidate(out string error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void UsesReviewedCurrentBuildBindings()
    {
        Assert.Equal(0x003B2290, ExternalMemoryOffsetMap.Functions.MsExeInputCue);
        Assert.Equal(0x0038F060, ExternalMemoryOffsetMap.Functions.MsSetDamageInternal);
        Assert.Equal(0x003ACEB0, ExternalMemoryOffsetMap.Functions.MsActionRequest);
        Assert.Equal(0x003B0D50, ExternalMemoryOffsetMap.Functions.MsLimitTypeDamageCheck);
        Assert.Equal(0x0046DF00, ExternalMemoryOffsetMap.Functions.AtelPopStackInteger);
        Assert.Equal(0x00479760, ExternalMemoryOffsetMap.Functions.AtelGetEventName);
    }
}
