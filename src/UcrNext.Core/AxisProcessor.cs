namespace UcrNext.Core;

public static class AxisProcessor
{
    /// <summary>Applies invert, deadzone (with rescale) and sensitivity, then clamps to -1..1.</summary>
    public static double Shape(double value, AxisSettings s)
    {
        double v = Math.Clamp(value, -1.0, 1.0);
        if (s.Invert) v = -v;

        double dz = Math.Clamp(s.Deadzone, 0.0, 0.95);
        double mag = Math.Abs(v);
        if (mag <= dz) return 0.0;
        // Rescale so output starts at 0 just outside the deadzone and still reaches 1.
        v = Math.Sign(v) * (mag - dz) / (1.0 - dz);

        v *= s.Sensitivity;
        return Math.Clamp(v, -1.0, 1.0);
    }

    /// <summary>Converts a stick value -1..1 to the signed 16-bit range used by XInput/ViGEm.</summary>
    public static short ToStick(double v)
    {
        v = Math.Clamp(v, -1.0, 1.0);
        return (short)Math.Round(v >= 0 ? v * short.MaxValue : v * -(double)short.MinValue);
    }

    /// <summary>Converts a trigger value 0..1 to 0..255.</summary>
    public static byte ToTrigger(double v) => (byte)Math.Round(Math.Clamp(v, 0.0, 1.0) * 255.0);
}
