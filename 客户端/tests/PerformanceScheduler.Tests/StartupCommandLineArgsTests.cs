using PerformanceScheduler.App.Services;

namespace PerformanceScheduler.Tests;

public sealed class StartupCommandLineArgsTests
{
    [Fact]
    public void Parse_NullOrEmptyArgs_ReturnsNonSilent()
    {
        var result1 = StartupCommandLineArgs.Parse(null);
        Assert.False(result1.IsSilent);

        var result2 = StartupCommandLineArgs.Parse([]);
        Assert.False(result2.IsSilent);
    }

    [Theory]
    [InlineData("--silent")]
    [InlineData("--SILENT")]
    [InlineData("-silent")]
    [InlineData("/silent")]
    [InlineData("/SILENT")]
    [InlineData("--minimized")]
    [InlineData("--MINIMIZED")]
    [InlineData("/minimized")]
    public void Parse_SilentFlags_ReturnsSilentTrue(string flag)
    {
        var result = StartupCommandLineArgs.Parse([flag]);
        Assert.True(result.IsSilent);
    }

    [Fact]
    public void Parse_MixedArgsWithSilent_ReturnsSilentTrue()
    {
        var result = StartupCommandLineArgs.Parse(["--other-arg", "foo", "--silent", "--debug"]);
        Assert.True(result.IsSilent);
    }

    [Fact]
    public void Parse_OtherArgsWithoutSilent_ReturnsSilentFalse()
    {
        var result = StartupCommandLineArgs.Parse(["--normal", "--foreground", "true"]);
        Assert.False(result.IsSilent);
    }
}
