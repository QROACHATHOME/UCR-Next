using System.Xml.Linq;

namespace UcrNext.Core.Tests;

/// <summary>Engine behaviour of the multi-input / timed binding modes and multiple virtual pads.</summary>
public class ModeTests
{
    private static readonly InputRef X = new("fake", InputKind.Axis, 0);
    private static readonly InputRef Y = new("fake", InputKind.Axis, 1);
    private static readonly InputRef B1 = new("fake", InputKind.Button, 0);
    private static readonly InputRef B2 = new("fake", InputKind.Button, 1);
    private static readonly InputRef B3 = new("fake", InputKind.Button, 2);
    private static readonly InputRef Mouse = new("fake", InputKind.Delta, 0);

    private long _now = 1000;

    private (MappingEngine, FakeDelta, FakePad, FakeKm) Make(params Binding[] bindings)
    {
        var input = new FakeDelta();
        var pad = new FakePad();
        var km = new FakeKm();
        var engine = new MappingEngine(input, km, pad) { Clock = () => _now };
        engine.SetProfiles(new[] { new Profile { Bindings = bindings.ToList() } });
        return (engine, input, pad, km);
    }

    private static double Axis(FakePad pad, XboxAxis a, int p = 0) => pad.Last[p].Axes[(int)a];
    private static bool Btn(FakePad pad, XboxButton b, int p = 0) => pad.Last[p].Buttons[(int)b];

    [Fact]
    public void Second_pad_plugs_in_first_pad_too_in_order()
    {
        var (engine, input, pad, _) = Make(new Binding { Input = B1, Output = OutputRef.Xbox(XboxButton.A, pad: 1) });
        input.Values[B1] = 1;
        engine.Tick();
        Assert.Equal(new[] { "update 0", "update 1" }, pad.Log);   // P1 then P2
        Assert.True(Btn(pad, XboxButton.A, 1));
        Assert.False(Btn(pad, XboxButton.A, 0));
        engine.SetActive(false);
        Assert.Equal(new[] { "disconnect 1", "disconnect 0" }, pad.Log.Where(l => l.StartsWith("disconnect")));
    }

    [Theory]
    [InlineData(1, 0, 1.0)]
    [InlineData(0, 1, -1.0)]
    [InlineData(1, 1, 0.0)]
    [InlineData(0, 0, 0.0)]
    public void Buttons_to_axis(int a, int b, double expected)
    {
        var (engine, input, pad, _) = Make(new Binding
        {
            Mode = BindingMode.ButtonsToAxis, Input = B1, ExtraInputs = { B2 }, Output = OutputRef.Xbox(XboxAxis.LeftY),
        });
        input.Values[B1] = a; input.Values[B2] = b;
        engine.Tick();
        Assert.Equal(expected, Axis(pad, XboxAxis.LeftY), 6);
    }

    [Fact]
    public void Stick_pair_rotates_and_uses_circular_deadzone()
    {
        AxisSettings S(PairComponent c) => new() { Component = c, RotationDegrees = 90, Deadzone = 0.2, CircularDeadzone = true };
        var (engine, input, pad, _) = Make(
            new Binding { Mode = BindingMode.StickPair, Input = X, ExtraInputs = { Y }, Output = OutputRef.Xbox(XboxAxis.LeftX), Settings = S(PairComponent.X) },
            new Binding { Mode = BindingMode.StickPair, Input = X, ExtraInputs = { Y }, Output = OutputRef.Xbox(XboxAxis.LeftY), Settings = S(PairComponent.Y) });

        input.Values[X] = 1; input.Values[Y] = 0;
        engine.Tick();
        Assert.Equal(0.0, Axis(pad, XboxAxis.LeftX), 6);   // (1,0) rotated 90° = (0,1)
        Assert.Equal(1.0, Axis(pad, XboxAxis.LeftY), 6);

        input.Values[X] = 0.12; input.Values[Y] = 0.12;    // length 0.17 < 0.2: inside the circle
        engine.Tick();
        Assert.Equal(0.0, Axis(pad, XboxAxis.LeftX), 6);
        Assert.Equal(0.0, Axis(pad, XboxAxis.LeftY), 6);
    }

    [Fact]
    public void Stick_trim_adds_position_and_resets()
    {
        var (engine, input, pad, _) = Make(new Binding
        {
            Mode = BindingMode.StickTrim, Input = X, ExtraInputs = { Y, B1, B2 }, Output = OutputRef.Xbox(XboxAxis.LeftX),
        });
        input.Values[X] = 0.3; input.Values[B1] = 1; engine.Tick();   // take trim
        input.Values[B1] = 0; input.Values[X] = 0; engine.Tick();
        Assert.Equal(0.3, Axis(pad, XboxAxis.LeftX), 6);               // centred stick now outputs the trim
        input.Values[B2] = 1; engine.Tick();                            // reset (output holds this tick)
        input.Values[B2] = 0; engine.Tick();
        Assert.Equal(0.0, Axis(pad, XboxAxis.LeftX), 6);
    }

    [Fact]
    public void Axes_range_presses_inside_the_box()
    {
        var (engine, input, pad, _) = Make(new Binding
        {
            Mode = BindingMode.AxesRange, Input = X, ExtraInputs = { Y }, Output = OutputRef.Xbox(XboxButton.B),
            Settings = new AxisSettings { XStart = 0.5, XEnd = 1, YStart = -1, YEnd = 1 },
        });
        input.Values[X] = 0.7; engine.Tick();
        Assert.True(Btn(pad, XboxButton.B));
        input.Values[X] = 0.2; engine.Tick();
        Assert.False(Btn(pad, XboxButton.B));
    }

    [Fact]
    public void Delta_to_axis_absolute_returns_to_centre_after_timeout()
    {
        var (engine, input, pad, _) = Make(new Binding
        {
            Mode = BindingMode.DeltaToAxis, Input = Mouse, Output = OutputRef.Xbox(XboxAxis.LeftX),
            Settings = new AxisSettings { DeltaMode = DeltaMode.Absolute, DeltaSensitivity = 50, TimeoutMs = 100 },
        });
        input.Deltas[Mouse] = (10, 2);                // average 5 counts per report
        engine.Tick();
        Assert.Equal(5 * 100 * 50 / 32768.0, Axis(pad, XboxAxis.LeftX), 6);
        input.Deltas[Mouse] = (0, 0);
        _now += 50; engine.Tick();
        Assert.NotEqual(0.0, Axis(pad, XboxAxis.LeftX));
        _now += 60; engine.Tick();
        Assert.Equal(0.0, Axis(pad, XboxAxis.LeftX));
    }

    [Fact]
    public void Delta_to_axis_relative_accumulates_and_resets()
    {
        var (engine, input, pad, _) = Make(new Binding
        {
            Mode = BindingMode.DeltaToAxis, Input = Mouse, ExtraInputs = { B1 }, Output = OutputRef.Xbox(XboxAxis.RightX),
            Settings = new AxisSettings { DeltaMode = DeltaMode.Relative, DeltaSensitivity = 100 },
        });
        input.Deltas[Mouse] = (100, 3); engine.Tick();
        input.Deltas[Mouse] = (64, 1); engine.Tick();
        Assert.Equal(16400 / 32768.0, Axis(pad, XboxAxis.RightX), 6);
        input.Deltas[Mouse] = (0, 0); input.Values[B1] = 1; engine.Tick();
        Assert.Equal(0.0, Axis(pad, XboxAxis.RightX));
    }

    [Fact]
    public void Delta_to_buttons_debounces_and_releases()
    {
        AxisSettings S(int dir) => new() { Direction = dir, TimeoutMs = 100, DebounceMs = 30 };
        var (engine, input, pad, _) = Make(
            new Binding { Mode = BindingMode.DeltaToButtons, Input = Mouse, Output = OutputRef.Xbox(XboxButton.DPadLeft), Settings = S(-1) },
            new Binding { Mode = BindingMode.DeltaToButtons, Input = Mouse, Output = OutputRef.Xbox(XboxButton.DPadRight), Settings = S(1) });

        input.Deltas[Mouse] = (8, 1); engine.Tick();
        Assert.False(Btn(pad, XboxButton.DPadRight));               // debounce pending
        input.Deltas[Mouse] = (0, 0);
        _now += 40; engine.Tick();
        Assert.True(Btn(pad, XboxButton.DPadRight));
        Assert.False(Btn(pad, XboxButton.DPadLeft));
        _now += 100; engine.Tick();                                   // centre timeout -> target 0
        _now += 40; engine.Tick();                                    // after debounce
        Assert.False(Btn(pad, XboxButton.DPadRight));
    }

    [Fact]
    public void Long_press_fires_short_or_long()
    {
        AxisSettings S(PairComponent c) => new() { Component = c, LongPressMs = 200, ShortPressMs = 50 };
        var (engine, input, pad, _) = Make(
            new Binding { Mode = BindingMode.LongPress, Input = B1, Output = OutputRef.Xbox(XboxButton.A), Settings = S(PairComponent.X) },
            new Binding { Mode = BindingMode.LongPress, Input = B1, Output = OutputRef.Xbox(XboxButton.B), Settings = S(PairComponent.Y) });

        input.Values[B1] = 1; engine.Tick();
        _now += 100; input.Values[B1] = 0; engine.Tick();             // short tap
        Assert.True(Btn(pad, XboxButton.A));
        _now += 60; engine.Tick();
        Assert.False(Btn(pad, XboxButton.A));

        input.Values[B1] = 1; engine.Tick();
        _now += 250; engine.Tick();                                   // held past 200 ms
        Assert.True(Btn(pad, XboxButton.B));
        Assert.False(Btn(pad, XboxButton.A));
        input.Values[B1] = 0; engine.Tick();
        Assert.False(Btn(pad, XboxButton.B));
        Assert.False(Btn(pad, XboxButton.A));                         // a long press gives no short press
    }

    [Fact]
    public void Increment_moves_gradually()
    {
        var (engine, input, pad, _) = Make(new Binding
        {
            Mode = BindingMode.Increment, Input = X, Output = OutputRef.Xbox(XboxAxis.RightY),
            Settings = new AxisSettings { IncrementRate = 2 },
        });
        input.Values[X] = 1;
        engine.Tick();
        for (int i = 0; i < 10; i++) { _now += 10; engine.Tick(); }   // 100 ms at full: +0.02 per 10 ms
        Assert.Equal(0.2, Axis(pad, XboxAxis.RightY), 6);
        input.Values[X] = 0;
        _now += 10; engine.Tick();
        Assert.Equal(0.2, Axis(pad, XboxAxis.RightY), 6);             // holds when centred
    }

    [Theory]
    [InlineData(AxisHalf.High, 1.0, 1.0)]
    [InlineData(AxisHalf.High, 0.0, -1.0)]
    [InlineData(AxisHalf.High, 0.5, 0.0)]
    [InlineData(AxisHalf.Low, -1.0, 1.0)]
    [InlineData(AxisHalf.Low, 0.5, -1.0)]
    public void Axis_splitter_halves(AxisHalf half, double input, double expected) =>
        Assert.Equal(expected, AxisProcessor.Split(input, half), 6);

    [Fact]
    public void Non_linear_sensitivity_matches_ucr_curve()
    {
        var s = new AxisSettings { Sensitivity = 0.8, NonLinearSensitivity = true };
        double k = 0.008;
        Assert.Equal(k * 0.5 + (1 - k) * 0.125, AxisProcessor.Shape(0.5, s), 9);
        Assert.Equal(1.0, AxisProcessor.Shape(1.0, s), 9);
    }
}

public class ImportPluginTests
{
    private static int ScanToVk(int sc) => sc switch { 0x1E => 0x41, _ => 0 }; // only 'A' known: forces fallbacks

    private static string Ctx(string mappings, string extraInputs = "", string extraOutputs = "") => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <Context xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
          <Profiles>
            <Profile Title="P" Guid="44444444-4444-4444-4444-444444444444">
              <Mappings>{mappings}</Mappings>
              <InputDeviceConfigurations>
                <DeviceConfiguration Guid="aaaaaaaa-0000-0000-0000-000000000001"><Device Title="Pad" ProviderName="SharpDX_XInput" DeviceHandle="xb360" DeviceNumber="0" /></DeviceConfiguration>
                <DeviceConfiguration Guid="aaaaaaaa-0000-0000-0000-000000000002"><Device Title="M: Trackball" ProviderName="Core_Interception" DeviceHandle="Mouse\HID\VID_1241&amp;PID_1111&amp;MI_00" DeviceNumber="0" /></DeviceConfiguration>
                {extraInputs}
              </InputDeviceConfigurations>
              <OutputDeviceConfigurations>
                <DeviceConfiguration Guid="bbbbbbbb-0000-0000-0000-000000000001"><Device Title="Xbox 360 1" ProviderName="Core_ViGEm" DeviceHandle="xb360" DeviceNumber="0" /></DeviceConfiguration>
                <DeviceConfiguration Guid="bbbbbbbb-0000-0000-0000-000000000002"><Device Title="Xbox 360 2" ProviderName="Core_ViGEm" DeviceHandle="xb360" DeviceNumber="1" /></DeviceConfiguration>
                <DeviceConfiguration Guid="bbbbbbbb-0000-0000-0000-000000000003"><Device Title="K" ProviderName="Core_Interception" DeviceHandle="Keyboard\HID\VID_046D&amp;PID_C31C" DeviceNumber="0" /></DeviceConfiguration>
                {extraOutputs}
              </OutputDeviceConfigurations>
            </Profile>
          </Profiles>
        </Context>
        """;

    private static string In(string cfg, int type, int value, int sub = 0) =>
        $"""<DeviceBinding IsBound="true" DeviceConfigurationGuid="aaaaaaaa-0000-0000-0000-00000000000{cfg}" KeyType="{type}" KeyValue="{value}" KeySubValue="{sub}" />""";
    private static string Out(string cfg, int type, int value) =>
        $"""<DeviceBinding IsBound="true" DeviceConfigurationGuid="bbbbbbbb-0000-0000-0000-00000000000{cfg}" KeyType="{type}" KeyValue="{value}" KeySubValue="0" />""";
    private static string Map(string title, string inputs, string plugin, string outputs, string props) =>
        $"""<Mapping Title="{title}"><DeviceBindings>{inputs}</DeviceBindings><Plugins><Plugin xsi:type="{plugin}"><Outputs>{outputs}</Outputs><Filters />{props}</Plugin></Plugins></Mapping>""";

    private static (Profile P, List<string> Notes) Import(string mappings, bool perDevice = false)
    {
        var r = new UcrImporter(ScanToVk, perDevice).Import(XDocument.Parse(Ctx(mappings)));
        return (r.Profiles.Single(), r.Notes);
    }

    [Fact]
    public void Second_vigem_pad_keeps_its_own_pad()
    {
        var (p, notes) = Import(Map("P2 Coin", In("1", 1, 8), "ButtonToButton", Out("2", 1, 8), "<Invert>false</Invert>"));
        Assert.Equal(OutputRef.Xbox(XboxButton.Back, pad: 1), p.Bindings.Single().Output);
        Assert.DoesNotContain(notes, n => n.Contains("merged"));
    }

    [Fact]
    public void Delta_to_axis_from_interception_mouse()
    {
        var (p, _) = Import(Map("Steer 25%", In("2", 0, 0), "DeltaToAxis", Out("1", 0, 0),
            "<Deadzone>2</Deadzone><Mode>Absolute</Mode><Sensitivity>25</Sensitivity><AbsoluteTimeout>80</AbsoluteTimeout>"));
        var b = p.Bindings.Single();
        Assert.Equal(BindingMode.DeltaToAxis, b.Mode);
        Assert.Equal(new InputRef("mouse", InputKind.Delta, 0), b.Input);
        Assert.Equal(25, b.Settings.DeltaSensitivity);
        Assert.Equal(2, b.Settings.DeltaDeadzone);
        Assert.Equal(80, b.Settings.TimeoutMs);

        var (p2, _) = Import(Map("Steer", In("2", 0, 1), "DeltaToAxis", Out("1", 0, 0), "<Mode>Relative</Mode>"), perDevice: true);
        Assert.Equal(new InputRef("rawmouse:1241:1111#1", InputKind.Delta, 1), p2.Bindings.Single().Input);
        Assert.Equal(DeltaMode.Relative, p2.Bindings.Single().Settings.DeltaMode);
    }

    [Fact]
    public void Buttons_to_axis_rotation_splitter_and_range()
    {
        var (p, notes) = Import(
            Map("B2A", In("1", 1, 0) + In("1", 1, 1), "ButtonsToAxis", Out("1", 0, 1), "<Invert>true</Invert>") +
            Map("Rot", In("1", 0, 0) + In("1", 0, 1), "AxesToAxesRotation", Out("1", 0, 0) + Out("1", 0, 1),
                "<InvertX>false</InvertX><InvertY>true</InvertY><Sensitivity>100</Sensitivity><Linear>false</Linear><DeadZone>10</DeadZone><CircularDz>true</CircularDz><Rotation>45</Rotation>") +
            Map("Split", In("1", 0, 2), "AxisSplitter", Out("1", 0, 4) + Out("1", 0, 5), "<InvertHigh>false</InvertHigh><InvertLow>false</InvertLow><DeadZone>0</DeadZone>") +
            Map("Range", In("1", 0, 0) + In("1", 0, 1), "AxesRangeToButton", Out("1", 1, 0),
                "<InvertX>false</InvertX><InvertY>false</InvertY><XStart>-10</XStart><XEnd>10</XEnd><YStart>50</YStart><YEnd>100</YEnd>"));
        Assert.DoesNotContain(notes, n => n.Contains("not supported"));

        var b2a = p.Bindings.Single(b => b.Name == "B2A");
        Assert.Equal(BindingMode.ButtonsToAxis, b2a.Mode);
        Assert.True(b2a.Settings.Invert);
        Assert.Equal(10, b2a.Input!.Index);                       // UCR XInput button 0 = A = UCR-Next index 10
        Assert.Single(b2a.ExtraInputs);

        var rotY = p.Bindings.Single(b => b.Name == "Rot (Y)");
        Assert.Equal(BindingMode.StickPair, rotY.Mode);
        Assert.Equal(PairComponent.Y, rotY.Settings.Component);
        Assert.Equal(45, rotY.Settings.RotationDegrees);
        Assert.True(rotY.Settings.CircularDeadzone);
        Assert.True(rotY.Settings.InvertY);

        var split = p.Bindings.Where(b => b.Name.StartsWith("Split")).ToList();
        Assert.Equal(new[] { AxisHalf.High, AxisHalf.Low }, split.Select(b => b.Settings.Half));
        Assert.Equal(OutputRef.Xbox(XboxAxis.RightTrigger), split[1].Output);

        var range = p.Bindings.Single(b => b.Name == "Range");
        Assert.Equal(BindingMode.AxesRange, range.Mode);
        Assert.Equal(0.5, range.Settings.YStart, 6);
    }

    [Fact]
    public void Delta_to_buttons_long_press_increment_and_trim()
    {
        var (p, notes) = Import(
            Map("Spin", In("2", 0, 0), "DeltaToButtons", Out("1", 2, 3) + Out("1", 2, 1), "<Min>3</Min><CenterTimeout>120</CenterTimeout><DeBounceTimeout>40</DeBounceTimeout>") +
            Map("Start", In("1", 1, 9), "ButtonToButtonsLongPress", Out("1", 1, 9) + Out("1", 1, 8), "<Timeout>500</Timeout><ShortPressDuration>80</ShortPressDuration>") +
            Map("Pedal", In("1", 0, 5), "AxisToAxisIncrement", Out("1", 0, 3), "<Invert>false</Invert><Linear>true</Linear><DeadZone>5</DeadZone><Sensitivity>100</Sensitivity><RelativeContinue>true</RelativeContinue><RelativeSensitivity>3</RelativeSensitivity><CounterEffect>2</CounterEffect>") +
            Map("Pedals", In("1", 0, 0) + In("1", 0, 1) + In("1", 1, 0) + In("1", 1, 1), "AxesToAxesTrim", Out("1", 0, 0) + Out("1", 0, 1), "<Sensitivity>100</Sensitivity><DeadZone>0</DeadZone>"));
        Assert.DoesNotContain(notes, n => n.Contains("not supported"));

        var spin = p.Bindings.Where(b => b.Name.StartsWith("Spin")).ToList();
        Assert.Equal(new[] { -1, 1 }, spin.Select(b => b.Settings.Direction));
        Assert.Equal(OutputRef.Xbox(XboxButton.DPadLeft), spin[0].Output);   // UCR "Button Low" = 1st output
        Assert.Equal(40, spin[0].Settings.DebounceMs);

        var lp = p.Bindings.Where(b => b.Name.StartsWith("Start")).ToList();
        Assert.Equal(new[] { PairComponent.X, PairComponent.Y }, lp.Select(b => b.Settings.Component));
        Assert.Equal(500, lp[0].Settings.LongPressMs);

        var inc = p.Bindings.Single(b => b.Name == "Pedal");
        Assert.Equal(BindingMode.Increment, inc.Mode);
        Assert.Equal(InputKind.Trigger, inc.Input!.Kind);
        Assert.Equal(3, inc.Settings.IncrementRate);

        var trim = p.Bindings.Where(b => b.Name.StartsWith("Pedals")).ToList();
        Assert.All(trim, b => Assert.Equal(3, b.ExtraInputs.Count));
        Assert.All(trim, b => Assert.Equal(BindingMode.StickTrim, b.Mode));
    }

    [Fact]
    public void Numpad_scan_codes_fall_back_and_mouse_wheel_imports()
    {
        var (p, notes) = Import(
            Map("Plus", In("1", 1, 0), "ButtonToButton", Out("3", 1, 0x4D), "<Invert>false</Invert>") +       // scan 0x4E = Numpad +
            Map("Wheel", In("2", 1, 5), "ButtonToButton", Out("1", 1, 0), "<Invert>false</Invert>"));
        Assert.Equal(OutputRef.Key(0x6B), p.Bindings.Single(b => b.Name == "Plus").Output);
        Assert.Equal(new InputRef("mouse", InputKind.Button, 5), p.Bindings.Single(b => b.Name == "Wheel").Input);
        Assert.DoesNotContain(notes, n => n.Contains("skipped"));
    }
}
