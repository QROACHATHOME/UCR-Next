namespace UcrNext.Core.Tests;

/// <summary>Standing in for UCR.exe: its --profile option and how it finds profiles.</summary>
public class UcrCompatTests
{
    [Theory]
    [InlineData(new[] { "--profile", "Racing" }, "Racing")]
    [InlineData(new[] { "-p", "Racing,Wheel" }, "Racing,Wheel")]
    [InlineData(new[] { "/p", "Racing" }, "Racing")]
    [InlineData(new[] { "--profile=Racing" }, "Racing")]
    [InlineData(new[] { "-p:Racing" }, "Racing")]
    [InlineData(new[] { "-PROFILE", "Daytona USA" }, "Daytona USA")]
    [InlineData(new[] { "--start" }, null)]
    [InlineData(new[] { "--profiles", "C:\\dir" }, null)]
    [InlineData(new string[0], null)]
    public void Reads_ucr_profile_option(string[] args, string? expected) =>
        Assert.Equal(expected, UcrCompat.ProfileArgument(args));

    private static readonly List<Profile> Profiles = new()
    {
        new Profile { Name = "Racing" },
        new Profile { Name = "Racing › Wheel" },
        new Profile { Name = "Racing › Pad" },
        new Profile { Name = "Shooters › Light gun › Wheel" },
        new Profile { Name = "Fighters" },
    };

    [Theory]
    [InlineData("Racing", "Racing")]
    [InlineData("racing,wheel", "Racing › Wheel")]                // case-insensitive, comma = child
    [InlineData(" Racing , Pad ", "Racing › Pad")]
    [InlineData("Wheel", "Racing › Wheel")]                       // first name matches at any level, shallowest first
    [InlineData("Light gun,Wheel", "Shooters › Light gun › Wheel")]
    [InlineData("Racing,Missing", "Racing")]                      // chain breaks off: deepest match, like UCR
    [InlineData("Fight", null)]                                    // exact names only
    [InlineData("Nothing", null)]
    public void Finds_profiles_like_ucr(string search, string? expected) =>
        Assert.Equal(expected, UcrCompat.FindProfile(Profiles, search)?.Name);

    private sealed class Host : IControlHost
    {
        public bool IsMapping { get; set; }
        public List<Profile> List { get; } = new() { new Profile { Name = "Default" }, new Profile { Name = "Racing › Wheel", Enabled = false } };
        public IReadOnlyList<Profile> Profiles => List;
        public void SetProfileEnabled(Profile profile, bool enabled) => profile.Enabled = enabled;
        public void ReloadProfiles() { }
        public bool HasUnsavedChanges => false;
        public void SaveProfiles() { }
        public void ShowWindow() { }
        public void HideWindow() { }
        public void Quit() { }
        public IEnumerable<string> StatusLines() { yield break; }
        public IReadOnlyList<string> ActiveProfileNames => List.Where(p => p.Enabled).Select(p => p.Name).ToList();
        public string ImportUcr(string path, bool perDevice) => "imported";
    }

    [Fact]
    public void Ucr_command_loads_only_that_profile_and_starts_mapping()
    {
        var h = new Host();
        var reply = new CommandProcessor(h).Execute("ucr \"Racing,Wheel\"");
        Assert.StartsWith("Loaded", reply);
        Assert.True(h.IsMapping);
        Assert.Equal(new[] { "Racing › Wheel" }, h.ActiveProfileNames);
    }

    [Fact]
    public void Ucr_command_reports_unknown_profile()
    {
        var h = new Host();
        Assert.StartsWith("error", new CommandProcessor(h).Execute("ucr Nope"));
        Assert.False(h.IsMapping);
        Assert.True(h.List[0].Enabled);   // nothing changed
    }
}
