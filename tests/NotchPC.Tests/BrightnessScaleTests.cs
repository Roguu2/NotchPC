using NotchPC.Core.Display;

namespace NotchPC.Tests;

public sealed class BrightnessScaleTests
{
    [Theory]
    [InlineData(0u, 0u, 100u, 0)]
    [InlineData(0u, 100u, 100u, 100)]
    [InlineData(0u, 37u, 100u, 37)]
    [InlineData(0u, 50u, 255u, 20)]   // monitors with a 0..255 range
    [InlineData(10u, 55u, 100u, 50)]  // range that does not start at 0
    [InlineData(0u, 300u, 100u, 100)] // out-of-range reading is clamped
    public void Monitor_value_to_percent(uint min, uint current, uint max, int expected) =>
        Assert.Equal(expected, BrightnessScale.ToPercent(min, current, max));

    [Fact]
    public void Broken_range_gives_no_percent() => Assert.Null(BrightnessScale.ToPercent(50, 50, 50));

    [Theory]
    [InlineData(0, 0u, 100u, 0u)]
    [InlineData(100, 0u, 100u, 100u)]
    [InlineData(50, 0u, 255u, 128u)]
    [InlineData(50, 10u, 100u, 55u)]
    [InlineData(150, 0u, 100u, 100u)]
    [InlineData(-5, 0u, 100u, 0u)]
    public void Percent_to_monitor_value(int percent, uint min, uint max, uint expected) =>
        Assert.Equal(expected, BrightnessScale.FromPercent(percent, min, max));

    [Fact]
    public void Round_trip_keeps_the_percent()
    {
        for (var p = 0; p <= 100; p++)
        {
            Assert.Equal(p, BrightnessScale.ToPercent(0, BrightnessScale.FromPercent(p, 0, 100), 100));
        }
    }
}
