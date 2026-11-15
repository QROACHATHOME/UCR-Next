using static UcrNext.Core.ProfileTemplates;

namespace UcrNext.Core.Tests;

/// <summary>Profile wizard templates, game types, "disable-all", active profile ids.</summary>
public class V014Tests
{
    private static InputRef Btn(int i) => new("fake0", InputKind.Button, i, $"Button {i}");
    private static InputRef Axis(int i) => new("fake0", InputKind.Axis, i, $"Axis {i}");

    private static (MappingEngine Engine, FakeInput Input, FakePad Pad, FakeKm Km) Run(params Binding[] bindings)
    {
        var input = new FakeInput();
        var pad = new FakePad();
        var km = new FakeKm();
        var engine = new MappingEngine(input, km, pad);
        engine.SetProfiles(new[] { new Profile { Bindings = bindings.ToList() } });
        return (engine, input, pad, km);
    }

    private static Step StepFor(Target t, string label, int player = 1) => Steps(t, player).Single(s => s.Label == label);

    [Fact]
    public void Arcade_stick_buttons_become_one_stick_axis()
    {
        var step = StepFor(Target.XboxPad, "Left stick left / right");
        var b = Build(step, new Pressed(Btn(3), 1), new Pressed(Btn(2), 1)); // asked for RIGHT first, then LEFT
        Assert.Equal(BindingMode.ButtonsToAxis, b.Mode);
        var (engine, input, pad, _) = Run(b);

        input.Values[Btn(3)] = 1;
        engine.Tick();
        Assert.Equal(1, pad.Last[0].Axes[(int)XboxAxis.LeftX], 3);
        input.Values[Btn(3)] = 0; input.Values[Btn(2)] = 1;
        engine.Tick();
        Assert.Equal(-1, pad.Last[0].Axes[(int)XboxAxis.LeftX], 3);
    }

    [Fact]
    public void Analog_stick_pushed_up_reads_up_even_when_its_axis_goes_negative()
    {
        // DirectInput Y axes usually go negative when pushed up; the Xbox pad's Y is positive up.
        var b = Build(StepFor(Target.XboxPad, "Left stick up / down"), new Pressed(Axis(1), -1));
        Assert.True(b.Settings.Invert);
        var (engine, input, pad, _) = Run(b);
        input.Values[Axis(1)] = -1;
        engine.Tick();
        Assert.Equal(1, pad.Last[0].Axes[(int)XboxAxis.LeftY], 3);
    }

    [Fact]
    public void Stick_direction_can_press_a_key()
    {
        var left = StepFor(Target.Keyboard, "Left");
        Assert.Equal(OutputRef.Key(0x25), left.Output);           // MAME P1: left arrow
        var b = Build(left, new Pressed(Axis(0), -1));
        var (engine, input, _, km) = Run(b);
        input.Values[Axis(0)] = -1;
        engine.Tick();
        Assert.Contains("key 37 down", km.Events);
        input.Values[Axis(0)] = 1;                                 // the other way: not "left"
        engine.Tick();
        Assert.Contains("key 37 up", km.Events);
    }

    [Fact]
    public void Templates_cover_pad_and_mame_keys_and_a_changed_key_is_used()
    {
        Assert.Equal(20, Steps(Target.XboxPad, 2).Count);
        Assert.All(Steps(Target.XboxPad, 2), s => Assert.Equal(1, s.Output.Device)); // player 2 → pad #2
        var p2 = Steps(Target.Keyboard, 2);
        Assert.Equal(12, p2.Count);
        Assert.Equal(OutputRef.Key('R'), p2[0].Output);            // MAME P2 up
        Assert.Empty(Steps(Target.Empty, 1));

        var b = Build(p2[0], new Pressed(Btn(0), 1), output: OutputRef.Key('U'));
        Assert.Equal(OutputRef.Key('U'), b.Output);
        Assert.Equal("Up", b.Name);
    }

    [Fact]
    public void Game_types_normalize_and_group()
    {
        Assert.Equal("Light gun", GameTypes.Normalize("  light GUN "));
        Assert.Equal("Rhythm", GameTypes.Normalize("Rhythm"));     // own types are kept
        Assert.Equal(GameTypes.None, GameTypes.GroupOf(new Profile()));
        Assert.Equal("Racing", GameTypes.GroupOf(new Profile { GameType = "racing" }));
        var sorted = new[] { GameTypes.None, "Racing", "Fighting" }.OrderBy(x => x, Comparer<string>.Create(GameTypes.Compare)).ToList();
        Assert.Equal(new[] { "Fighting", "Racing", GameTypes.None }, sorted);
    }

    [Fact]
    public void Engine_reports_active_profile_ids()
    {
        var a = new Profile { Name = "A" };
        var b = new Profile { Name = "B", Applications = { "game.exe" } };
        var engine = new MappingEngine(new FakeInput(), new FakeKm(), new FakePad());
        engine.SetProfiles(new[] { a, b });
        Assert.Equal(new[] { a.Id }, engine.ActiveProfileIds);
        engine.ForegroundApp = "game.exe";
        engine.Tick();
        Assert.Equal(new[] { a.Id, b.Id }, engine.ActiveProfileIds);
    }

    [Fact]
    public void Disable_all_switches_every_profile_off()
    {
        var list = new List<Profile> { new() { Name = "One" }, new() { Name = "Two", Enabled = false }, new() { Name = "Three" } };
        var c = new CommandProcessor(new Host(list));
        Assert.Equal("Disabled 2 profile(s): One, Three", c.Execute("disable-all"));
        Assert.All(list, p => Assert.False(p.Enabled));
        Assert.Equal("No profile was enabled", c.Execute("none"));
        list[0].Enabled = true;
        Assert.StartsWith("Disabled 1", c.Execute("disable --all"));
        list[1].GameType = "racing";
        Assert.Contains("type: Racing", c.Execute("profiles"));
    }

    private sealed class Host(List<Profile> list) : IControlHost
    {
        public bool IsMapping { get; set; }
        public IReadOnlyList<Profile> Profiles => list;
        public void SetProfileEnabled(Profile profile, bool enabled) => profile.Enabled = enabled;
        public void ReloadProfiles() { }
        public bool HasUnsavedChanges => false;
        public void SaveProfiles() { }
        public void ShowWindow() { }
        public void HideWindow() { }
        public void Quit() { }
        public IEnumerable<string> StatusLines() => Array.Empty<string>();
        public IReadOnlyList<string> ActiveProfileNames => list.Where(p => p.Enabled).Select(p => p.Name).ToList();
        public string ImportUcr(string path, bool perDevice) => "";
    }
}
