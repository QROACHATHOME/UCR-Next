using System.Xml.Linq;

namespace UcrNext.Core.Tests;

/// <summary>Device slots (and UCR shadow devices), "wait for devices", DualShock 4 and vJoy POV output.</summary>
public class V09EngineTests
{
    private static DeviceInfo Dev(string id, string name = "") => new(id, name.Length > 0 ? name : id, "fake", 8, 4);
    private static InputRef SlotButton(string slot, int i = 0) => new(InputRef.SlotId(slot), InputKind.Button, i);

    private static (MappingEngine, FakeDelta, FakePad) Make(IEnumerable<DeviceSlot> slots, IEnumerable<DeviceInfo> devices, params Profile[] profiles)
    {
        var input = new FakeDelta();
        var pad = new FakePad();
        var engine = new MappingEngine(input, new FakeKm(), pad);
        engine.SetConnectedDevices(devices);
        engine.SetDeviceSlots(slots);
        engine.SetProfiles(profiles);
        return (engine, input, pad);
    }

    private static Profile P(params Binding[] b) => new() { Bindings = b.ToList() };

    [Fact]
    public void Slot_reads_the_device_assigned_to_it()
    {
        var slot = new DeviceSlot { Name = "P1 stick", Device = new DeviceMatch { DeviceId = "fake2" } };
        var (engine, input, pad) = Make(new[] { slot }, new[] { Dev("fake2"), Dev("fake3") },
            P(new Binding { Input = SlotButton("P1 stick"), Output = OutputRef.Xbox(XboxButton.A) }));
        input.Values[new InputRef("fake2", InputKind.Button, 0)] = 1;
        engine.Tick();
        Assert.True(pad.Last[0].Buttons[(int)XboxButton.A]);

        // Replace the controller: one change in the slot, the binding follows.
        slot.Device.DeviceId = "fake3";
        engine.SetDeviceSlots(new[] { slot });
        engine.Tick();
        Assert.False(pad.Last[0].Buttons[(int)XboxButton.A]);
        input.Values[new InputRef("fake3", InputKind.Button, 0)] = 1;
        engine.Tick();
        Assert.True(pad.Last[0].Buttons[(int)XboxButton.A]);
    }

    [Fact]
    public void Slot_finds_a_renumbered_device_by_name()
    {
        var slot = new DeviceSlot { Name = "Stick", Device = new DeviceMatch { DeviceId = "fake9", Name = "Arcade Stick" } };
        var (engine, input, pad) = Make(new[] { slot }, new[] { Dev("fake4", "Arcade Stick") },
            P(new Binding { Input = SlotButton("Stick"), Output = OutputRef.Xbox(XboxButton.B) }));
        input.Values[new InputRef("fake4", InputKind.Button, 0)] = 1;
        engine.Tick();
        Assert.True(pad.Last[0].Buttons[(int)XboxButton.B]);
        Assert.Equal("fake4", engine.SlotAssignments["Stick"][0]);
    }

    [Fact]
    public void Shadow_device_drives_the_next_pad_only_while_connected()
    {
        var slot = new DeviceSlot
        {
            Name = "Stick", Device = new DeviceMatch { DeviceId = "fake1" },
            Shadows = { new DeviceMatch { DeviceId = "fake2" } },
        };
        var profile = P(new Binding { Input = SlotButton("Stick"), Output = OutputRef.Xbox(XboxButton.A) });
        var (engine, input, pad) = Make(new[] { slot }, new[] { Dev("fake1") }, profile);
        engine.Tick();
        Assert.DoesNotContain("update 1", pad.Log); // player 2's stick missing: no second pad plugged in

        engine.SetConnectedDevices(new[] { Dev("fake1"), Dev("fake2") });
        input.Values[new InputRef("fake2", InputKind.Button, 0)] = 1;
        engine.Tick();
        Assert.False(pad.Last[0].Buttons[(int)XboxButton.A]);
        Assert.True(pad.Last[1].Buttons[(int)XboxButton.A]);

        // Player 2's stick unplugged: pad 2 is unplugged again.
        engine.SetConnectedDevices(new[] { Dev("fake1") });
        engine.Tick();
        Assert.Contains("disconnect 1", pad.Log);
    }

    [Fact]
    public void Shadow_copies_have_their_own_states()
    {
        var slot = new DeviceSlot { Name = "S", Device = new DeviceMatch { DeviceId = "fake1" }, Shadows = { new DeviceMatch { DeviceId = "fake2" } } };
        var profile = P(
            new Binding { Mode = BindingMode.SetState, Input = SlotButton("S", 1), Settings = { StateName = "shift" } },
            new Binding { Input = SlotButton("S", 0), Output = OutputRef.Xbox(XboxButton.Y), Conditions = { new StateCondition { Name = "shift" } } });
        var (engine, input, pad) = Make(new[] { slot }, new[] { Dev("fake1"), Dev("fake2") }, profile);
        input.Values[new InputRef("fake1", InputKind.Button, 1)] = 1;  // player 1 holds shift
        input.Values[new InputRef("fake1", InputKind.Button, 0)] = 1;
        input.Values[new InputRef("fake2", InputKind.Button, 0)] = 1;  // player 2 doesn't
        engine.Tick();
        Assert.True(pad.Last[0].Buttons[(int)XboxButton.Y]);
        Assert.False(pad.Last[1].Buttons[(int)XboxButton.Y]);
    }

    [Fact]
    public void Profile_waits_for_its_devices()
    {
        var profile = P(new Binding { Input = new InputRef("fake2", InputKind.Button, 0), Output = OutputRef.Xbox(XboxButton.A) });
        profile.RequireDevices = true;
        var (engine, input, pad) = Make(Array.Empty<DeviceSlot>(), new[] { Dev("fake1") }, profile);
        engine.Tick();
        Assert.Empty(pad.Log);                  // no virtual pad for a missing controller
        Assert.Single(engine.WaitingProfileNames);

        engine.SetConnectedDevices(new[] { Dev("fake1"), Dev("fake2") });
        engine.Tick();
        Assert.Contains("update 0", pad.Log);
        Assert.Empty(engine.WaitingProfileNames);
    }

    [Fact]
    public void Keyboard_inputs_never_make_a_profile_wait()
    {
        var profile = P(new Binding { Input = new InputRef("keyboard", InputKind.Button, 0x41), Output = OutputRef.Xbox(XboxButton.A) });
        profile.RequireDevices = true;
        var (engine, _, pad) = Make(Array.Empty<DeviceSlot>(), Array.Empty<DeviceInfo>(), profile);
        engine.Tick();
        Assert.Contains("update 0", pad.Log);
    }

    [Fact]
    public void DualShock4_output_is_separate_from_xbox()
    {
        var b1 = new InputRef("fake", InputKind.Button, 0);
        var x = new InputRef("fake", InputKind.Axis, 0);
        var (engine, input, pad) = Make(Array.Empty<DeviceSlot>(), Array.Empty<DeviceInfo>(), P(
            new Binding { Input = b1, Output = OutputRef.Ds4(Ds4Button.Cross) },
            new Binding { Input = x, Output = OutputRef.Ds4(XboxAxis.LeftX, 1) }));
        input.Values[b1] = 1;
        input.Values[x] = 0.5;
        engine.Tick();
        Assert.True(pad.Ds4Last[0].Buttons[(int)Ds4Button.Cross]);
        Assert.Equal(0.5, pad.Ds4Last[1].Axes[(int)XboxAxis.LeftX], 6);
        Assert.DoesNotContain(pad.Log, l => l.StartsWith("update")); // no Xbox pad
        engine.ReleaseAll();
        Assert.Contains("ds4 disconnect 1", pad.Log);
        Assert.Contains("ds4 disconnect 0", pad.Log);
    }

    [Fact]
    public void VJoy_pov_directions_combine_into_an_angle()
    {
        var up = new InputRef("fake", InputKind.Button, 0);
        var right = new InputRef("fake", InputKind.Button, 1);
        var input = new FakeDelta();
        var vjoy = new FakeVJoy();
        var engine = new MappingEngine(input, new FakeKm(), new FakePad(), vjoy);
        engine.SetProfiles(new[] { P(
            new Binding { Input = up, Output = OutputRef.VJoyPov(1, 1, 0) },
            new Binding { Input = right, Output = OutputRef.VJoyPov(1, 1, 1) }) });
        engine.Tick();
        Assert.Equal(-1, vjoy.Updates[^1].State.PovAngle(1));
        input.Values[up] = 1; engine.Tick();
        Assert.Equal(0, vjoy.Updates[^1].State.PovAngle(1));
        input.Values[right] = 1; engine.Tick();
        Assert.Equal(45, vjoy.Updates[^1].State.PovAngle(1));
        input.Values[up] = 0; engine.Tick();
        Assert.Equal(90, vjoy.Updates[^1].State.PovAngle(1));
    }

    [Fact]
    public void Output_names()
    {
        Assert.Equal("DualShock 4 #2: Cross", OutputRef.Ds4(Ds4Button.Cross, 1).ToString());
        Assert.Equal("DualShock 4 #1: L2 (analog)", OutputRef.Ds4(XboxAxis.LeftTrigger).ToString());
        Assert.Equal("vJoy 1: POV 2 Left", OutputRef.VJoyPov(1, 2, 3).ToString());
        Assert.True(OutputRef.Ds4(XboxAxis.RightTrigger).IsTrigger);
        var all = OutputNames.All(new[] { new VJoyDeviceInfo(1, new[] { VJoyAxis.X }, 2, Povs: 1) }).ToList();
        Assert.Contains(OutputRef.VJoyPov(1, 1, 2), all);
        Assert.Contains(OutputRef.Ds4(Ds4Button.Touchpad, 3), all);
    }
}

public class V09ImportTests
{
    private const string Xml = """
        <?xml version="1.0" encoding="utf-8"?>
        <Context xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
          <Profiles>
            <Profile Title="Two players" Guid="11111111-2222-3333-4444-555555555555">
              <Mappings>
                <Mapping Title="Fire">
                  <DeviceBindings>
                    <DeviceBinding IsBound="true" DeviceConfigurationGuid="aaaaaaaa-0000-0000-0000-000000000001" KeyType="1" KeyValue="48" KeySubValue="0" />
                  </DeviceBindings>
                  <Plugins>
                    <Plugin xsi:type="ButtonToButton">
                      <Outputs>
                        <DeviceBinding IsBound="true" DeviceConfigurationGuid="bbbbbbbb-0000-0000-0000-000000000002" KeyType="1" KeyValue="0" KeySubValue="0" />
                      </Outputs>
                      <Filters />
                      <Invert>false</Invert>
                    </Plugin>
                  </Plugins>
                </Mapping>
                <Mapping Title="Hat">
                  <DeviceBindings>
                    <DeviceBinding IsBound="true" DeviceConfigurationGuid="aaaaaaaa-0000-0000-0000-000000000001" KeyType="1" KeyValue="49" KeySubValue="0" />
                  </DeviceBindings>
                  <Plugins>
                    <Plugin xsi:type="ButtonToButton">
                      <Outputs>
                        <DeviceBinding IsBound="true" DeviceConfigurationGuid="cccccccc-0000-0000-0000-000000000003" KeyType="2" KeyValue="1" KeySubValue="2" />
                      </Outputs>
                      <Filters />
                    </Plugin>
                  </Plugins>
                </Mapping>
                <Mapping Title="Pad">
                  <DeviceBindings>
                    <DeviceBinding IsBound="true" DeviceConfigurationGuid="aaaaaaaa-0000-0000-0000-000000000001" KeyType="1" KeyValue="50" KeySubValue="0" />
                  </DeviceBindings>
                  <Plugins>
                    <Plugin xsi:type="ButtonToButton">
                      <Outputs>
                        <DeviceBinding IsBound="true" DeviceConfigurationGuid="bbbbbbbb-0000-0000-0000-000000000002" KeyType="2" KeyValue="3" KeySubValue="0" />
                      </Outputs>
                      <Filters />
                    </Plugin>
                  </Plugins>
                </Mapping>
              </Mappings>
              <InputDeviceConfigurations>
                <DeviceConfiguration Guid="aaaaaaaa-0000-0000-0000-000000000001" ConfigurationName="Stick">
                  <Device Title="Arcade Stick" ProviderName="SharpDX_DirectInput" DeviceHandle="VID_044F&amp;PID_B10A" DeviceNumber="0" />
                  <ShadowDevices>
                    <Device Title="Arcade Stick" ProviderName="SharpDX_DirectInput" DeviceHandle="VID_044F&amp;PID_B10A" DeviceNumber="1" />
                  </ShadowDevices>
                </DeviceConfiguration>
              </InputDeviceConfigurations>
              <OutputDeviceConfigurations>
                <DeviceConfiguration Guid="bbbbbbbb-0000-0000-0000-000000000002" ConfigurationName="PS pad">
                  <Device Title="DS4 1" ProviderName="Core_ViGEm" DeviceHandle="ds4" DeviceNumber="0" />
                  <ShadowDevices>
                    <Device Title="DS4 2" ProviderName="Core_ViGEm" DeviceHandle="ds4" DeviceNumber="1" />
                  </ShadowDevices>
                </DeviceConfiguration>
                <DeviceConfiguration Guid="cccccccc-0000-0000-0000-000000000003" ConfigurationName="Stick out">
                  <Device Title="vJoy Stick 1" ProviderName="Core_vJoyInterfaceWrap" DeviceHandle="0" DeviceNumber="0" />
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
    public void Shadow_devices_become_a_slot()
    {
        var p = Run(out var r);
        var slot = Assert.Single(r.Slots);
        Assert.Equal("Arcade Stick", slot.Name);
        Assert.Equal("dinput:044F:B10A#1", slot.Device.DeviceId);
        Assert.Equal("dinput:044F:B10A#2", Assert.Single(slot.Shadows).DeviceId);
        var fire = p.Bindings.Single(b => b.Name == "Fire");
        Assert.Equal("Arcade Stick", fire.Input!.Slot);
        Assert.Equal(0, fire.Input.Index);
        Assert.DoesNotContain(r.Notes, n => n.Contains("hadow"));
    }

    [Fact]
    public void DualShock4_and_vjoy_pov_outputs()
    {
        var p = Run(out _);
        Assert.Equal(OutputRef.Ds4(Ds4Button.Cross), p.Bindings.Single(b => b.Name == "Fire").Output);
        Assert.Equal(OutputRef.Ds4(Ds4Button.DPadLeft), p.Bindings.Single(b => b.Name == "Pad").Output);
        Assert.Equal(OutputRef.VJoyPov(1, 2, 2), p.Bindings.Single(b => b.Name == "Hat").Output);
    }
}
