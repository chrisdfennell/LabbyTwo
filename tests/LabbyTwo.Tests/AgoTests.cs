using LabbyTwo.Core;

namespace LabbyTwo.Tests;

public class AgoTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 9, 20, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, "just now")]
    [InlineData(30, "just now")]
    [InlineData(60, "1m ago")]
    [InlineData(59 * 60, "59m ago")]
    [InlineData(60 * 60, "1h ago")]
    [InlineData(3 * 60 * 60 + 5 * 60, "3h 5m ago")]
    [InlineData(25 * 24 * 60 * 60 + 21 * 60 * 60, "25d 21h ago")]
    [InlineData(29 * 24 * 60 * 60, "29d ago")]
    public void Reads_as_two_units_at_most(int secondsAgo, string expected) =>
        Assert.Equal(expected, Ago.Since(Now.AddSeconds(-secondsAgo), Now));

    [Fact]
    public void A_missing_timestamp_is_not_rendered_as_the_year_one() =>
        Assert.Equal(Ago.Unknown, Ago.Since(DateTimeOffset.MinValue, Now));

    [Fact]
    public void A_timestamp_in_the_future_reads_as_now_rather_than_negative() =>
        Assert.Equal("just now", Ago.Since(Now.AddMinutes(5), Now));
}
