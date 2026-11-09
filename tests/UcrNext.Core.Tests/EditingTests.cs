using UcrNext.Core;

namespace UcrNext.Core.Tests;

public class EditingTests
{
    private static Profile Sample()
    {
        var g = new BindingGroup { Name = "Pad 1", Color = "#22C55E" };
        var p = new Profile { Name = "Racing", Groups = { g } };
        p.Bindings.Add(new Binding { Name = "Gas", GroupId = g.Id, Input = new InputRef("xinput:0", InputKind.Trigger, 1, "RT") });
        p.Bindings.Add(new Binding { Name = "Horn", Input = new InputRef("keyboard", InputKind.Button, 72, "H") });
        return p;
    }

    [Fact]
    public void Profile_copy_paste_round_trips_with_new_ids_and_keeps_groups()
    {
        var p = Sample();
        Assert.True(ProfileCopy.TryParse(ProfileCopy.ProfileToText(p), out var parsed, out var b, out _));
        Assert.Null(b);
        var copy = ProfileCopy.CloneAsNew(parsed!, "Racing (copy)");

        Assert.NotEqual(p.Id, copy.Id);
        Assert.NotEqual(p.Groups[0].Id, copy.Groups[0].Id);
        Assert.All(copy.Bindings, x => Assert.DoesNotContain(p.Bindings, o => o.Id == x.Id));
        Assert.Equal(copy.Groups[0].Id, copy.Bindings[0].GroupId);   // group link remapped
        Assert.Null(copy.Bindings[1].GroupId);
        Assert.Equal("Pad 1", copy.Groups[0].Name);
    }

    [Fact]
    public void Pasted_bindings_join_a_group_of_the_same_name_or_create_it()
    {
        var src = Sample();
        var text = ProfileCopy.BindingsToText(src.Bindings, src.Groups);
        Assert.True(ProfileCopy.TryParse(text, out var prof, out var bindings, out var groups));
        Assert.Null(prof);

        var target = new Profile { Groups = { new BindingGroup { Name = "pad 1" } } };
        var added = ProfileCopy.PasteBindings(target, bindings!, groups!);
        Assert.Equal(2, added.Count);
        Assert.Single(target.Groups);                                    // matched by name, case-insensitive
        Assert.Equal(target.Groups[0].Id, added[0].GroupId);

        var empty = new Profile();
        ProfileCopy.PasteBindings(empty, bindings!, groups!);
        Assert.Equal("Pad 1", Assert.Single(empty.Groups).Name);         // created with the source colour
        Assert.Equal("#22C55E", empty.Groups[0].Color);

        var into = new Profile { Groups = { new BindingGroup { Name = "Other" } } };
        var all = ProfileCopy.PasteBindings(into, bindings!, groups!, into.Groups[0].Id);
        Assert.All(all, x => Assert.Equal(into.Groups[0].Id, x.GroupId));
    }

    [Fact]
    public void A_stored_profile_file_can_be_pasted_and_junk_is_rejected()
    {
        Assert.True(ProfileCopy.TryParse(ProfileCopy.Serialize(Sample()), out var p, out _, out _));
        Assert.Equal("Racing", p!.Name);
        Assert.False(ProfileCopy.TryParse("hello", out _, out _, out _));
        Assert.False(ProfileCopy.TryParse("{\"x\":1}", out _, out _, out _));
    }

    [Fact]
    public void Copy_names_count_up()
    {
        Assert.Equal("A (copy)", ProfileCopy.CopyName("A", new[] { "A" }));
        Assert.Equal("A (copy 3)", ProfileCopy.CopyName("A", new[] { "A", "A (copy)", "a (COPY 2)" }));
        Assert.Equal("A (copy 2)", ProfileCopy.CopyName("A (copy)", new[] { "A", "A (copy)" }));   // copy of a copy
    }

    [Fact]
    public void Edit_key_ignores_enabled_and_folding()
    {
        var p = Sample();
        var key = ProfileCopy.EditKey(p);
        p.Enabled = !p.Enabled;
        p.Groups[0].Collapsed = true;
        Assert.Equal(key, ProfileCopy.EditKey(p));
        p.Bindings[0].Name = "Throttle";
        Assert.NotEqual(key, ProfileCopy.EditKey(p));
    }

    [Fact]
    public void History_undoes_and_redoes_and_new_edits_drop_redo()
    {
        var h = new EditHistory();
        h.Reset("s0", "k0");
        Assert.False(h.CanUndo);
        Assert.True(h.Record("s1", "k1"));
        Assert.False(h.Record("s1b", "k1"));        // same content: no new step
        Assert.True(h.Record("s2", "k2"));
        Assert.Equal("s1b", h.Undo());
        Assert.Equal("s0", h.Undo());
        Assert.Null(h.Undo());
        Assert.Equal("s1b", h.Redo());
        h.Record("s3", "k3");
        Assert.False(h.CanRedo);
        Assert.Equal("s1b", h.Undo());
    }

    [Fact]
    public void History_is_limited()
    {
        var h = new EditHistory(limit: 3);
        h.Reset("s0", "k0");
        for (int i = 1; i <= 5; i++) h.Record("s" + i, "k" + i);
        Assert.Equal("s4", h.Undo());
        Assert.Equal("s3", h.Undo());
        Assert.False(h.CanUndo);
    }

    [Fact]
    public void Deleted_profiles_are_kept_in_the_deleted_folder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ucrnext-del-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ProfileStore(dir);
            var p = Sample();
            store.Save(p);
            Assert.NotNull(store.Load(p.Id));
            store.Delete(p);
            Assert.Empty(store.LoadAll());
            Assert.Null(store.Load(p.Id));
            Assert.True(File.Exists(Path.Combine(dir, "deleted", p.Id.ToString("N") + ".json")));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
