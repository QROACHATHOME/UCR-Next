namespace UcrNext.Core;

/// <summary>What the profile wizard asks for, and how a pressed control becomes a binding.</summary>
public static class ProfileTemplates
{
    public enum Target { XboxPad, Keyboard, Empty }

    public enum StepKind
    {
        /// <summary>Output is a button or key. A stick direction works too (that half of the axis).</summary>
        Button,
        /// <summary>Output is a stick axis. Ask for the positive direction; two buttons (e.g. an arcade stick) make one axis.</summary>
        StickAxis,
        /// <summary>Output is an analog trigger; a button pulls it fully.</summary>
        Trigger,
    }

    /// <param name="Label">"A button", "Left stick left / right"</param>
    /// <param name="Prompt">What to press first ("Push the left stick RIGHT, or press the button for right").</param>
    /// <param name="SecondPrompt">Stick axes only: the opposite direction, asked when the first was a button.</param>
    public sealed record Step(string Label, string Prompt, StepKind Kind, OutputRef Output, string? SecondPrompt = null);

    /// <summary>A captured control and the direction it moved (+1 / -1; buttons are +1).</summary>
    public sealed record Pressed(InputRef Input, int Direction);

    public static IReadOnlyList<Step> Steps(Target target, int player) => target switch
    {
        Target.XboxPad => XboxSteps(Math.Clamp(player, 1, OutputRef.MaxXboxPads) - 1),
        Target.Keyboard => KeySteps(player),
        _ => Array.Empty<Step>(),
    };

    private static IReadOnlyList<Step> XboxSteps(int pad)
    {
        Step Btn(string label, XboxButton b) => new(label, $"Press the control you want as {label}", StepKind.Button, OutputRef.Xbox(b, pad));
        Step Stick(string label, XboxAxis a, string pos, string neg) => new(label,
            $"{label}: push the stick {pos} — or press the button for {pos}", StepKind.StickAxis, OutputRef.Xbox(a, pad),
            $"{label}: now press the button for {neg}");
        Step Trig(string label, XboxAxis a) => new(label, $"Press or pull the control you want as {label}", StepKind.Trigger, OutputRef.Xbox(a, pad));
        return new[]
        {
            Stick("Left stick left / right", XboxAxis.LeftX, "RIGHT", "LEFT"),
            Stick("Left stick up / down", XboxAxis.LeftY, "UP", "DOWN"),
            Btn("D-pad up", XboxButton.DPadUp), Btn("D-pad down", XboxButton.DPadDown),
            Btn("D-pad left", XboxButton.DPadLeft), Btn("D-pad right", XboxButton.DPadRight),
            Btn("A button", XboxButton.A), Btn("B button", XboxButton.B), Btn("X button", XboxButton.X), Btn("Y button", XboxButton.Y),
            Btn("Left bumper (LB)", XboxButton.LeftShoulder), Btn("Right bumper (RB)", XboxButton.RightShoulder),
            Trig("Left trigger (LT)", XboxAxis.LeftTrigger), Trig("Right trigger (RT)", XboxAxis.RightTrigger),
            Btn("Back", XboxButton.Back), Btn("Start", XboxButton.Start),
            Stick("Right stick left / right", XboxAxis.RightX, "RIGHT", "LEFT"),
            Stick("Right stick up / down", XboxAxis.RightY, "UP", "DOWN"),
            Btn("Left stick click (L3)", XboxButton.LeftThumb), Btn("Right stick click (R3)", XboxButton.RightThumb),
        };
    }

    /// <summary>MAME's default keys for player 1 or 2 (each can be changed in the wizard).</summary>
    private static IReadOnlyList<Step> KeySteps(int player)
    {
        bool p2 = player == 2;
        var keys = new (string Label, int Vk)[]
        {
            ("Up", p2 ? 'R' : 0x26), ("Down", p2 ? 'F' : 0x28), ("Left", p2 ? 'D' : 0x25), ("Right", p2 ? 'G' : 0x27),
            ("Button 1", p2 ? 'A' : 0xA2), ("Button 2", p2 ? 'S' : 0xA4), ("Button 3", p2 ? 'Q' : 0x20),
            ("Button 4", p2 ? 'W' : 0xA0), ("Button 5", p2 ? 'I' : 'Z'), ("Button 6", p2 ? 'K' : 'X'),
            ("Start", p2 ? '2' : '1'), ("Coin", p2 ? '6' : '5'),
        };
        return keys.Select(k => new Step(k.Label, $"Press the control for {k.Label}", StepKind.Button, OutputRef.Key(k.Vk))).ToList();
    }

    /// <summary>
    /// The binding for one wizard step. <paramref name="second"/> is the opposite direction of a stick
    /// axis set up with two buttons (null otherwise). <paramref name="output"/> overrides the step's output
    /// (a key changed in the wizard).
    /// </summary>
    public static Binding Build(Step step, Pressed first, Pressed? second = null, OutputRef? output = null)
    {
        var b = new Binding { Name = step.Label, Input = first.Input, Output = output ?? step.Output };
        bool analog = first.Input.Kind is InputKind.Axis or InputKind.Trigger;
        switch (step.Kind)
        {
            case StepKind.StickAxis when !analog && second is { Input.Kind: InputKind.Button }:
                b.Mode = BindingMode.ButtonsToAxis;
                b.ExtraInputs.Add(second.Input);
                break;
            case StepKind.StickAxis when analog:
                b.Settings.Invert = first.Direction < 0; // asked for the positive direction (right / up)
                break;
            case StepKind.StickAxis:
                b.Settings.ButtonAxisPressed = 1; // one button: only the positive direction
                break;
            default:
                // A stick direction as a button (or trigger): that half of the axis.
                if (first.Input.Kind == InputKind.Axis) b.Settings.Invert = first.Direction < 0;
                break;
        }
        return b;
    }

    /// <summary>Short description of a captured control for the wizard's list.</summary>
    public static string Describe(Pressed p, Func<string, string> deviceName)
    {
        string dir = p.Input.Kind == InputKind.Axis ? (p.Direction < 0 ? " (−)" : " (+)") : "";
        string label = p.Input.Label.Length > 0 ? p.Input.Label : $"{p.Input.Kind} {p.Input.Index}";
        return $"{label}{dir} — {deviceName(p.Input.DeviceId)}";
    }
}
