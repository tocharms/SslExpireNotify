using SslExpireNotify.Worker.Options;
using SslExpireNotify.Worker.Services;
using Xunit;

namespace SslExpireNotify.Tests;

public class AlertLevelResolverTests
{
    [Theory]
    [InlineData(31, null)]      // outside every threshold
    [InlineData(300, null)]
    [InlineData(30, "NOTICE")]  // exactly on the boundary
    [InlineData(16, "NOTICE")]
    [InlineData(15, "WARNING")]
    [InlineData(8, "WARNING")]
    [InlineData(7, "URGENT")]
    [InlineData(1, "URGENT")]
    [InlineData(0, "EXPIRED")]  // expires today
    [InlineData(-1, "EXPIRED")]
    [InlineData(-400, "EXPIRED")]
    public void Resolve_picks_the_most_severe_matching_level(int daysRemaining, string? expected)
    {
        var resolver = TestData.DefaultResolver();

        var level = resolver.Resolve(daysRemaining);

        Assert.Equal(expected, level?.Level);
    }

    [Fact]
    public void Resolve_honours_a_custom_level_table_without_a_rebuild()
    {
        // Levels are pure configuration: adding one must work with no code change.
        var resolver = new AlertLevelResolver(
        [
            new AlertLevelOptions { Level = "EARLY",  Days = 60, Severity = 1, RepeatEveryDays = 14 },
            new AlertLevelOptions { Level = "NOTICE", Days = 30, Severity = 2, RepeatEveryDays = 7 },
            new AlertLevelOptions { Level = "FINAL",  Days = 0,  Severity = 3, RepeatEveryDays = 1 }
        ]);

        Assert.Equal("EARLY", resolver.Resolve(45)?.Level);
        Assert.Equal("NOTICE", resolver.Resolve(30)?.Level);
        Assert.Equal("FINAL", resolver.Resolve(-2)?.Level);
        Assert.Null(resolver.Resolve(61));
    }

    [Fact]
    public void SeverityOf_ranks_an_unknown_level_below_every_configured_one()
    {
        var resolver = TestData.DefaultResolver();

        Assert.True(resolver.SeverityOf("LEVEL_REMOVED_FROM_CONFIG") < resolver.SeverityOf("NOTICE"));
    }
}
