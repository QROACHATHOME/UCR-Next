namespace UcrNext.Core.Tests;

/// <summary>Devices known by a stable id (serial / USB port) and by their older "VID:PID#n" id.</summary>
public class StableIdTests
{
    private static readonly DeviceInfo Spinner2 = new("fake:1241:1111@P2", "Spinner #2", "fake", 5, 0, AltId: "fake:1241:1111#2");

    [Fact]
    public void DeviceInfo_matches_either_id()
    {
        Assert.True(Spinner2.Is("fake:1241:1111@P2"));
        Assert.True(Spinner2.Is("FAKE:1241:1111#2"));
        Assert.False(Spinner2.Is("fake:1241:1111#1"));
        Assert.True(new DeviceMatch { DeviceId = "fake:1241:1111#2" }.Matches(Spinner2));
    }

    [Fact]
    public void Slot_saved_with_the_older_id_finds_the_device()
    {
        var input = new FakeDelta();
        var pad = new FakePad();
        var engine = new MappingEngine(input, new FakeKm(), pad);
        engine.SetConnectedDevices(new[] { Spinner2 });
        engine.SetDeviceSlots(new[] { new DeviceSlot { Name = "Spinner 2", Device = new DeviceMatch { DeviceId = "fake:1241:1111#2" } } });
        engine.SetProfiles(new[] { new Profile { Bindings = { new Binding { Input = new InputRef(InputRef.SlotId("Spinner 2"), InputKind.Button, 0), Output = OutputRef.Xbox(XboxButton.A) } } } });
        Assert.Equal("fake:1241:1111@P2", engine.SlotAssignments["Spinner 2"][0]); // reads through the stable id
        input.Values[new InputRef("fake:1241:1111@P2", InputKind.Button, 0)] = 1;
        engine.Tick();
        Assert.True(pad.Last[0].Buttons[(int)XboxButton.A]);
    }

    [Fact]
    public void Older_id_counts_as_connected_for_waiting_profiles()
    {
        var pad = new FakePad();
        var engine = new MappingEngine(new FakeDelta(), new FakeKm(), pad);
        engine.SetConnectedDevices(new[] { Spinner2 });
        var p = new Profile { RequireDevices = true, Bindings = { new Binding { Input = new InputRef("fake:1241:1111#2", InputKind.Button, 0), Output = OutputRef.Xbox(XboxButton.A) } } };
        engine.SetProfiles(new[] { p });
        engine.Tick();
        Assert.Empty(engine.WaitingProfileNames);
    }
}
