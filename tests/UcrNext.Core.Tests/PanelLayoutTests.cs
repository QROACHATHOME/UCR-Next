namespace UcrNext.Core.Tests;

/// <summary>The arcade panel layout: LED-Next import, links, ids.</summary>
public class PanelLayoutTests
{
    private const string LedNextCabinet = """
        {
          "version": 1,
          "panel": {
            "width": 1600, "height": 640, "backgroundImage": null, "backgroundOpacity": 0.4,
            "buttons": [
              { "id": "P1_B1", "label": "1", "player": 1, "kind": "Button", "x": 560, "y": 300, "size": 56 },
              { "id": "P1_START", "label": "", "player": 1, "kind": "Start", "x": 400, "y": 120, "size": 40 },
              { "id": "P1_SPIN", "label": "", "player": 1, "kind": "Spinner", "x": 300, "y": 300, "size": 70 },
              { "id": "P2_STICK", "label": "", "player": 2, "kind": 7, "x": 900, "y": 300, "size": 60 }
            ]
          },
          "boards": []
        }
        """;

    private static string WriteTemp(string text)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ucrnext-panel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "cabinet.json");
        File.WriteAllText(file, text);
        return file;
    }

    [Fact]
    public void Imports_led_next_cabinet()
    {
        var (layout, photo) = PanelLayout.ImportLedNext(WriteTemp(LedNextCabinet));
        Assert.Null(photo);
        Assert.Equal(0.4, layout.BackgroundOpacity, 6);
        Assert.Equal(4, layout.Controls.Count);
        var b1 = layout.Find("P1_B1")!;
        Assert.Equal(("1", 1, 560.0, 300.0, 56.0), (b1.Label, b1.Player, b1.X, b1.Y, b1.Size));
        Assert.Equal(PanelControlKind.Start, layout.Find("P1_START")!.Kind);
        Assert.Equal(PanelControlKind.Spinner, layout.Find("P1_SPIN")!.Kind);
        Assert.Equal(PanelControlKind.Joystick, layout.Find("P2_STICK")!.Kind); // kind as a number
    }

    [Fact]
    public void Rejects_a_file_without_a_panel() =>
        Assert.Throws<FormatException>(() => PanelLayout.ImportLedNext(WriteTemp("""{ "version": 1 }""")));

    [Fact]
    public void Reimport_keeps_links_of_controls_with_the_same_id()
    {
        var current = new PanelLayout();
        current.Controls.Add(new PanelControl { Id = "P1_B1", X = 1, Links = { new PanelLink { Part = "Press", Input = new InputRef("keyboard", InputKind.Button, 0x41) } } });
        current.Controls.Add(new PanelControl { Id = "GONE", Links = { new PanelLink { Part = "Press", Input = new InputRef("keyboard", InputKind.Button, 0x42) } } });
        var (imported, _) = PanelLayout.ImportLedNext(WriteTemp(LedNextCabinet));
        current.MergeFrom(imported);
        Assert.Equal(4, current.Controls.Count);
        Assert.Null(current.Find("GONE"));
        var b1 = current.Find("P1_B1")!;
        Assert.Equal(560, b1.X);                  // position from LED-Next
        Assert.True(b1.IsLinked);                  // link kept
    }

    [Fact]
    public void Parts_and_linked_state()
    {
        var stick = new PanelControl { Kind = PanelControlKind.Joystick };
        Assert.Equal(new[] { "Up", "Down", "Left", "Right" }, stick.Parts);
        stick.Links.Add(new PanelLink { Part = "Up", Input = new InputRef("keyboard", InputKind.Button, 0x26) });
        Assert.False(stick.IsLinked);
        foreach (var p in new[] { "Down", "Left", "Right" }) stick.Links.Add(new PanelLink { Part = p, Input = new InputRef("keyboard", InputKind.Button, 0x27) });
        Assert.True(stick.IsLinked);
    }

    [Theory]
    [InlineData(InputKind.Button, 0, 1.0, true)]
    [InlineData(InputKind.Button, 0, 0.0, false)]
    [InlineData(InputKind.Axis, 1, 0.8, true)]
    [InlineData(InputKind.Axis, 1, -0.8, false)]   // stick pushed the other way
    [InlineData(InputKind.Axis, -1, -0.8, true)]
    [InlineData(InputKind.Delta, 0, -3.0, true)]    // spinner turning
    [InlineData(InputKind.Delta, 0, 0.0, false)]
    public void Active_when_pressed_in_its_direction(InputKind kind, int sign, double value, bool expected) =>
        Assert.Equal(expected, PanelControl.IsActive(new PanelLink { Input = new InputRef("x", kind, 0), Sign = sign }, value));

    [Fact]
    public void Ids_for_copies()
    {
        var l = new PanelLayout();
        l.Controls.Add(new PanelControl { Id = "P1_B8" });
        l.Controls.Add(new PanelControl { Id = "P1_B9" });
        Assert.Equal("P1_B10", l.NextIdAfter("P1_B8"));
        Assert.Equal("COIN_2", l.NextIdAfter("COIN"));
    }

    [Fact]
    public void Saves_and_loads_next_to_the_profiles_folder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ucrnext-panel-" + Guid.NewGuid().ToString("N"));
        var folder = PanelLayout.FolderFor(Path.Combine(dir, "profiles") + "\\");
        Assert.Equal(dir, folder);
        var l = new PanelLayout { BackgroundImage = "panel.png" };
        l.Controls.Add(new PanelControl { Id = "P1_B1", Links = { new PanelLink { Input = new InputRef("rawmouse:1241:1111@P1", InputKind.Delta, 0) } } });
        l.Save(PanelLayout.PathIn(folder));
        var back = PanelLayout.Load(PanelLayout.PathIn(folder));
        Assert.Equal("panel.png", back.BackgroundImage);
        Assert.Equal("rawmouse:1241:1111@P1", back.Find("P1_B1")!.Links[0].Input.DeviceId);
    }
}
