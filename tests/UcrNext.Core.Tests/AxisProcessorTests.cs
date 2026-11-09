namespace UcrNext.Core.Tests;

public class AxisProcessorTests
{
    [Theory]
    [InlineData(0.05, 0.1, 0.0)]
    [InlineData(-0.1, 0.1, 0.0)]
    [InlineData(1.0, 0.1, 1.0)]
    [InlineData(-1.0, 0.2, -1.0)]
    [InlineData(0.55, 0.1, 0.5)]
    public void Deadzone_is_removed_and_range_rescaled(double input, double dz, double expected)
    {
        var v = AxisProcessor.Shape(input, new AxisSettings { Deadzone = dz });
        Assert.Equal(expected, v, 6);
    }

    [Fact]
    public void Invert_flips_sign()
    {
        Assert.Equal(-0.5, AxisProcessor.Shape(0.5, new AxisSettings { Invert = true }), 6);
    }

    [Fact]
    public void Sensitivity_scales_and_clamps()
    {
        Assert.Equal(0.8, AxisProcessor.Shape(0.4, new AxisSettings { Sensitivity = 2 }), 6);
        Assert.Equal(1.0, AxisProcessor.Shape(0.9, new AxisSettings { Sensitivity = 2 }), 6);
    }

    [Fact]
    public void Stick_and_trigger_conversion_hit_the_extremes()
    {
        Assert.Equal(short.MaxValue, AxisProcessor.ToStick(1));
        Assert.Equal(short.MinValue, AxisProcessor.ToStick(-1));
        Assert.Equal(0, AxisProcessor.ToStick(0));
        Assert.Equal(255, AxisProcessor.ToTrigger(1));
        Assert.Equal(0, AxisProcessor.ToTrigger(-0.5));
    }
}
