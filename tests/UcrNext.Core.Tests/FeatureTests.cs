namespace UcrNext.Core.Tests;

internal sealed class FakeVJoy : IVJoyOutput
{
    public List<(int Device, VJoyState State)> Updates { get; } = new();
    public List<int> Releases { get; } = new();
    public bool IsAvailable => true;
    public string Status => "fake";
    public IReadOnlyList<VJoyDeviceInfo> Devices { get; } = new[] { new VJoyDeviceInfo(1, Enum.GetValues<VJoyAxis>(), 32) };
    public void Update(int device, VJoyState state) => Updates.Add((device, state.Clone()));
    public void Release(int device) => Releases.Add(device);
}

public class PerApplicationTests
{
    private static readonly InputRef Btn = new("fake", InputKind.Button, 0);

    [Theory]
    [InlineData("game", "game.exe", true)]
    [InlineData("C:\\Games\\Game.EXE", "game.exe", true)]
    [InlineData("game.exe", "other.exe", false)]
    [InlineData("game.exe", null, false)]
    public void AppliesTo_matches_exe_names(string listed, string? foreground, bool expected) =>
        Assert.Equal(expected, Profile.AppliesTo(new[] { listed }, foreground));

    [Fact]
    public void Empty_list_applies_everywhere() => Assert.True(Profile.AppliesTo(Array.Empty<string>(), null));

    [Fact]
    public void Profile_only_runs_while_its_program_is_in_front()
    {
        var input = new FakeInput { Values = { [Btn] = 1 } };
        var km = new FakeKm();
        var engine = new MappingEngine(input, km, null);
        engine.SetProfiles(new[]
        {
            new Profile { Name = "Game", Applications = { "game.exe" }, Bindings = { new Binding { Input = Btn, Output = OutputRef.Key(0x41) } } },
        });

        engine.ForegroundApp = "explorer.exe"; engine.Tick();
        Assert.Empty(km.Events);
        Assert.Empty(engine.ActiveProfileNames);

        engine.ForegroundApp = "GAME.exe"; engine.Tick();
        Assert.Equal(new[] { "key 65 down" }, km.Events);
        Assert.Equal(new[] { "Game" }, engine.ActiveProfileNames);

        engine.ForegroundApp = "explorer.exe"; engine.Tick();
        Assert.Equal(new[] { "key 65 down", "key 65 up" }, km.Events); // released on switch-away
    }
}

public class BlockingTests
{
    private static readonly InputRef KeyA = new("keyboard", InputKind.Button, 0x41);

    [Fact]
    public void Blocked_inputs_follow_active_bindings_and_mapping_state()
    {
        var engine = new MappingEngine(new FakeInput(), new FakeKm(), null);
        engine.SetProfiles(new[] { new Profile { Bindings = { new Binding { Input = KeyA, Output = OutputRef.Key(0x42), Block = true } } } });

        engine.SetActive(false);
        Assert.False(engine.IsBlocked(KeyA));   // mapping off: never block

        engine.SetActive(true);
        Assert.True(engine.HasBlocks);
        Assert.True(engine.IsBlocked(KeyA));
        Assert.False(engine.IsBlocked(new InputRef("keyboard", InputKind.Button, 0x43)));

        engine.SetProfiles(Array.Empty<Profile>());
        Assert.False(engine.IsBlocked(KeyA));
    }

    [Fact]
    public void Block_only_applies_while_the_profile_is_active()
    {
        var engine = new MappingEngine(new FakeInput(), new FakeKm(), null);
        engine.SetProfiles(new[] { new Profile { Applications = { "game.exe" }, Bindings = { new Binding { Input = KeyA, Output = OutputRef.Key(0x42), Block = true } } } });
        engine.ForegroundApp = "notepad.exe"; engine.Tick();
        Assert.False(engine.IsBlocked(KeyA));
        engine.ForegroundApp = "game.exe"; engine.Tick();
        Assert.True(engine.IsBlocked(KeyA));
    }
}

public class VJoyEngineTests
{
    private static readonly InputRef Stick = new("fake", InputKind.Axis, 0);
    private static readonly InputRef Btn = new("fake", InputKind.Button, 0);

    [Fact]
    public void Axis_and_button_reach_vjoy_and_are_released_on_stop()
    {
        var input = new FakeInput { Values = { [Stick] = -0.5, [Btn] = 1 } };
        var vjoy = new FakeVJoy();
        var engine = new MappingEngine(input, new FakeKm(), null, vjoy);
        engine.SetProfiles(new[] { new Profile { Bindings =
        {
            new Binding { Input = Stick, Output = OutputRef.VJoy(1, VJoyAxis.RZ) },
            new Binding { Input = Btn, Output = OutputRef.VJoyButton(1, 5) },
        } } });

        engine.Tick(); engine.Tick();
        var (dev, state) = Assert.Single(vjoy.Updates); // second tick unchanged -> not resent
        Assert.Equal(1, dev);
        Assert.Equal(-0.5, state.Axes[(int)VJoyAxis.RZ], 6);
        Assert.True(state.Buttons[4]);

        engine.SetActive(false);
        Assert.Equal(new[] { 1 }, vjoy.Releases);
    }

    [Fact]
    public void Vjoy_output_ref_round_trips_with_device_number()
    {
        var a = OutputRef.VJoyButton(2, 10);
        Assert.Equal(9, a.Index);
        Assert.NotEqual(OutputRef.VJoyButton(1, 10), a);
        Assert.Equal("vJoy 2: Button 10", a.ToString());
    }
}

public class CommandTests
{
    private sealed class Host : IControlHost
    {
        public bool IsMapping { get; set; }
        public List<Profile> List { get; } = new()
        {
            new Profile { Name = "Default" },
            new Profile { Name = "Racing Wheel", Enabled = false, Applications = { "dirt.exe" } },
            new Profile { Name = "Racing Pad", Enabled = false },
        };
        public IReadOnlyList<Profile> Profiles => List;
        public string Last = "";
        public void SetProfileEnabled(Profile profile, bool enabled) => profile.Enabled = enabled;
        public void ReloadProfiles() => Last = "reload";
        public void ShowWindow() => Last = "show";
        public void HideWindow() => Last = "hide";
        public void Quit() => Last = "quit";
        public IEnumerable<string> StatusLines() { yield return "Foreground app: x.exe"; }
        public IReadOnlyList<string> ActiveProfileNames => List.Where(p => p.Enabled).Select(p => p.Name).ToList();
        public string ImportUcr(string path, bool perDevice) { Last = $"import {path} {perDevice}"; return "imported"; }
    }

    [Fact]
    public void Game_command_enables_the_profiles_listing_that_game()
    {
        var h = new Host();
        h.List[1].Games.Add("Daytona USA");
        h.List[2].Games.Add("Daytona USA 2");
        var c = new CommandProcessor(h);

        Assert.StartsWith("Enabled for", c.Execute("game \"daytona usa\" --start"));   // exact wins over partial
        Assert.Equal(new[] { false, true, false }, h.List.Select(p => p.Enabled));
        Assert.True(h.IsMapping);
        Assert.StartsWith("error:", c.Execute("game daytona"));      // ambiguous partial
        c.Execute("game \"usa 2\"");
        Assert.Equal(new[] { false, false, true }, h.List.Select(p => p.Enabled));
        Assert.Contains("Daytona USA  →  Racing Wheel", c.Execute("games"));
    }

    [Fact]
    public void Import_command_passes_path_and_flag()
    {
        var h = new Host(); var c = new CommandProcessor(h);
        c.Execute("import \"C:\\UCR v0.9\\context.xml\" --per-device");
        Assert.Equal("import C:\\UCR v0.9\\context.xml True", h.Last);
    }

    [Fact]
    public void Start_stop_toggle()
    {
        var h = new Host(); var c = new CommandProcessor(h);
        Assert.Equal("Mapping ON", c.Execute("start")); Assert.True(h.IsMapping);
        Assert.Equal("Mapping OFF", c.Execute("toggle")); Assert.False(h.IsMapping);
        c.Execute("STOP"); Assert.False(h.IsMapping);
    }

    [Fact]
    public void Enable_by_quoted_name_and_unique_prefix()
    {
        var h = new Host(); var c = new CommandProcessor(h);
        c.Execute("enable \"racing wheel\"");
        Assert.True(h.List[1].Enabled);
        Assert.StartsWith("error:", c.Execute("enable Racing")); // ambiguous
        c.Execute("disable def");
        Assert.False(h.List[0].Enabled);
        Assert.StartsWith("error:", c.Execute("enable nope"));
    }

    [Fact]
    public void Solo_enables_exactly_one()
    {
        var h = new Host(); var c = new CommandProcessor(h);
        c.Execute("solo \"Racing Pad\"");
        Assert.Equal(new[] { false, false, true }, h.List.Select(p => p.Enabled));
    }

    [Fact]
    public void Status_and_profiles_text()
    {
        var h = new Host { IsMapping = true }; var c = new CommandProcessor(h);
        var s = c.Execute("status");
        Assert.Contains("Mapping: ON", s);
        Assert.Contains("Active profiles: Default", s);
        Assert.Contains("Foreground app: x.exe", s);
        var p = c.Execute("profiles");
        Assert.Contains("[x] Default", p);
        Assert.Contains("[ ] Racing Wheel  (0 bindings)  apps: dirt.exe", p);
    }

    [Fact]
    public void Window_and_lifecycle_commands_reach_the_host()
    {
        var h = new Host(); var c = new CommandProcessor(h);
        c.Execute("show"); Assert.Equal("show", h.Last);
        c.Execute("hide"); Assert.Equal("hide", h.Last);
        c.Execute("reload"); Assert.Equal("reload", h.Last);
        c.Execute("quit"); Assert.Equal("quit", h.Last);
        Assert.StartsWith("error: unknown command", c.Execute("dance"));
    }

    [Theory]
    [InlineData("enable \"My Profile\"", new[] { "enable", "My Profile" })]
    [InlineData("  start  ", new[] { "start" })]
    [InlineData("solo a b", new[] { "solo", "a", "b" })]
    [InlineData("enable \"\"", new[] { "enable", "" })]
    public void Command_line_split(string text, string[] expected) => Assert.Equal(expected, CommandLine.Split(text));
}

public class CompatibilityTests
{
    [Fact]
    public void Profiles_saved_by_version_0_1_still_load()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ucrnext-compat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "old.json"), """
                { "Id": "234bd290-ee99-429c-846f-74a9b27407f4", "Name": "Default", "Enabled": true,
                  "Bindings": [ { "Id": "88feb504-1c5d-4fb2-81cb-a2a74e955aa5", "Name": "A", "Enabled": true,
                    "Input": { "DeviceId": "xinput:0", "Kind": "Button", "Index": 10, "Label": "XInput #1 A" },
                    "Output": { "Target": "Keyboard", "Kind": "Button", "Index": 32, "IsTrigger": false },
                    "Settings": { "Deadzone": 0, "Sensitivity": 1, "Invert": false, "ButtonThreshold": 0.5 } } ] }
                """);
            var p = Assert.Single(new ProfileStore(dir).LoadAll());
            Assert.Empty(p.Applications);
            Assert.False(p.Bindings[0].Block);
            Assert.Equal(OutputRef.Key(32), p.Bindings[0].Output);
        }
        finally { Directory.Delete(dir, true); }
    }
}
