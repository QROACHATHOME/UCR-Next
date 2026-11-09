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

        double adz = Math.Clamp(s.AntiDeadzone, 0.0, 0.95);
        if (adz > 0) v = Math.Sign(v) * (adz + (1.0 - adz) * Math.Abs(v));

        return Math.Clamp(ApplySensitivity(v, s), -1.0, 1.0);
    }

    /// <summary>
    /// Linear: v × sensitivity. Non-linear (UCR's curve): s·v + (1-s)·v³ with s = percentage/10000,
    /// i.e. mostly cubic — exactly what UCR's SensitivityHelper does.
    /// </summary>
    public static double ApplySensitivity(double v, AxisSettings s)
    {
        if (!s.NonLinearSensitivity) return v * s.Sensitivity;
        double k = s.Sensitivity / 100.0; // Sensitivity is stored as percentage / 100
        return k * v + (1 - k) * v * v * v;
    }

    /// <summary>Per-axis deadzone with rescale (UCR DeadZoneHelper).</summary>
    public static double Deadzone(double v, double dz)
    {
        dz = Math.Clamp(dz, 0.0, 0.95);
        double mag = Math.Abs(v);
        return mag <= dz ? 0.0 : Math.Sign(v) * (mag - dz) / (1.0 - dz);
    }

    /// <summary>Radial deadzone with rescale (UCR CircularDeadZoneHelper).</summary>
    public static (double X, double Y) CircularDeadzone(double x, double y, double dz)
    {
        dz = Math.Clamp(dz, 0.0, 0.95);
        double len = Math.Sqrt(x * x + y * y);
        if (len <= dz || len == 0) return (0, 0);
        double scale = (len - dz) / (1 - dz) / len;
        return (Math.Clamp(x * scale, -1, 1), Math.Clamp(y * scale, -1, 1));
    }

    /// <summary>Axis splitter half: High maps 0..1 → -1..1 (negative input = -1); Low maps 0..-1 → -1..1.</summary>
    public static double Split(double v, AxisHalf half) => half switch
    {
        AxisHalf.High => v <= 0 ? -1 : Math.Clamp(v * 2 - 1, -1, 1),
        AxisHalf.Low => v >= 0 ? -1 : Math.Clamp(-v * 2 - 1, -1, 1),
        _ => v,
    };

    /// <summary>Converts a stick value -1..1 to the signed 16-bit range used by XInput/ViGEm.</summary>
    public static short ToStick(double v)
    {
        v = Math.Clamp(v, -1.0, 1.0);
        return (short)Math.Round(v >= 0 ? v * short.MaxValue : v * -(double)short.MinValue);
    }

    /// <summary>Converts a trigger value 0..1 to 0..255.</summary>
    public static byte ToTrigger(double v) => (byte)Math.Round(Math.Clamp(v, 0.0, 1.0) * 255.0);
}
