namespace UcrNext.Core.Tests;

public class ProfileStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ucrnext-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public void Profiles_round_trip_through_json()
    {
        var store = new ProfileStore(_dir);
        var p = new Profile
        {
            Name = "Racing",
            Bindings =
            {
                new Binding
                {
                    Name = "A to Space",
                    Input = new InputRef("xinput:0", InputKind.Button, 10, "XInput #1 A"),
                    Output = OutputRef.Key(0x20),
                },
                new Binding
                {
                    Input = new InputRef("winmm:046D:C24F:0", InputKind.Axis, 0),
                    Output = OutputRef.Xbox(XboxAxis.LeftX),
                    Settings = new AxisSettings { Deadzone = 0.1, Sensitivity = 1.5, Invert = true },
                },
            },
        };
        store.Save(p);

        var loaded = new ProfileStore(_dir).LoadAll();

        var q = Assert.Single(loaded);
        Assert.Equal("Racing", q.Name);
        Assert.Equal(p.Id, q.Id);
        Assert.Equal(2, q.Bindings.Count);
        Assert.Equal(p.Bindings[0].Input, q.Bindings[0].Input);
        Assert.Equal("XInput #1 A", q.Bindings[0].Input!.Label);
        Assert.Equal(OutputRef.Key(0x20), q.Bindings[0].Output);
        Assert.Equal(OutputRef.Xbox(XboxAxis.LeftX), q.Bindings[1].Output);
        Assert.True(q.Bindings[1].Settings.Invert);
        Assert.Equal(1.5, q.Bindings[1].Settings.Sensitivity);
    }

    [Fact]
    public void Enums_are_saved_as_text()
    {
        var store = new ProfileStore(_dir);
        store.Save(new Profile { Bindings = { new Binding { Output = OutputRef.Xbox(XboxButton.A) } } });
        var json = File.ReadAllText(Directory.GetFiles(_dir, "*.json").Single());
        Assert.Contains("\"Xbox360\"", json);
    }

    [Fact]
    public void Delete_removes_the_file_and_corrupt_files_are_skipped()
    {
        var store = new ProfileStore(_dir);
        var p = new Profile();
        store.Save(p);
        File.WriteAllText(Path.Combine(_dir, "broken.json"), "{ not json");

        Assert.Single(store.LoadAll());
        store.Delete(p);
        Assert.Empty(store.LoadAll());
    }
}
