using System.Xml.Linq;

namespace UcrNext.Core.Tests;

public class UcrImportTests
{
    // Same shape XmlSerializer produces for UCR's Context (profiles, device configurations,
    // mappings with input DeviceBindings, plugins written as <Plugin xsi:type="...">).
    private const string ContextXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <Context xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
          <Profiles>
            <Profile Title="Racing" Guid="11111111-1111-1111-1111-111111111111">
              <ChildProfiles>
                <Profile Title="Daytona" Guid="22222222-2222-2222-2222-222222222222">
                  <ChildProfiles />
                  <Mappings>
                    <Mapping Title="Gas">
                      <DeviceBindings>
                        <DeviceBinding IsBound="true" DeviceConfigurationGuid="aaaaaaaa-0000-0000-0000-000000000001" KeyType="0" KeyValue="8" KeySubValue="0" />
                      </DeviceBindings>
                      <Plugins>
                        <Plugin xsi:type="AxisToAxis">
                          <Outputs>
                            <DeviceBinding IsBound="true" DeviceConfigurationGuid="bbbbbbbb-0000-0000-0000-000000000002" KeyType="0" KeyValue="4" KeySubValue="0" />
                          </Outputs>
                          <Filters />
                          <Invert>true</Invert>
                          <Linear>true</Linear>
                          <DeadZone>0</DeadZone>
                          <AntiDeadZone>0</AntiDeadZone>
                          <Sensitivity>100</Sensitivity>
                        </Plugin>
                      </Plugins>
                    </Mapping>
                  </Mappings>
                  <InputDeviceConfigurations />
                  <OutputDeviceConfigurations />
                </Profile>
              </ChildProfiles>
              <Mappings>
                <Mapping Title="Steer">
                  <DeviceBindings>
                    <DeviceBinding IsBound="true" DeviceConfigurationGuid="aaaaaaaa-0000-0000-0000-000000000001" KeyType="0" KeyValue="0" KeySubValue="0" />
                  </DeviceBindings>
                  <Plugins>
                    <Plugin xsi:type="AxisToAxis">
                      <Outputs>
                        <DeviceBinding IsBound="true" DeviceConfigurationGuid="bbbbbbbb-0000-0000-0000-000000000002" KeyType="0" KeyValue="0" KeySubValue="0" />
                      </Outputs>
                      <Filters />
                      <Invert>false</Invert>
                      <Linear>true</Linear>
                      <DeadZone>10</DeadZone>
                      <AntiDeadZone>20</AntiDeadZone>
                      <Sensitivity>150</Sensitivity>
                    </Plugin>
                  </Plugins>
                </Mapping>
                <Mapping Title="Gas">
                  <DeviceBindings>
                    <DeviceBinding IsBound="true" DeviceConfigurationGuid="aaaaaaaa-0000-0000-0000-000000000001" KeyType="0" KeyValue="4" KeySubValue="0" />
                  </DeviceBindings>
                  <Plugins>
                    <Plugin xsi:type="AxisToAxis">
                      <Outputs>
                        <DeviceBinding IsBound="true" DeviceConfigurationGuid="bbbbbbbb-0000-0000-0000-000000000002" KeyType="0" KeyValue="5" KeySubValue="0" />
                      </Outputs>
                      <Filters />
                      <Invert>false</Invert>
                      <Linear>false</Linear>
                      <DeadZone>0</DeadZone>
                      <AntiDeadZone>0</AntiDeadZone>
                      <Sensitivity>80</Sensitivity>
                    </Plugin>
                  </Plugins>
                </Mapping>
                <Mapping Title="Shift up">
                  <DeviceBindings>
                    <DeviceBinding IsBound="true" DeviceConfigurationGuid="aaaaaaaa-0000-0000-0000-000000000001" KeyType="1" KeyValue="52" KeySubValue="0" />
                  </DeviceBindings>
                  <Plugins>
                    <Plugin xsi:type="ButtonToButton">
                      <Outputs>
                        <DeviceBinding IsBound="true" DeviceConfigurationGuid="bbbbbbbb-0000-0000-0000-000000000002" KeyType="1" KeyValue="5" KeySubValue="0" />
                      </Outputs>
                      <Filters />
                      <Invert>false</Invert>
                    </Plugin>
                  </Plugins>
                </Mapping>
                <Mapping Title="Hat">
                  <DeviceBindings>
                    <DeviceBinding IsBound="true" DeviceConfigurationGuid="aaaaaaaa-0000-0000-0000-000000000001" KeyType="2" KeyValue="0" KeySubValue="1" />
                  </DeviceBindings>
                  <Plugins>
                    <Plugin xsi:type="ButtonToButton">
                      <Outputs>
                        <DeviceBinding IsBound="true" DeviceConfigurationGuid="bbbbbbbb-0000-0000-0000-000000000002" KeyType="2" KeyValue="1" KeySubValue="0" />
                      </Outputs>
                      <Filters />
                      <Invert>false</Invert>
                    </Plugin>
                  </Plugins>
                </Mapping>
                <Mapping Title="Pause">
                  <DeviceBindings>
                    <DeviceBinding IsBound="true" DeviceConfigurationGuid="cccccccc-0000-0000-0000-000000000003" KeyType="1" KeyValue="8" KeySubValue="0" />
                  </DeviceBindings>
                  <Plugins>
                    <Plugin xsi:type="ButtonToButton">
                      <Outputs>
                        <DeviceBinding IsBound="true" DeviceConfigurationGuid="dddddddd-0000-0000-0000-000000000004" KeyType="1" KeyValue="0" KeySubValue="0" />
                      </Outputs>
                      <Filters />
                      <Invert>false</Invert>
                    </Plugin>
                  </Plugins>
                </Mapping>
                <Mapping Title="Look">
                  <DeviceBindings>
                    <DeviceBinding IsBound="true" DeviceConfigurationGuid="eeeeeeee-0000-0000-0000-000000000005" KeyType="1" KeyValue="0" KeySubValue="0" Block="true" />
                  </DeviceBindings>
                  <Plugins>
                    <Plugin xsi:type="ButtonToAxis">
                      <Outputs>
                        <DeviceBinding IsBound="true" DeviceConfigurationGuid="ffffffff-0000-0000-0000-000000000006" KeyType="0" KeyValue="2" KeySubValue="0" />
                      </Outputs>
                      <Filters />
                      <Range>-50</Range>
                      <Initialize>false</Initialize>
                      <RangePressed>100</RangePressed>
                    </Plugin>
                  </Plugins>
                </Mapping>
                <Mapping Title="Macro">
                  <DeviceBindings>
                    <DeviceBinding IsBound="true" DeviceConfigurationGuid="cccccccc-0000-0000-0000-000000000003" KeyType="1" KeyValue="0" KeySubValue="0" />
                  </DeviceBindings>
                  <Plugins>
                    <Plugin xsi:type="MysteryPlugin"><Outputs /><Filters /></Plugin>
                  </Plugins>
                </Mapping>
                <Mapping Title="Brake">
                  <DeviceBindings>
                    <DeviceBinding IsBound="true" DeviceConfigurationGuid="aaaaaaaa-0000-0000-0000-000000000001" KeyType="0" KeyValue="4" KeySubValue="0" />
                  </DeviceBindings>
                  <Plugins>
                    <Plugin xsi:type="AxisToButton">
                      <Outputs>
                        <DeviceBinding IsBound="true" DeviceConfigurationGuid="dddddddd-0000-0000-0000-000000000004" KeyType="1" KeyValue="16" KeySubValue="0" />
                        <DeviceBinding IsBound="false" DeviceConfigurationGuid="00000000-0000-0000-0000-000000000000" KeyType="0" KeyValue="0" KeySubValue="0" />
                      </Outputs>
                      <Filters />
                      <Invert>false</Invert>
                      <DeadZone>40</DeadZone>
                    </Plugin>
                  </Plugins>
                </Mapping>
              </Mappings>
              <InputDeviceConfigurations>
                <DeviceConfiguration Guid="aaaaaaaa-0000-0000-0000-000000000001" ConfigurationName="Wheel">
                  <Device Title="G29 Driving Force" ProviderName="SharpDX_DirectInput" DeviceHandle="VID_046D&amp;PID_C24F" DeviceNumber="0" />
                  <ShadowDevices />
                </DeviceConfiguration>
                <DeviceConfiguration Guid="cccccccc-0000-0000-0000-000000000003" ConfigurationName="Pad">
                  <Device Title="Xbox Controller 1" ProviderName="SharpDX_XInput" DeviceHandle="xb360" DeviceNumber="0" />
                  <ShadowDevices />
                </DeviceConfiguration>
                <DeviceConfiguration Guid="eeeeeeee-0000-0000-0000-000000000005" ConfigurationName="Kbd">
                  <Device Title="K: Logitech Keyboard" ProviderName="Core_Interception" DeviceHandle="Keyboard\HID\VID_046D&amp;PID_C31C&amp;REV_6400&amp;MI_00" DeviceNumber="0" />
                  <ShadowDevices />
                </DeviceConfiguration>
              </InputDeviceConfigurations>
              <OutputDeviceConfigurations>
                <DeviceConfiguration Guid="bbbbbbbb-0000-0000-0000-000000000002" ConfigurationName="Virtual pad">
                  <Device Title="Xbox 360 1" ProviderName="Core_ViGEm" DeviceHandle="xb360" DeviceNumber="0" />
                  <ShadowDevices />
                </DeviceConfiguration>
                <DeviceConfiguration Guid="dddddddd-0000-0000-0000-000000000004" ConfigurationName="Keys">
                  <Device Title="K: Keyboard" ProviderName="Core_Interception" DeviceHandle="Keyboard\HID\VID_046D&amp;PID_C31C" DeviceNumber="0" />
                  <ShadowDevices />
                </DeviceConfiguration>
                <DeviceConfiguration Guid="ffffffff-0000-0000-0000-000000000006" ConfigurationName="Stick">
                  <Device Title="vJoy Stick 1" ProviderName="Core_vJoyInterfaceWrap" DeviceHandle="0" DeviceNumber="0" />
                  <ShadowDevices />
                </DeviceConfiguration>
              </OutputDeviceConfigurations>
            </Profile>
          </Profiles>
        </Context>
        """;

    // Fake scan code -> VK: 0x01 Esc, 0x10 Q, 0x11 W.
    private static int ScanToVk(int sc) => sc switch { 0x01 => 0x1B, 0x10 => 0x51, 0x11 => 0x57, _ => 0 };

    private static UcrImportResult Run(bool perDevice = false) =>
        new UcrImporter(ScanToVk, perDevice).Import(XDocument.Parse(ContextXml));

    [Fact]
    public void Imports_parent_and_flattened_child_disabled()
    {
        var r = Run();
        Assert.Equal(new[] { "Racing", "Racing › Daytona" }, r.Profiles.Select(p => p.Name));
        Assert.All(r.Profiles, p => Assert.False(p.Enabled));
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), r.Profiles[0].Id);
    }

    [Fact]
    public void DirectInput_axis_to_vigem_with_settings()
    {
        var steer = Run().Profiles[0].Bindings.Single(b => b.Name == "Steer");
        Assert.Equal(new InputRef("dinput:046D:C24F#1", InputKind.Axis, 0), steer.Input);
        Assert.Equal(OutputRef.Xbox(XboxAxis.LeftX), steer.Output);
        Assert.Equal(0.10, steer.Settings.Deadzone, 6);
        Assert.Equal(0.20, steer.Settings.AntiDeadzone, 6);
        Assert.Equal(1.5, steer.Settings.Sensitivity, 6);
        Assert.False(steer.Settings.Invert);
    }

    [Fact]
    public void Pedal_to_trigger_uses_full_range()
    {
        var gas = Run().Profiles[0].Bindings.Single(b => b.Name == "Gas");
        Assert.Equal(new InputRef("dinput:046D:C24F#1", InputKind.Axis, 1), gas.Input); // offset 4 = Y
        Assert.Equal(OutputRef.Xbox(XboxAxis.RightTrigger), gas.Output);
        Assert.True(gas.Settings.TriggerFullRange);
        Assert.True(gas.Settings.Invert); // DirectInput Y is flipped in UCR-Next, so the import compensates
    }

    [Fact]
    public void Child_overrides_parent_mapping_with_same_title()
    {
        var child = Run().Profiles[1];
        var gas = child.Bindings.Single(b => b.Name == "Gas");
        Assert.Equal(2, gas.Input!.Index);                         // offset 8 = Z, from the child
        Assert.Equal(OutputRef.Xbox(XboxAxis.LeftTrigger), gas.Output);
        Assert.Contains(child.Bindings, b => b.Name == "Steer");   // inherited
    }

    [Fact]
    public void Buttons_povs_and_keyboard_outputs()
    {
        var p = Run().Profiles[0];
        var shift = p.Bindings.Single(b => b.Name == "Shift up");
        Assert.Equal(new InputRef("dinput:046D:C24F#1", InputKind.Button, 4), shift.Input); // 52 - 48
        Assert.Equal(OutputRef.Xbox(XboxButton.RightShoulder), shift.Output);

        var hat = p.Bindings.Single(b => b.Name == "Hat");
        Assert.Equal(128 + 1, hat.Input!.Index);                         // POV 0, Right
        Assert.Equal(OutputRef.Xbox(XboxButton.DPadRight), hat.Output);

        var pause = p.Bindings.Single(b => b.Name == "Pause");
        Assert.Equal(new InputRef("xinput:0", InputKind.Button, 5), pause.Input); // UCR Back (8) -> our Back (5)
        Assert.Equal(OutputRef.Key(0x1B), pause.Output);                          // scan code 1 = Esc
    }

    [Fact]
    public void Button_to_axis_values_block_and_vjoy()
    {
        var look = Run().Profiles[0].Bindings.Single(b => b.Name == "Look");
        Assert.Equal(new InputRef("keyboard", InputKind.Button, 0x1B), look.Input); // Interception kbd -> any keyboard
        Assert.True(look.Block);
        Assert.Equal(OutputRef.VJoy(1, VJoyAxis.Z), look.Output);
        Assert.Equal(-0.5, look.Settings.ButtonAxisReleased, 6);
        Assert.Equal(1.0, look.Settings.ButtonAxisPressed, 6);
    }

    [Fact]
    public void Per_device_keyboard_option()
    {
        var look = Run(perDevice: true).Profiles[0].Bindings.Single(b => b.Name == "Look");
        Assert.Equal(@"ikbd:HID\VID_046D&PID_C31C&REV_6400&MI_00#1", look.Input!.DeviceId);
        Assert.Equal(0x01, look.Input.Index);
    }

    [Fact]
    public void Per_device_keyboard_through_raw_input()
    {
        var r = new UcrImporter(ScanToVk, UcrImporter.PerDevice.RawInput).Import(XDocument.Parse(ContextXml));
        var look = r.Profiles[0].Bindings.Single(b => b.Name == "Look");
        Assert.Equal(new InputRef("rawkbd:046D:C31C#1", InputKind.Button, 0x01), look.Input);
        Assert.False(look.Block);                                   // Raw Input can't block…
        Assert.Contains(r.Notes, n => n.Contains("\"Look\"") && n.Contains("Interception"));   // …and the report says so
    }

    [Fact]
    public void Axis_to_button_high_only_and_unsupported_plugins_are_reported()
    {
        var r = Run();
        var brake = r.Profiles[0].Bindings.Where(b => b.Name.StartsWith("Brake")).ToList();
        var high = Assert.Single(brake);                     // low output unbound
        Assert.Equal(OutputRef.Key(0x57), high.Output);      // scan 0x11 = W
        Assert.Equal(0.40, high.Settings.ButtonThreshold, 6);
        Assert.Contains(r.Notes, n => n.Contains("MysteryPlugin") && n.Contains("not supported"));
        var gas = r.Profiles[0].Bindings.Single(b => b.Name == "Gas");
        Assert.True(gas.Settings.NonLinearSensitivity);    // UCR's curve is now reproduced exactly
        Assert.Equal(0.8, gas.Settings.Sensitivity, 6);
    }

    [Fact]
    public void Rejects_non_ucr_xml() =>
        Assert.Throws<FormatException>(() => new UcrImporter(ScanToVk, false).Import(XDocument.Parse("<Foo/>")));

    [Fact]
    public void Reimport_keeps_user_additions()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ucrnext-imp-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ProfileStore(dir);
            Run().SaveTo(store);
            var p = store.LoadAll().Single(x => x.Name == "Racing");
            p.Games.Add("Daytona USA"); p.Enabled = true; store.Save(p);

            Run().SaveTo(store);
            var again = store.LoadAll().Single(x => x.Name == "Racing");
            Assert.Equal(new[] { "Daytona USA" }, again.Games);
            Assert.True(again.Enabled);
            Assert.Equal(2, store.LoadAll().Count);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}

public class NewEngineBehaviourTests
{
    private static readonly InputRef Btn = new("fake", InputKind.Button, 0);
    private static readonly InputRef Pedal = new("fake", InputKind.Axis, 1);

    [Fact]
    public void Profile_is_skipped_while_listed_device_is_connected()
    {
        var input = new FakeInput { Values = { [Btn] = 1 } };
        var km = new FakeKm();
        var pad = new FakePad();
        var engine = new MappingEngine(input, km, pad);
        engine.SetProfiles(new[] { new Profile
        {
            Name = "Pad as wheel",
            SkipIfDevicesPresent = { new DeviceMatch { DeviceId = "dinput:046D:C24F#1", Name = "G29" } },
            Bindings = { new Binding { Input = Btn, Output = OutputRef.Xbox(XboxButton.A) } },
        } });

        engine.SetConnectedDevices(new[] { new DeviceInfo("dinput:046D:C24F#2", "G29", "DirectInput", 20, 3) }); // matched by name
        engine.Tick();
        Assert.Empty(pad.Updates);   // no virtual pad created
        Assert.Equal(new[] { "Pad as wheel" }, engine.SkippedProfileNames);

        engine.SetConnectedDevices(Array.Empty<DeviceInfo>()); // wheel unplugged
        engine.Tick();
        Assert.True(pad.Updates.Last().Buttons[(int)XboxButton.A]);
        Assert.Empty(engine.SkippedProfileNames);
    }

    [Fact]
    public void Inverted_button_is_active_while_released()
    {
        var input = new FakeInput();
        var km = new FakeKm();
        var engine = new MappingEngine(input, km, null);
        engine.SetProfiles(new[] { new Profile { Bindings = { new Binding { Input = Btn, Output = OutputRef.Key(0x41), Settings = new AxisSettings { Invert = true } } } } });
        engine.Tick();
        input.Values[Btn] = 1; engine.Tick();
        Assert.Equal(new[] { "key 65 down", "key 65 up" }, km.Events);
    }

    [Fact]
    public void Button_to_axis_uses_pressed_and_released_values()
    {
        var input = new FakeInput();
        var pad = new FakePad();
        var engine = new MappingEngine(input, new FakeKm(), pad);
        engine.SetProfiles(new[] { new Profile { Bindings = { new Binding
        {
            Input = Btn, Output = OutputRef.Xbox(XboxAxis.RightX),
            Settings = new AxisSettings { ButtonAxisReleased = -0.5, ButtonAxisPressed = 1 },
        } } } });
        engine.Tick();
        Assert.Equal(-0.5, pad.Updates.Last().Axes[(int)XboxAxis.RightX], 6);
        input.Values[Btn] = 1; engine.Tick();
        Assert.Equal(1.0, pad.Updates.Last().Axes[(int)XboxAxis.RightX], 6);
    }

    [Theory]
    [InlineData(-1.0, 0.0)]
    [InlineData(0.0, 0.5)]
    [InlineData(1.0, 1.0)]
    public void Full_range_trigger_maps_whole_axis(double pedal, double trigger)
    {
        var input = new FakeInput { Values = { [Pedal] = pedal } };
        var pad = new FakePad();
        var engine = new MappingEngine(input, new FakeKm(), pad);
        engine.SetProfiles(new[] { new Profile { Bindings = { new Binding
        {
            Input = Pedal, Output = OutputRef.Xbox(XboxAxis.RightTrigger), Settings = new AxisSettings { TriggerFullRange = true },
        } } } });
        engine.Tick();
        Assert.Equal(trigger, pad.Updates.Last().Axes[(int)XboxAxis.RightTrigger], 6);
    }

    [Fact]
    public void Anti_deadzone_lifts_small_values()
    {
        var s = new AxisSettings { AntiDeadzone = 0.2 };
        Assert.Equal(0.6, AxisProcessor.Shape(0.5, s), 6);
        Assert.Equal(0.0, AxisProcessor.Shape(0.0, s), 6);
        Assert.Equal(-1.0, AxisProcessor.Shape(-1.0, s), 6);
    }
}
