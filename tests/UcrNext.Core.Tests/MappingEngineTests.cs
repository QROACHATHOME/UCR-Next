namespace UcrNext.Core.Tests;

public class MappingEngineTests
{
    private static readonly InputRef ButtonA = new("fake", InputKind.Button, 0);
    private static readonly InputRef StickX = new("fake", InputKind.Axis, 0);

    private static (MappingEngine, FakeInput, FakeKm, FakePad) Make(params Binding[] bindings)
    {
        var input = new FakeInput();
        var km = new FakeKm();
        var pad = new FakePad();
        var engine = new MappingEngine(input, km, pad);
        engine.SetProfiles(new[] { new Profile { Bindings = bindings.ToList() } });
        return (engine, input, km, pad);
    }

    [Fact]
    public void Button_to_key_presses_once_and_releases()
    {
        var (engine, input, km, _) = Make(new Binding { Input = ButtonA, Output = OutputRef.Key(0x41) });

        engine.Tick();
        input.Values[ButtonA] = 1; engine.Tick(); engine.Tick();
        input.Values[ButtonA] = 0; engine.Tick();

        Assert.Equal(new[] { "key 65 down", "key 65 up" }, km.Events);
    }

    [Fact]
    public void Stick_to_virtual_stick_applies_settings()
    {
        var (engine, input, _, pad) = Make(new Binding
        {
            Input = StickX,
            Output = OutputRef.Xbox(XboxAxis.LeftX),
            Settings = new AxisSettings { Invert = true },
        });

        input.Values[StickX] = 0.5;
        engine.Tick();

        Assert.Single(pad.Updates);
        Assert.Equal(-0.5, pad.Updates[0].Axes[(int)XboxAxis.LeftX], 6);
    }

    [Fact]
    public void Pad_is_only_updated_when_state_changes()
    {
        var (engine, input, _, pad) = Make(new Binding { Input = ButtonA, Output = OutputRef.Xbox(XboxButton.A) });

        engine.Tick(); engine.Tick();
        input.Values[ButtonA] = 1; engine.Tick(); engine.Tick();

        Assert.Equal(2, pad.Updates.Count);
        Assert.True(pad.Updates[1].Buttons[(int)XboxButton.A]);
    }

    [Fact]
    public void Disabled_profiles_and_bindings_are_ignored()
    {
        var input = new FakeInput { Values = { [ButtonA] = 1 } };
        var km = new FakeKm();
        var engine = new MappingEngine(input, km, null);
        engine.SetProfiles(new[]
        {
            new Profile { Enabled = false, Bindings = { new Binding { Input = ButtonA, Output = OutputRef.Key(1) } } },
            new Profile { Bindings = { new Binding { Enabled = false, Input = ButtonA, Output = OutputRef.Key(2) } } },
        });
        engine.Tick();
        Assert.Empty(km.Events);
    }

    [Fact]
    public void Deactivating_releases_held_keys()
    {
        var (engine, input, km, _) = Make(new Binding { Input = ButtonA, Output = OutputRef.Key(0x20) });
        input.Values[ButtonA] = 1;
        engine.Tick();
        engine.SetActive(false);
        engine.Tick();

        Assert.Equal(new[] { "key 32 down", "key 32 up" }, km.Events);
    }

    [Fact]
    public void Removing_a_binding_while_held_releases_the_key()
    {
        var (engine, input, km, _) = Make(new Binding { Input = ButtonA, Output = OutputRef.Key(0x20) });
        input.Values[ButtonA] = 1;
        engine.Tick();
        engine.SetProfiles(Array.Empty<Profile>());
        engine.Tick();

        Assert.Equal(new[] { "key 32 down", "key 32 up" }, km.Events);
    }

    [Fact]
    public void Axis_drives_button_past_threshold()
    {
        var (engine, input, _, pad) = Make(new Binding { Input = StickX, Output = OutputRef.Xbox(XboxButton.B) });
        input.Values[StickX] = 0.7;
        engine.Tick();
        Assert.True(pad.Updates.Last().Buttons[(int)XboxButton.B]);
    }

    [Fact]
    public void Edits_after_SetProfiles_do_not_leak_into_the_engine()
    {
        var binding = new Binding { Input = ButtonA, Output = OutputRef.Key(0x41) };
        var (engine, input, km, _) = Make(binding);
        binding.Output = OutputRef.Key(0x42); // not pushed yet
        input.Values[ButtonA] = 1;
        engine.Tick();
        Assert.Equal(new[] { "key 65 down" }, km.Events);
    }
}
