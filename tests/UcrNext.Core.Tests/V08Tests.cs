using System.Xml.Linq;

namespace UcrNext.Core.Tests;

/// <summary>Toggle / pulse buttons, mouse wheel output, the UCR plugin modes added in 0.8 and named states.</summary>
public class V08EngineTests
{
    private static readonly InputRef X = new("fake", InputKind.Axis, 0);
    private static readonly InputRef Y = new("fake", InputKind.Axis, 1);
    private static readonly InputRef B1 = new("fake", InputKind.Button, 0);
    private static readonly InputRef B2 = new("fake", InputKind.Button, 1);

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

    private static double Axis(FakePad pad, XboxAxis a) => pad.Last[0].Axes[(int)a];
    private static bool Btn(FakePad pad, XboxButton b) => pad.Last[0].Buttons[(int)b];

    private void Step(MappingEngine e, int ms = 4) { _now += ms; e.Tick(); }

    [Fact]
    public void Toggle_flips_on_each_press()
    {
        var (engine, input, pad, _) = Make(new Binding { Input = B1, Output = OutputRef.Xbox(XboxButton.A), Settings = { Toggle = true } });
        Step(engine); Assert.False(Btn(pad, XboxButton.A));
        input.Values[B1] = 1; Step(engine); Assert.True(Btn(pad, XboxButton.A));
        input.Values[B1] = 0; Step(engine); Assert.True(Btn(pad, XboxButton.A));    // stays on after release
        input.Values[B1] = 1; Step(engine); Assert.False(Btn(pad, XboxButton.A));   // second press: off
        input.Values[B1] = 0; Step(engine); Assert.False(Btn(pad, XboxButton.A));
    }

    [Fact]
    public void Pulse_gives_fixed_length_press_on_press_or_release()
    {
        var (engine, input, pad, _) = Make(
            new Binding { Input = B1, Output = OutputRef.Xbox(XboxButton.A), Settings = { PulseMs = 50 } },
            new Binding { Input = B1, Output = OutputRef.Xbox(XboxButton.B), Settings = { PulseMs = 50, PulseOnRelease = true } });
        input.Values[B1] = 1; Step(engine);
        Assert.True(Btn(pad, XboxButton.A)); Assert.False(Btn(pad, XboxButton.B));
        Step(engine, 60);                                   // still held, pulse over
        Assert.False(Btn(pad, XboxButton.A));
        input.Values[B1] = 0; Step(engine);
        Assert.True(Btn(pad, XboxButton.B));
        Step(engine, 60);
        Assert.False(Btn(pad, XboxButton.B));
    }

    [Fact]
    public void Wheel_button_scrolls_one_notch_per_press()
    {
        var (engine, input, _, km) = Make(
            new Binding { Input = B1, Output = OutputRef.Mouse(MouseButton.WheelDown) },
            new Binding { Input = B2, Output = OutputRef.Mouse(MouseButton.WheelRight) });
        input.Values[B1] = 1;
        Step(engine); Step(engine); Step(engine);           // held: still one notch
        input.Values[B1] = 0; Step(engine);
        input.Values[B1] = 1; Step(engine);
        input.Values[B2] = 1; Step(engine);
        Assert.Equal(new[] { "wheel -120", "wheel -120", "wheel 120 h" }, km.Events);
    }

    [Fact]
    public void Wheel_axis_scrolls_continuously()
    {
        var (engine, input, _, km) = Make(new Binding { Input = Y, Output = OutputRef.Mouse(MouseAxis.Wheel) });
        input.Values[Y] = 1;
        for (int i = 0; i < 50; i++) Step(engine);          // 50 ticks × 0.08 = 4 notches
        Assert.Equal(4, km.Events.Count(e => e == "wheel 120"));
        input.Values[Y] = -1;
        for (int i = 0; i < 25; i++) Step(engine);
        Assert.Equal(2, km.Events.Count(e => e == "wheel -120"));
    }

    [Theory]
    [InlineData(MergeMode.Average, 0.6, -0.2, 0.2)]
    [InlineData(MergeMode.Greatest, 0.6, -0.8, -0.8)]
    [InlineData(MergeMode.Sum, 0.6, 0.8, 1.0)]
    public void Axis_merger(MergeMode mode, double hi, double lo, double expected)
    {
        var (engine, input, pad, _) = Make(new Binding
        {
            Mode = BindingMode.AxisMerger, Input = X, ExtraInputs = { Y }, Output = OutputRef.Xbox(XboxAxis.LeftX),
            Settings = { MergeMode = mode },
        });
        input.Values[X] = hi; input.Values[Y] = lo;
        Step(engine);
        Assert.Equal(expected, Axis(pad, XboxAxis.LeftX), 6);
    }

    [Fact]
    public void Axis_merger_two_pedals_to_one_axis()
    {
        // Typical use: gas on the high input, brake inverted on the low input, summed.
        var (engine, input, pad, _) = Make(new Binding
        {
            Mode = BindingMode.AxisMerger, Input = X, ExtraInputs = { Y }, Output = OutputRef.Xbox(XboxAxis.LeftY),
            Settings = { MergeMode = MergeMode.Sum, InvertY = true },
        });
        input.Values[X] = 0.5; input.Values[Y] = 0.75;
        Step(engine);
        Assert.Equal(-0.25, Axis(pad, XboxAxis.LeftY), 6);
    }

    [Fact]
    public void Axis_to_delta_moves_min_to_max_pixels_per_10ms()
    {
        var (engine, input, _, km) = Make(new Binding
        {
            Mode = BindingMode.AxisToDelta, Input = X, Output = OutputRef.Mouse(MouseAxis.X),
            Settings = { MinDelta = 2, MaxDelta = 10 },
        });
        input.Values[X] = 1;
        Step(engine);                                       // first tick has no elapsed time
        Step(engine, 10);
        Assert.Equal("move 10 0", km.Events.Last());        // full deflection = Max per 10 ms
        input.Values[X] = -0.5;
        Step(engine, 10);
        Assert.Equal("move -6 0", km.Events.Last());        // 2 + 0.5 × 8
        input.Values[X] = 0;
        int n = km.Events.Count;
        Step(engine, 10);
        Assert.Equal(n, km.Events.Count);                   // centred: no movement
    }

    [Fact]
    public void Default_value_holds_until_the_axis_is_moved()
    {
        var (engine, input, pad, _) = Make(
            new Binding { Mode = BindingMode.DefaultValue, Output = OutputRef.Xbox(XboxAxis.RightY), Settings = { DefaultValue = -1 } },
            new Binding { Input = Y, Output = OutputRef.Xbox(XboxAxis.RightY) });
        Step(engine);
        Assert.Equal(-1, Axis(pad, XboxAxis.RightY), 6);   // no input bound to the default binding
        input.Values[Y] = 0.01; Step(engine);
        Assert.Equal(-1, Axis(pad, XboxAxis.RightY), 6);   // tiny jitter doesn't count
        input.Values[Y] = 0.5; Step(engine);
        Assert.Equal(0.5, Axis(pad, XboxAxis.RightY), 6);
        input.Values[Y] = 0; Step(engine);
        Assert.Equal(0, Axis(pad, XboxAxis.RightY), 6);    // follows the real axis from now on
    }

    [Theory]
    [InlineData(0, 1.0, 0.0)]
    [InlineData(90, 0.0, -1.0)]
    [InlineData(45, 0.5, -0.5)]
    public void Fpv_angle_mix(double angle, double roll, double yaw)
    {
        Binding B(PairComponent c, XboxAxis a) => new()
        {
            Mode = BindingMode.FpvMix, Input = X, ExtraInputs = { Y }, Output = OutputRef.Xbox(a),
            Settings = { CameraAngle = angle, Component = c },
        };
        var (engine, input, pad, _) = Make(B(PairComponent.X, XboxAxis.LeftX), B(PairComponent.Y, XboxAxis.RightX));
        input.Values[X] = 1;                                // full roll, no yaw
        Step(engine);
        Assert.Equal(roll, Axis(pad, XboxAxis.LeftX), 6);
        Assert.Equal(yaw, Axis(pad, XboxAxis.RightX), 6);
    }

    [Fact]
    public void Reversible_throttle_flips_with_the_button()
    {
        var (engine, input, pad, _) = Make(new Binding
        {
            Mode = BindingMode.ReversibleThrottle, Input = X, ExtraInputs = { B1 }, Output = OutputRef.Xbox(XboxAxis.LeftY),
        });
        input.Values[X] = 1; Step(engine);
        Assert.Equal(1, Axis(pad, XboxAxis.LeftY), 6);
        input.Values[X] = -1; Step(engine);
        Assert.Equal(0, Axis(pad, XboxAxis.LeftY), 6);      // throttle closed = centre
        input.Values[B1] = 1; input.Values[X] = 0; Step(engine);
        Assert.Equal(-0.5, Axis(pad, XboxAxis.LeftY), 6);   // reversed: half throttle = half back
        input.Values[B1] = 0; Step(engine);
        input.Values[B1] = 1; Step(engine);
        Assert.Equal(0.5, Axis(pad, XboxAxis.LeftY), 6);    // pressed again: forward
    }

    [Fact]
    public void Shift_state_switches_a_button_to_another_output()
    {
        var (engine, input, pad, _) = Make(
            new Binding { Mode = BindingMode.SetState, Input = B2, Settings = { StateName = "Shift" } },
            new Binding { Input = B1, Output = OutputRef.Xbox(XboxButton.A), Conditions = StateCondition.Parse("!shift") },
            new Binding { Input = B1, Output = OutputRef.Xbox(XboxButton.B), Conditions = StateCondition.Parse("shift") });
        input.Values[B1] = 1; Step(engine);
        Assert.True(Btn(pad, XboxButton.A)); Assert.False(Btn(pad, XboxButton.B));
        input.Values[B2] = 1; Step(engine);                 // hold shift
        Assert.False(Btn(pad, XboxButton.A)); Assert.True(Btn(pad, XboxButton.B));
        Assert.Contains(engine.ActiveStates, s => s.EndsWith(": Shift"));
        input.Values[B2] = 0; Step(engine);
        Assert.True(Btn(pad, XboxButton.A)); Assert.False(Btn(pad, XboxButton.B));
    }

    [Fact]
    public void State_toggle_and_axis_range()
    {
        var (engine, input, pad, _) = Make(
            new Binding { Mode = BindingMode.SetState, Input = B2, Settings = { StateName = "menu", StateOnPress = StateAction.Toggle, StateOnRelease = StateAction.Unchanged } },
            new Binding { Mode = BindingMode.SetState, Input = X, Settings = { StateName = "fast", XStart = 0.8, XEnd = 1, StateOnPress = StateAction.On, StateOnRelease = StateAction.Off } },
            new Binding { Input = B1, Output = OutputRef.Xbox(XboxButton.X), Conditions = StateCondition.Parse("menu") },
            new Binding { Input = B1, Output = OutputRef.Xbox(XboxButton.Y), Conditions = StateCondition.Parse("fast, !menu") });
        input.Values[B1] = 1;
        input.Values[B2] = 1; Step(engine); input.Values[B2] = 0; Step(engine);
        Assert.True(Btn(pad, XboxButton.X));                // menu toggled on
        input.Values[B2] = 1; Step(engine); input.Values[B2] = 0; Step(engine);
        Assert.False(Btn(pad, XboxButton.X));               // and off again
        Assert.False(Btn(pad, XboxButton.Y));
        input.Values[X] = 0.9; Step(engine);
        Assert.True(Btn(pad, XboxButton.Y));                // axis inside the range
        input.Values[X] = 0.2; Step(engine);
        Assert.False(Btn(pad, XboxButton.Y));
    }

    [Fact]
    public void Conditions_parse_and_format()
    {
        var c = StateCondition.Parse(" shift , !menu;; ");
        Assert.Equal("shift, !menu", StateCondition.Format(c));
        Assert.True(c[1].Not);
    }

    [Fact]
    public void Incomplete_bindings_per_mode()
    {
        Assert.True(new Binding { Mode = BindingMode.DefaultValue, Output = OutputRef.Xbox(XboxAxis.LeftX) }.IsComplete);
        Assert.True(new Binding { Mode = BindingMode.SetState, Input = B1 }.IsComplete);
        Assert.False(new Binding { Mode = BindingMode.Direct, Input = B1 }.IsComplete);
    }
}

public class V08ImportTests
{
    private const string Xml = """
        <?xml version="1.0" encoding="utf-8"?>
        <Context xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
          <Profiles>
            <Profile Title="Flight" Guid="33333333-3333-3333-3333-333333333333">
              <ChildProfiles />
              <Mappings>
                <Mapping Title="Pedals">
                  <DeviceBindings>
                    <DeviceBinding IsBound="true" DeviceConfigurationGuid="aaaaaaaa-0000-0000-0000-000000000001" KeyType="0" KeyValue="0" KeySubValue="0" />
                    <DeviceBinding IsBound="true" DeviceConfigurationGuid="aaaaaaaa-0000-0000-0000-000000000001" KeyType="0" KeyValue="8" KeySubValue="0" />
                  </DeviceBindings>
                  <Plugins>
                    <Plugin xsi:type="AxisMerger">
                      <Outputs><DeviceBinding IsBound="true" DeviceConfigurationGuid="bbbbbbbb-0000-0000-0000-000000000002" KeyType="0" KeyValue="2" KeySubValue="0" /></Outputs>
                      <Filters><Filter><Name>shift</Name><Negative>true</Negative></Filter></Filters>
                      <DeadZone>5</DeadZone><Mode>Sum</Mode><InvertHigh>false</InvertHigh><InvertLow>true</InvertLow>
                      <Linear>false</Linear><Sensitivity>100</Sensitivity>
                    </Plugin>
                  </Plugins>
                </Mapping>
                <Mapping Title="Shift">
                  <DeviceBindings>
                    <DeviceBinding IsBound="true" DeviceConfigurationGuid="aaaaaaaa-0000-0000-0000-000000000001" KeyType="1" KeyValue="48" KeySubValue="0" />
                  </DeviceBindings>
                  <Plugins>
                    <Plugin xsi:type="ButtonToFilter">
                      <Outputs />
                      <Filters />
                      <FilterName>Shift</FilterName><FilterStateDown>Toggle</FilterStateDown><FilterStateUp>Unchanged</FilterStateUp>
                    </Plugin>
                  </Plugins>
                </Mapping>
                <Mapping Title="Scroll">
                  <DeviceBindings>
                    <DeviceBinding IsBound="true" DeviceConfigurationGuid="aaaaaaaa-0000-0000-0000-000000000001" KeyType="1" KeyValue="49" KeySubValue="0" />
                  </DeviceBindings>
                  <Plugins>
                    <Plugin xsi:type="ButtonToEvent">
                      <Outputs><DeviceBinding IsBound="true" DeviceConfigurationGuid="cccccccc-0000-0000-0000-000000000003" KeyType="1" KeyValue="6" KeySubValue="0" /></Outputs>
                      <Filters />
                      <Mode>Release</Mode>
                    </Plugin>
                  </Plugins>
                </Mapping>
                <Mapping Title="Look">
                  <DeviceBindings>
                    <DeviceBinding IsBound="true" DeviceConfigurationGuid="aaaaaaaa-0000-0000-0000-000000000001" KeyType="0" KeyValue="4" KeySubValue="0" />
                  </DeviceBindings>
                  <Plugins>
                    <Plugin xsi:type="AxisToDelta">
                      <Outputs><DeviceBinding IsBound="true" DeviceConfigurationGuid="cccccccc-0000-0000-0000-000000000003" KeyType="0" KeyValue="1" KeySubValue="0" /></Outputs>
                      <Filters />
                      <Invert>false</Invert><DeadZone>10</DeadZone><Sensitivity>100</Sensitivity><Min>2</Min><Max>30</Max>
                    </Plugin>
                  </Plugins>
                </Mapping>
                <Mapping Title="Throttle">
                  <DeviceBindings>
                    <DeviceBinding IsBound="true" DeviceConfigurationGuid="aaaaaaaa-0000-0000-0000-000000000001" KeyType="0" KeyValue="20" KeySubValue="0" />
                    <DeviceBinding IsBound="true" DeviceConfigurationGuid="aaaaaaaa-0000-0000-0000-000000000001" KeyType="1" KeyValue="50" KeySubValue="0" />
                  </DeviceBindings>
                  <Plugins>
                    <Plugin xsi:type="ReversibleThrottle">
                      <Outputs><DeviceBinding IsBound="true" DeviceConfigurationGuid="bbbbbbbb-0000-0000-0000-000000000002" KeyType="0" KeyValue="5" KeySubValue="0" /></Outputs>
                      <Filters />
                      <InvertInput>false</InvertInput><InvertOutput>true</InvertOutput>
                    </Plugin>
                    <Plugin xsi:type="AxisInitializer">
                      <Outputs><DeviceBinding IsBound="true" DeviceConfigurationGuid="bbbbbbbb-0000-0000-0000-000000000002" KeyType="0" KeyValue="3" KeySubValue="0" /></Outputs>
                      <Filters />
                      <Percentage>-100</Percentage>
                    </Plugin>
                  </Plugins>
                </Mapping>
                <Mapping Title="Camera">
                  <DeviceBindings>
                    <DeviceBinding IsBound="true" DeviceConfigurationGuid="aaaaaaaa-0000-0000-0000-000000000001" KeyType="0" KeyValue="0" KeySubValue="0" />
                    <DeviceBinding IsBound="true" DeviceConfigurationGuid="aaaaaaaa-0000-0000-0000-000000000001" KeyType="0" KeyValue="12" KeySubValue="0" />
                  </DeviceBindings>
                  <Plugins>
                    <Plugin xsi:type="FpvAngleMix">
                      <Outputs>
                        <DeviceBinding IsBound="true" DeviceConfigurationGuid="bbbbbbbb-0000-0000-0000-000000000002" KeyType="0" KeyValue="0" KeySubValue="0" />
                        <DeviceBinding IsBound="true" DeviceConfigurationGuid="bbbbbbbb-0000-0000-0000-000000000002" KeyType="0" KeyValue="2" KeySubValue="0" />
                      </Outputs>
                      <Filters />
                      <CameraAngle>30</CameraAngle><InvertRoll>true</InvertRoll><InvertYaw>false</InvertYaw>
                    </Plugin>
                  </Plugins>
                </Mapping>
              </Mappings>
              <InputDeviceConfigurations>
                <DeviceConfiguration Guid="aaaaaaaa-0000-0000-0000-000000000001" ConfigurationName="Stick">
                  <Device Title="Stick" ProviderName="SharpDX_DirectInput" DeviceHandle="VID_044F&amp;PID_B10A" DeviceNumber="0" />
                  <ShadowDevices />
                </DeviceConfiguration>
              </InputDeviceConfigurations>
              <OutputDeviceConfigurations>
                <DeviceConfiguration Guid="bbbbbbbb-0000-0000-0000-000000000002" ConfigurationName="Virtual pad">
                  <Device Title="Xbox 360 1" ProviderName="Core_ViGEm" DeviceHandle="xb360" DeviceNumber="0" />
                  <ShadowDevices />
                </DeviceConfiguration>
                <DeviceConfiguration Guid="cccccccc-0000-0000-0000-000000000003" ConfigurationName="Mouse">
                  <Device Title="M: Mouse" ProviderName="Core_Interception" DeviceHandle="Mouse\HID\VID_046D&amp;PID_C077" DeviceNumber="0" />
                  <ShadowDevices />
                </DeviceConfiguration>
              </OutputDeviceConfigurations>
            </Profile>
          </Profiles>
        </Context>
        """;

    private static Profile Run(out UcrImportResult r)
    {
        r = new UcrImporter(_ => 0, false).Import(XDocument.Parse(Xml));
        return r.Profiles.Single();
    }

    [Fact]
    public void Axis_merger_with_filter_condition()
    {
        var b = Run(out _).Bindings.Single(x => x.Name == "Pedals");
        Assert.Equal(BindingMode.AxisMerger, b.Mode);
        Assert.Equal(MergeMode.Sum, b.Settings.MergeMode);
        Assert.Equal(0.05, b.Settings.Deadzone, 6);
        Assert.True(b.Settings.InvertY);
        Assert.Equal(OutputRef.Xbox(XboxAxis.RightX), b.Output);
        var c = Assert.Single(b.Conditions);
        Assert.Equal("shift", c.Name); Assert.True(c.Not);
    }

    [Fact]
    public void Button_to_filter_becomes_set_state()
    {
        var b = Run(out _).Bindings.Single(x => x.Name.StartsWith("Shift"));
        Assert.Equal(BindingMode.SetState, b.Mode);
        Assert.Null(b.Output);
        Assert.Equal("Shift", b.Settings.StateName);
        Assert.Equal(StateAction.Toggle, b.Settings.StateOnPress);
        Assert.Equal(StateAction.Unchanged, b.Settings.StateOnRelease);
    }

    [Fact]
    public void Button_to_event_scrolls_the_wheel()
    {
        var b = Run(out _).Bindings.Single(x => x.Name == "Scroll");
        Assert.Equal(OutputRef.Mouse(MouseButton.WheelDown), b.Output);
        Assert.True(b.Settings.PulseOnRelease);
        Assert.True(b.Settings.PulseMs > 0);
    }

    [Fact]
    public void Axis_to_delta_reversible_throttle_initializer_and_fpv()
    {
        var p = Run(out var r);
        var look = p.Bindings.Single(x => x.Name == "Look");
        Assert.Equal(BindingMode.AxisToDelta, look.Mode);
        Assert.Equal(OutputRef.Mouse(MouseAxis.Y), look.Output);
        Assert.Equal((2, 30), (look.Settings.MinDelta, look.Settings.MaxDelta));
        Assert.Equal(0.10, look.Settings.Deadzone, 6);

        var thr = p.Bindings.Single(x => x.Name == "Throttle" && x.Mode == BindingMode.ReversibleThrottle);
        Assert.True(thr.Settings.InvertOutput);
        Assert.Equal(2, thr.Extra(0)!.Index);               // button 50 - 48

        var init = p.Bindings.Single(x => x.Mode == BindingMode.DefaultValue);
        Assert.Null(init.Input);
        Assert.Equal(OutputRef.Xbox(XboxAxis.RightY), init.Output);
        Assert.Equal(-1, init.Settings.DefaultValue, 6);

        var fpv = p.Bindings.Where(x => x.Mode == BindingMode.FpvMix).ToList();
        Assert.Equal(2, fpv.Count);
        Assert.Equal(30, fpv[0].Settings.CameraAngle, 6);
        Assert.True(fpv[0].Settings.Invert);

        Assert.DoesNotContain(r.Notes, n => n.Contains("not supported"));
    }

    [Fact]
    public void Imported_profile_round_trips_through_json()
    {
        var p = Run(out _);
        var copy = ProfileCopy.Deserialize(ProfileCopy.Serialize(p));
        Assert.Equal(ProfileCopy.EditKey(p), ProfileCopy.EditKey(copy));
        Assert.Equal("!shift", StateCondition.Format(copy.Bindings.Single(x => x.Name == "Pedals").Conditions));
    }
}
