using Azki.Core.Platform;

namespace Azki.Core.Tests;

public sealed class WindowsBuildRequirementTests
{
    [Theory]
    [InlineData(26300)]
    [InlineData(26310)]
    [InlineData(28000)]
    public void Build26300AndNewerIsSupported(int build)
    {
        var version = new Version(10, 0, build, 0);

        Assert.Equal(WindowsBuildStatus.Supported, WindowsBuildRequirement.Evaluate(version));
        Assert.False(WindowsBuildRequirement.ShouldWarn(version));
        Assert.Null(WindowsBuildRequirement.WarningMessage(version));
    }

    [Theory]
    [InlineData(22000)]
    [InlineData(22631)]
    [InlineData(26100)]
    [InlineData(26299)]
    public void OlderWindows11BuildsWarnButAreNotBlocked(int build)
    {
        var version = new Version(10, 0, build, 0);

        Assert.Equal(WindowsBuildStatus.OlderThanRecommended, WindowsBuildRequirement.Evaluate(version));
        Assert.True(WindowsBuildRequirement.ShouldWarn(version));
        Assert.Contains(build.ToString(System.Globalization.CultureInfo.InvariantCulture), WindowsBuildRequirement.WarningMessage(version));
    }

    [Theory]
    [InlineData(10, 19045)]
    [InlineData(6, 9600)]
    public void Windows10AndOlderAreReportedSeparately(int major, int build)
    {
        var version = new Version(major, 0, build, 0);

        Assert.Equal(WindowsBuildStatus.BelowWindows11, WindowsBuildRequirement.Evaluate(version));
        Assert.True(WindowsBuildRequirement.ShouldWarn(version));
    }

    [Fact]
    public void RecommendedMinimumIsWindows11Version26H2()
    {
        Assert.Equal(26300, WindowsBuildRequirement.RecommendedMinimumBuild);
    }

    [Fact]
    public void EvaluateRejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => WindowsBuildRequirement.Evaluate(null!));
    }
}
