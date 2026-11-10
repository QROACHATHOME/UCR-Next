using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using UcrNext.Core;
using UcrNext.Devices;

namespace UcrNext.App;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

public sealed class RelayCommand : ICommand
{
    private readonly Action _run;
    private readonly Func<bool>? _can;
    public RelayCommand(Action run, Func<bool>? can = null) { _run = run; _can = can; }
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
    public bool CanExecute(object? parameter) => _can?.Invoke() ?? true;
    public void Execute(object? parameter) => _run();
}

/// <summary>Group colours offered in the editor (and used in turn for new groups).</summary>
public static class GroupColors
{
    public static readonly string[] Palette =
    {
        "#3B82F6", "#22C55E", "#F59E0B", "#EF4444", "#A855F7",
        "#14B8A6", "#EC4899", "#F97316", "#64748B", "#EAB308",
    };

    public static Color Parse(string? hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex ?? "#8A8A8A"); }
        catch (FormatException) { return Color.FromRgb(0x8A, 0x8A, 0x8A); }
    }

    public static SolidColorBrush Brush(string? hex, byte alpha = 255)
    {
        var c = Parse(hex);
        var b = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
        b.Freeze();
        return b;
    }
}

/// <summary>One entry of the output drop-down.</summary>
public sealed class OutputOption
{
    public OutputOption(OutputRef r) { Ref = r; Display = OutputNames.Describe(r); }
    public OutputRef Ref { get; }
    public string Display { get; }
    public override string ToString() => Display;

    public static IReadOnlyList<OutputOption> All { get; private set; } = Build(null);

    /// <summary>Rebuilds the list once the available vJoy devices are known.</summary>
    public static void Initialize(IEnumerable<VJoyDeviceInfo> vjoy) => All = Build(vjoy);

    private static List<OutputOption> Build(IEnumerable<VJoyDeviceInfo>? vjoy) =>
        OutputNames.All(vjoy).Select(o => new OutputOption(o)).ToList();
}

public sealed class BindingVm : Observable
{
    private readonly Action<BindingVm> _changed;

    public BindingVm(Binding model, ProfileVm owner, Action<BindingVm> changed)
    {
        Model = model; Owner = owner; _changed = changed;
    }

    public Binding Model { get; }
    public ProfileVm Owner { get; }
    public IReadOnlyList<OutputOption> Outputs
    {
        get
        {
            // Keep a saved output selectable even if its vJoy device is currently missing.
            var all = OutputOption.All;
            if (Model.Output is null || all.Any(o => o.Ref.Equals(Model.Output))) return all;
            return all.Append(new OutputOption(Model.Output)).ToList();
        }
    }

    public string Name
    {
        get => Model.Name;
        set { if (Model.Name == value) return; Model.Name = value; Raise(); _changed(this); }
    }

    public bool Enabled
    {
        get => Model.Enabled;
        set { if (Model.Enabled == value) return; Model.Enabled = value; Raise(); _changed(this); }
    }

    /// <summary>The input as bound, with your name for its device in front ("P1 stick · Button 3").</summary>
    public string InputText
    {
        get
        {
            if (Model.Input is not { } i) return Binding.NeedsInput(Model.Mode) ? "(press Bind…)" : "(no input needed)";
            if (i.Slot is { } slot) return $"{slot} (slot) · {i}";
            var alias = DeviceCatalog.Current?.Alias(i.DeviceId);
            return alias is null ? i.ToString() : $"{alias} · {i}";
        }
    }

    public ImageSource? DeviceImage => Model.Input is { } i ? DeviceCatalog.Current?.Image(ProfileVm.SlotDevice(i.DeviceId)) : null;
    public bool HasDeviceImage => DeviceImage is not null;
    public bool HasDeviceIcon => Model.Input is not null && DeviceImage is null;
    public Wpf.Ui.Controls.SymbolRegular DeviceIcon => DeviceCatalog.IconFor(ProfileVm.SlotDevice(Model.Input?.DeviceId ?? ""));
    public string DeviceToolTip => Model.Input is { } i ? ProfileVm.DeviceName(i.DeviceId) : "";

    internal void NotifyDevice()
    {
        foreach (var n in new[] { nameof(InputText), nameof(DeviceImage), nameof(HasDeviceImage), nameof(HasDeviceIcon), nameof(DeviceIcon), nameof(DeviceToolTip) })
            Raise(n);
    }

    public void SetInput(InputRef input)
    {
        Model.Input = input;
        if (Model.Name is "" or "New binding") Model.Name = input.Label;
        if (!IsBlockable) Model.Block = false;
        NotifyDevice(); Raise(nameof(Name)); Raise(nameof(IsAnalogInput)); Raise(nameof(IsBlockable)); Raise(nameof(Block));
        Raise(nameof(CanUseFullRange)); Raise(nameof(ShowBlockHint));
        _changed(this);
        Owner.RequestRebuild(); // group summaries list the devices used
    }

    // ---- Group ----

    /// <summary>The group this binding is shown in (the "not in a group" entry when none).</summary>
    public GroupVm Group
    {
        get => Owner.GroupOf(Model);
        set
        {
            var id = value?.Model?.Id;
            if (Model.GroupId == id) return;
            Model.GroupId = id;
            _changed(this);
            Owner.RequestRebuild(); // deferred: moving the row while its combo box is still handling the click
        }
    }

    public Brush GroupBrush => Group.Brush;

    internal void NotifyGroup() { Raise(nameof(Group)); Raise(nameof(GroupBrush)); }

    public bool IsAnalogInput => Model.Input?.Kind is InputKind.Axis or InputKind.Trigger;

    /// <summary>Keyboard keys and mouse buttons can be blocked; gamepads cannot.</summary>
    public bool IsBlockable => Model.Input?.DeviceId is { } id &&
        (id == KeyboardMouseHookProvider.KeyboardId || id == KeyboardMouseHookProvider.MouseId ||
         id.StartsWith(InterceptionProvider.KeyboardPrefix) || id.StartsWith(InterceptionProvider.MousePrefix));

    /// <summary>Per-device keyboard/mouse through Raw Input: blocking isn't possible, so say why.</summary>
    public bool ShowBlockHint => Model.Input?.DeviceId is { } id &&
        (id.StartsWith(RawInputProvider.KeyboardPrefix) || (id.StartsWith(RawInputProvider.MousePrefix) && Model.Input.Kind == InputKind.Button));

    public OutputOption? SelectedOutput
    {
        get => Model.Output is null ? null : Outputs.FirstOrDefault(o => o.Ref.Equals(Model.Output));
        set { Model.Output = value?.Ref; Raise(); Raise(nameof(CanUseFullRange)); _changed(this); }
    }

    // ---- Modes and extra inputs ----

    public static IReadOnlyList<BindingMode> Modes { get; } = Enum.GetValues<BindingMode>();

    public BindingMode Mode
    {
        get => Model.Mode;
        set
        {
            if (Model.Mode == value) return;
            Model.Mode = value;
            int need = Binding.ExtraInputNames(value).Length;
            while (Model.ExtraInputs.Count < need) Model.ExtraInputs.Add(null);
            if (Model.ExtraInputs.Count > need) Model.ExtraInputs.RemoveRange(need, Model.ExtraInputs.Count - need);
            Raise(); Raise(nameof(Extras)); Raise(nameof(ModeHint)); Raise(nameof(InputText)); Raise(nameof(NeedsOutput));
            _changed(this);
        }
    }

    public string ModeHint => Model.Mode switch
    {
        BindingMode.Direct when Model.Settings.Toggle && Model.Input?.Kind == InputKind.Button => "Toggle: press for on, press again for off",
        BindingMode.Direct when Model.Settings.PulseMs > 0 && Model.Input?.Kind == InputKind.Button =>
            $"Pulse: {Model.Settings.PulseMs} ms press each time the button is {(Model.Settings.PulseOnRelease ? "released" : "pressed")}",
        BindingMode.Direct => "",
        BindingMode.ButtonsToAxis => "Input alone = +1, 2nd button alone = -1",
        BindingMode.StickPair => "Two axes as one stick (circular deadzone, rotation); outputs the X or Y part",
        BindingMode.StickTrim => "Stick with trim: hold Trim to add the current position, Reset clears it",
        BindingMode.AxesRange => "Pressed while both axes are inside the box",
        BindingMode.DeltaToAxis => "Mouse/trackball movement → axis (input: movement X or Y)",
        BindingMode.DeltaToButtons => "Mouse/trackball movement → button for one direction",
        BindingMode.LongPress => "Short press or long press of the input",
        BindingMode.Increment => "Axis moves the output gradually",
        BindingMode.AxisMerger => "Two axes → one (average, greatest or sum), e.g. two pedals",
        BindingMode.AxisToDelta => "Axis → mouse movement (output: Mouse move X or Y)",
        BindingMode.DefaultValue => "Starting value for the output axis until something moves it (no input)",
        BindingMode.FpvMix => "Roll + yaw mixed by a camera angle; outputs roll (X) or yaw (Y)",
        BindingMode.ReversibleThrottle => "Throttle → forward half; the button flips to reverse",
        BindingMode.SetState => string.IsNullOrWhiteSpace(Model.Settings.StateName)
            ? "Switches a named state (set its name in Settings); no output"
            : $"Switches state \"{Model.Settings.StateName.Trim()}\" — no output",
        _ => "",
    };

    /// <summary>SetState bindings have no output.</summary>
    public bool NeedsOutput => Binding.NeedsOutput(Model.Mode);

    /// <summary>"Only while: shift, !menu" (see Settings).</summary>
    public bool HasConditions => Model.Conditions.Count > 0;
    public string ConditionsText => "Only while: " + StateCondition.Format(Model.Conditions);

    public sealed record ExtraSlot(BindingVm Owner, int Index, string Name, string Text);

    public IReadOnlyList<ExtraSlot> Extras => Binding.ExtraInputNames(Model.Mode)
        .Select((name, i) => new ExtraSlot(this, i, name, Model.Extra(i)?.ToString() ?? "(not bound)")).ToList();

    public void SetExtra(int index, InputRef? input)
    {
        while (Model.ExtraInputs.Count <= index) Model.ExtraInputs.Add(null);
        Model.ExtraInputs[index] = input;
        Raise(nameof(Extras));
        _changed(this);
    }

    /// <summary>Call after the settings dialog changed Model.Settings directly.</summary>
    public void SettingsEdited()
    {
        Raise(nameof(Deadzone)); Raise(nameof(Sensitivity)); Raise(nameof(Invert)); Raise(nameof(TriggerFullRange));
        Raise(nameof(HasConditions)); Raise(nameof(ConditionsText)); Raise(nameof(ModeHint));
        _changed(this);
    }

    /// <summary>A stick/pedal axis driving a trigger can use its whole travel.</summary>
    public bool CanUseFullRange => Model.Input?.Kind == InputKind.Axis && Model.Output?.IsTrigger == true;

    public bool TriggerFullRange
    {
        get => Model.Settings.TriggerFullRange;
        set { Model.Settings.TriggerFullRange = value; Raise(); _changed(this); }
    }

    public double Deadzone
    {
        get => Model.Settings.Deadzone;
        set { Model.Settings.Deadzone = Math.Round(value, 2); Raise(); _changed(this); }
    }

    public double Sensitivity
    {
        get => Model.Settings.Sensitivity;
        set { Model.Settings.Sensitivity = Math.Round(value, 2); Raise(); _changed(this); }
    }

    public bool Invert
    {
        get => Model.Settings.Invert;
        set { Model.Settings.Invert = value; Raise(); _changed(this); }
    }

    public bool Block
    {
        get => Model.Block;
        set { Model.Block = value; Raise(); _changed(this); }
    }
}

/// <summary>A group of bindings as shown in the editor; <see cref="Model"/> is null for "not in a group".</summary>
public sealed class GroupVm : Observable
{
    private readonly ProfileVm _owner;
    private bool _ungroupedExpanded = true;
    private bool _showHeader = true;
    private string _summary = "";

    public GroupVm(ProfileVm owner, BindingGroup? model) { _owner = owner; Model = model; }

    public BindingGroup? Model { get; }
    public ProfileVm Owner => _owner;
    public bool IsUngrouped => Model is null;
    public bool IsGroup => Model is not null;
    public ObservableCollection<BindingVm> Bindings { get; } = new();

    public string Name
    {
        get => Model?.Name ?? "Not in a group";
        set
        {
            if (Model is null || Model.Name == value) return;
            Model.Name = value;
            Raise();
            foreach (var b in Bindings) b.NotifyGroup();
            _owner.Changed();
        }
    }

    public string Color
    {
        get => Model?.Color ?? "#8A8A8A";
        set
        {
            if (Model is null || Model.Color == value) return;
            Model.Color = value;
            Raise(); Raise(nameof(Brush)); Raise(nameof(TintBrush));
            foreach (var b in Bindings) b.NotifyGroup();
            _owner.Changed();
            _owner.RequestRebuild(); // the profile list shows the group colours
        }
    }

    public Brush Brush => GroupColors.Brush(Color);
    /// <summary>Faint version of the colour for the group's header background.</summary>
    public Brush TintBrush => GroupColors.Brush(Color, 0x26);

    /// <summary>Folding a group isn't an edit: it is remembered but doesn't count as an unsaved change.</summary>
    public bool IsExpanded
    {
        get => Model is null ? _ungroupedExpanded : !Model.Collapsed;
        set
        {
            if (IsExpanded == value) return;
            if (Model is null) _ungroupedExpanded = value; else Model.Collapsed = !value;
            Raise();
        }
    }

    /// <summary>The "not in a group" header is hidden while the profile has no groups at all.</summary>
    public bool ShowHeader { get => _showHeader; set => Set(ref _showHeader, value); }

    /// <summary>"3 bindings · Xbox controller 1, Keyboard"</summary>
    public string Summary { get => _summary; private set => Set(ref _summary, value); }

    internal void RefreshSummary()
    {
        var devices = Bindings.Select(b => b.Model.Input?.DeviceId).Where(d => !string.IsNullOrEmpty(d))
            .Distinct().Select(d => ProfileVm.DeviceName(d!)).Distinct().ToList();
        Summary = $"{Bindings.Count} binding{(Bindings.Count == 1 ? "" : "s")}" +
                  (devices.Count == 0 ? "" : " · " + string.Join(", ", devices));
    }

    public override string ToString() => Name;
}

public sealed class ProfileVm : Observable
{
    private readonly Action<ProfileVm> _changed;
    private readonly Action<ProfileVm> _enabledChanged;
    private readonly GroupVm _ungrouped;
    private readonly Dictionary<Guid, GroupVm> _groupVms = new();
    private bool _rebuildPending;
    private bool _isDirty;

    /// <summary>Turns a device id into a readable name (set by the main view model, which knows the devices).</summary>
    public static Func<string, string> DeviceName { get; set; } = id => id;
    /// <summary>"slot:P1 stick" → the id of the controller filling it (other ids unchanged).</summary>
    public static Func<string, string> SlotDevice { get; set; } = id => id;

    /// <summary>Wait until every controller the bindings read is connected (no virtual pad for a missing controller).</summary>
    public bool RequireDevices
    {
        get => Model.RequireDevices;
        set { if (Model.RequireDevices == value) return; Model.RequireDevices = value; Raise(); _changed(this); }
    }

    public ProfileVm(Profile model, Action<ProfileVm> changed, Action<ProfileVm> enabledChanged)
    {
        Model = model; _changed = changed; _enabledChanged = enabledChanged;
        _ungrouped = new GroupVm(this, null);
        foreach (var b in model.Bindings) Bindings.Add(NewBindingVm(b));
        foreach (var d in model.SkipIfDevicesPresent) SkipDevices.Add(d);
        RebuildGroups();
    }

    public Profile Model { get; }
    /// <summary>All bindings, in profile order.</summary>
    public ObservableCollection<BindingVm> Bindings { get; } = new();
    /// <summary>What the editor shows: the groups in order, then the bindings not in a group.</summary>
    public ObservableCollection<GroupVm> Groups { get; } = new();
    /// <summary>Choices for a binding's group drop-down ("not in a group" first).</summary>
    public ObservableCollection<GroupVm> GroupChoices { get; } = new();
    public ObservableCollection<DeviceMatch> SkipDevices { get; } = new();

    /// <summary>Has edits that aren't saved yet.</summary>
    public bool IsDirty { get => _isDirty; set => Set(ref _isDirty, value); }

    internal void Changed() => _changed(this);

    private BindingVm NewBindingVm(Binding b) => new(b, this, _ => _changed(this));

    // ---- Groups ----

    public GroupVm GroupOf(Binding b) =>
        b.GroupId is { } id && _groupVms.TryGetValue(id, out var g) ? g : _ungrouped;

    /// <summary>Re-sorts the bindings into their groups (on the next dispatcher turn).</summary>
    public void RequestRebuild()
    {
        if (_rebuildPending) return;
        _rebuildPending = true;
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => { _rebuildPending = false; RebuildGroups(); });
    }

    public void RebuildGroups()
    {
        foreach (var stale in _groupVms.Keys.Where(id => Model.Groups.All(g => g.Id != id)).ToList()) _groupVms.Remove(stale);
        var groups = Model.Groups.Select(g => _groupVms.TryGetValue(g.Id, out var vm) ? vm : _groupVms[g.Id] = new GroupVm(this, g)).ToList();

        var members = groups.Append(_ungrouped).ToDictionary(g => g, _ => new List<BindingVm>());
        foreach (var b in Bindings) members[GroupOf(b.Model)].Add(b);

        _ungrouped.ShowHeader = groups.Count > 0;
        var shown = new List<GroupVm>(groups);
        if (members[_ungrouped].Count > 0 || groups.Count == 0) shown.Add(_ungrouped);

        Sync(Groups, shown);
        Sync(GroupChoices, groups.Prepend(_ungrouped).ToList());
        foreach (var (g, list) in members) { Sync(g.Bindings, list); g.RefreshSummary(); }
        foreach (var b in Bindings) b.NotifyGroup();
        Raise(nameof(Summary)); Raise(nameof(GroupBrushes)); Raise(nameof(IsEmpty));
    }

    /// <summary>Makes <paramref name="target"/> equal to <paramref name="wanted"/> with as few changes as possible.</summary>
    private static void Sync<T>(ObservableCollection<T> target, IList<T> wanted) where T : class
    {
        for (int i = 0; i < wanted.Count; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], wanted[i])) continue;
            int j = target.IndexOf(wanted[i]);
            if (j > i) target.Move(j, i); else target.Insert(i, wanted[i]);
        }
        while (target.Count > wanted.Count) target.RemoveAt(target.Count - 1);
    }

    /// <summary>The first palette colour no group uses yet (cycling once all are used).</summary>
    private string NextColor() =>
        GroupColors.Palette.FirstOrDefault(c => Model.Groups.All(g => !string.Equals(g.Color, c, StringComparison.OrdinalIgnoreCase)))
        ?? GroupColors.Palette[Model.Groups.Count % GroupColors.Palette.Length];

    public GroupVm AddGroup(string? name = null, string? color = null)
    {
        var g = new BindingGroup
        {
            Name = name ?? $"Group {Model.Groups.Count + 1}",
            Color = color ?? NextColor(),
        };
        Model.Groups.Add(g);
        RebuildGroups();
        _changed(this);
        return _groupVms[g.Id];
    }

    /// <summary>Removes the group; its bindings stay, outside any group.</summary>
    public void RemoveGroup(GroupVm g)
    {
        if (g.Model is null) return;
        Model.Groups.Remove(g.Model);
        foreach (var b in Model.Bindings.Where(b => b.GroupId == g.Model.Id)) b.GroupId = null;
        RebuildGroups();
        _changed(this);
    }

    public void MoveGroup(GroupVm g, int delta)
    {
        if (g.Model is null) return;
        int i = Model.Groups.IndexOf(g.Model), j = i + delta;
        if (i < 0 || j < 0 || j >= Model.Groups.Count) return;
        Model.Groups.RemoveAt(i);
        Model.Groups.Insert(j, g.Model);
        RebuildGroups();
        _changed(this);
    }

    /// <summary>
    /// Puts every binding that isn't in a group into a group per input device ("Xbox controller 1",
    /// "Keyboard"…), reusing a group with that name if there is one. Returns how many were grouped.
    /// </summary>
    public int GroupByDevice()
    {
        int n = 0;
        foreach (var b in Model.Bindings.Where(b => GroupOf(b).IsUngrouped && b.Input is not null))
        {
            var name = DeviceName(b.Input!.DeviceId);
            var g = Model.Groups.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.CurrentCultureIgnoreCase));
            if (g is null)
            {
                g = new BindingGroup { Name = name, Color = NextColor() };
                Model.Groups.Add(g);
            }
            b.GroupId = g.Id;
            n++;
        }
        if (n == 0) return 0;
        RebuildGroups();
        _changed(this);
        return n;
    }

    public bool IsEmpty => Bindings.Count == 0;

    public string Summary
    {
        get
        {
            var s = $"{Bindings.Count} binding{(Bindings.Count == 1 ? "" : "s")}";
            return Model.Groups.Count == 0 ? s : s + $" · {Model.Groups.Count} group{(Model.Groups.Count == 1 ? "" : "s")}";
        }
    }

    /// <summary>Colours of the profile's groups, for the dots in the profile list.</summary>
    public IReadOnlyList<Brush> GroupBrushes => Model.Groups.Select(g => (Brush)GroupColors.Brush(g.Color)).ToList();

    // ---- Profile settings ----

    /// <summary>One game per line.</summary>
    public string GamesText
    {
        get => string.Join(Environment.NewLine, Model.Games);
        set
        {
            Model.Games = value.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            Raise(); Raise(nameof(GamesSummary));
            _changed(this);
        }
    }

    public string GamesSummary => Model.Games.Count switch
    {
        0 => "",
        1 => Model.Games[0],
        _ => $"{Model.Games.Count} games",
    };

    public void AddSkipDevice(DeviceInfo d)
    {
        if (Model.SkipIfDevicesPresent.Any(m => m.Matches(d))) return;
        var m = new DeviceMatch { DeviceId = d.DeviceId, Name = d.Name };
        Model.SkipIfDevicesPresent.Add(m);
        SkipDevices.Add(m);
        _changed(this);
    }

    public void RemoveSkipDevice(DeviceMatch m)
    {
        Model.SkipIfDevicesPresent.Remove(m);
        SkipDevices.Remove(m);
        _changed(this);
    }

    public string Name
    {
        get => Model.Name;
        set { if (Model.Name == value) return; Model.Name = value; Raise(); _changed(this); }
    }

    /// <summary>Switching a profile on/off takes effect and is saved at once (it isn't an edit to undo).</summary>
    public bool Enabled
    {
        get => Model.Enabled;
        set { if (Model.Enabled == value) return; Model.Enabled = value; Raise(); _enabledChanged(this); }
    }

    /// <summary>Comma-separated program names; empty = active in every program.</summary>
    public string ApplicationsText
    {
        get => string.Join(", ", Model.Applications);
        set
        {
            Model.Applications = value.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            Raise(); Raise(nameof(AppsSummary));
            _changed(this);
        }
    }

    public string AppsSummary => Model.Applications.Count == 0 ? "all programs" : string.Join(", ", Model.Applications);

    public void AddApplication(string exe)
    {
        if (Model.Applications.Any(a => string.Equals(Profile.NormalizeExe(a), Profile.NormalizeExe(exe), StringComparison.OrdinalIgnoreCase))) return;
        Model.Applications.Add(exe);
        Raise(nameof(ApplicationsText)); Raise(nameof(AppsSummary));
        _changed(this);
    }

    // ---- Bindings ----

    public BindingVm AddBinding(GroupVm? group = null)
    {
        var b = new Binding { GroupId = group?.Model?.Id };
        Model.Bindings.Add(b);
        var vm = NewBindingVm(b);
        Bindings.Add(vm);
        RebuildGroups();
        _changed(this);
        return vm;
    }

    /// <summary>Shows bindings that were already added to <see cref="Model"/> (paste, duplicate).</summary>
    public void ShowAddedBindings(IEnumerable<Binding> added)
    {
        foreach (var b in added) Bindings.Add(NewBindingVm(b));
        RebuildGroups();
        _changed(this);
    }

    public void DuplicateBinding(BindingVm vm)
    {
        var copies = ProfileCopy.PasteBindings(Model, new[] { vm.Model }, Model.Groups);
        foreach (var c in copies) c.Name = vm.Model.Name + " (copy)";
        ShowAddedBindings(copies);
    }

    public void RemoveBinding(BindingVm vm)
    {
        Model.Bindings.Remove(vm.Model);
        Bindings.Remove(vm);
        RebuildGroups();
        _changed(this);
    }
}

public sealed class MainViewModel : Observable
{
    private readonly AppServices _s;
    private readonly DispatcherTimer _changeTimer;
    private readonly DispatcherTimer _refreshTimer;
    private readonly EditHistory _history = new();
    /// <summary>Saved content (<see cref="ProfileCopy.EditKey"/>) of each profile on disk.</summary>
    private readonly Dictionary<Guid, string> _saved = new();
    private ProfileVm? _selected;
    private string _status = "";
    private string _liveText = "";
    private bool _hasUnsaved;
    private Autostart.Mode _autostart;
    private readonly DispatcherTimer _activityTimer;
    private readonly Dictionary<InputRef, double> _lastValues = new();

    public MainViewModel(AppServices services)
    {
        _s = services;
        Catalog = new DeviceCatalog(_s);
        Catalog.Changed += OnCatalogChanged;
        OutputOption.Initialize(_s.VJoy.Devices);
        ProfileVm.DeviceName = DeviceLabel;
        ProfileVm.SlotDevice = id => InputRef.SlotOf(id) is { } n &&
            _s.Settings.DeviceSlots.FirstOrDefault(x => string.Equals(x.Name, n, StringComparison.OrdinalIgnoreCase)) is { } slot
            ? (_s.Engine.SlotAssignments.TryGetValue(n, out var ids) && ids[0] is { } live ? live : slot.Device.DeviceId)
            : id;

        // Edits take effect (and become an undo step) shortly after the last change, so typing
        // or dragging a slider is one step. Nothing is written to disk until Save.
        _changeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _changeTimer.Tick += (_, _) => CommitChange();

        SaveCommand = new RelayCommand(Save, () => HasUnsavedChanges);
        RevertCommand = new RelayCommand(Revert, () => HasUnsavedChanges);
        UndoCommand = new RelayCommand(Undo, () => CanUndo);
        RedoCommand = new RelayCommand(Redo, () => CanRedo);

        LoadFromDisk(resetHistory: true);

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshTimer.Tick += (_, _) => Refresh();
        _refreshTimer.Start();
        // Lights up a device in the list when it is used, to tell identical devices apart.
        _activityTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(80), DispatcherPriority.Background, (_, _) => CheckActivity(), Dispatcher.CurrentDispatcher);
        _activityTimer.Start();
        _autostart = _s.AutostartMode;
        Refresh();

        var ui = Dispatcher.CurrentDispatcher; // constructed on the UI thread
        _s.EngineError += ex => ui.InvokeAsync(() => Status = "Engine error: " + ex.Message);
        Status = $"Profiles folder: {_s.Store.Directory}";
    }

    public AppServices Services => _s;
    public ObservableCollection<ProfileVm> Profiles { get; } = new();
    public ObservableCollection<DeviceInfo> Devices { get; } = new();
    /// <summary>The "Detected devices" list: your names and pictures, hidden ones left out unless shown.</summary>
    public ObservableCollection<DeviceVm> DeviceList { get; } = new();
    public DeviceCatalog Catalog { get; }

    public bool ShowHiddenDevices
    {
        get => _s.Settings.ShowHiddenDevices;
        set { _s.Settings.ShowHiddenDevices = value; _s.SaveSettings(); Raise(); SyncDeviceList(); }
    }

    public int HiddenDeviceCount => Devices.Count(d => Catalog.IsHidden(d.DeviceId));
    public string HiddenDevicesText => $"Show hidden devices ({HiddenDeviceCount})";
    public bool AnyHiddenDevices => HiddenDeviceCount > 0 || ShowHiddenDevices;

    public ProfileVm? Selected { get => _selected; set => Set(ref _selected, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public string PadStatus => _s.Pad.Status;
    /// <summary>"Active: Default, Racing · Foreground: game.exe"</summary>
    public string LiveText { get => _liveText; private set => Set(ref _liveText, value); }
    public string DetailsText => string.Join(Environment.NewLine, _s.StatusLines());
    public bool InterceptionRunning => _s.Interception?.IsRunning == true;

    public bool IsMapping
    {
        get => _s.Runner.IsMapping;
        set
        {
            if (value == _s.Runner.IsMapping) return;
            ApplyPending();
            _s.Runner.IsMapping = value;
            Raise();
            Raise(nameof(MappingButtonText));
            Status = value ? $"Mapping ON ({_s.Engine.ActiveBindingCount} active bindings) — Ctrl+Alt+Pause stops it" : "Mapping OFF";
            Refresh();
        }
    }

    public string MappingButtonText => IsMapping ? "Mapping ON" : "Start mapping";

    // ---- Save / revert / undo ----

    public ICommand SaveCommand { get; }
    public ICommand RevertCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand RedoCommand { get; }

    public bool HasUnsavedChanges
    {
        get => _hasUnsaved;
        private set { if (Set(ref _hasUnsaved, value)) Raise(nameof(WindowTitle)); }
    }

    public string WindowTitle => "UCR-Next — Universal Control Remapper" + (HasUnsavedChanges ? "  •  unsaved changes" : "");

    public bool CanUndo => _history.CanUndo || _changeTimer.IsEnabled;
    public bool CanRedo => _history.CanRedo && !_changeTimer.IsEnabled;

    private IEnumerable<Profile> Models => Profiles.Select(p => p.Model);
    private string HistoryKey() => string.Join("\n", Models.Select(ProfileCopy.EditKey));

    private ProfileVm NewVm(Profile p) => new(p, OnProfileChanged, OnEnabledChanged);

    private void OnProfileChanged(ProfileVm vm)
    {
        _changeTimer.Stop();
        _changeTimer.Start();
        HasUnsavedChanges = true; // confirmed (or cleared, if undone by hand) when the change is committed
        RaiseHistory();
    }

    /// <summary>Hands pending edits to the engine and records them as an undo step.</summary>
    private void CommitChange()
    {
        _changeTimer.Stop();
        PushToEngine();
        _history.Record(ProfileCopy.SerializeAll(Models), HistoryKey());
        UpdateDirty();
        RaiseHistory();
    }

    /// <summary>Applies edits that are still waiting for the short typing delay.</summary>
    public void ApplyPending()
    {
        if (_changeTimer.IsEnabled) CommitChange();
    }

    private void RaiseHistory()
    {
        Raise(nameof(CanUndo)); Raise(nameof(CanRedo));
        CommandManager.InvalidateRequerySuggested();
    }

    private void UpdateDirty()
    {
        bool any = false;
        foreach (var p in Profiles)
        {
            bool dirty = !_saved.TryGetValue(p.Model.Id, out var key) || key != ProfileCopy.EditKey(p.Model);
            p.IsDirty = dirty;
            any |= dirty;
        }
        any |= _saved.Keys.Any(id => Profiles.All(p => p.Model.Id != id)); // deleted, not saved yet
        HasUnsavedChanges = any;
    }

    /// <summary>Writes changed profiles to disk; profiles deleted in the editor go to the "deleted" folder.</summary>
    public void Save()
    {
        ApplyPending();
        int n = 0;
        foreach (var p in Profiles.Where(p => p.IsDirty))
        {
            _s.Store.Save(p.Model);
            _saved[p.Model.Id] = ProfileCopy.EditKey(p.Model);
            n++;
        }
        foreach (var id in _saved.Keys.Where(id => Profiles.All(p => p.Model.Id != id)).ToList())
        {
            _s.Store.Delete(new Profile { Id = id });
            _saved.Remove(id);
            n++;
        }
        UpdateDirty();
        RaiseHistory();
        Status = n == 0 ? "Nothing to save" : $"Saved at {DateTime.Now:T}" + (IsMapping ? $" — {_s.Engine.ActiveBindingCount} active bindings" : "");
    }

    /// <summary>Throws away unsaved edits and goes back to the profiles on disk (Undo brings them back).</summary>
    public void Revert()
    {
        ApplyPending();
        LoadFromDisk(resetHistory: false);
        Status = "Back to the saved profiles — Undo (Ctrl+Z) brings your changes back";
    }

    public void Undo()
    {
        ApplyPending();
        if (_history.Undo() is { } state) { Restore(state); Status = "Undone"; }
    }

    public void Redo()
    {
        ApplyPending();
        if (_history.Redo() is { } state) { Restore(state); Status = "Redone"; }
    }

    private void Restore(string state)
    {
        // Profile on/off isn't part of undo: keep what is switched on now.
        var enabled = Profiles.ToDictionary(p => p.Model.Id, p => p.Model.Enabled);
        var list = ProfileCopy.DeserializeAll(state);
        foreach (var p in list) if (enabled.TryGetValue(p.Id, out var e)) p.Enabled = e;
        SetProfiles(list, _selected?.Model.Id);
        PushToEngine();
        UpdateDirty();
        RaiseHistory();
    }

    private void SetProfiles(IEnumerable<Profile> list, Guid? keepSelected)
    {
        int index = _selected is null ? 0 : Math.Max(0, Profiles.IndexOf(_selected));
        Profiles.Clear();
        foreach (var p in list) Profiles.Add(NewVm(p));
        Selected = Profiles.FirstOrDefault(p => p.Model.Id == keepSelected)
                   ?? (Profiles.Count == 0 ? null : Profiles[Math.Min(index, Profiles.Count - 1)]);
    }

    private void LoadFromDisk(bool resetHistory)
    {
        var list = _s.Store.LoadAll();
        if (list.Count == 0)
        {
            var d = new Profile { Name = "Default" };
            _s.Store.Save(d);
            list.Add(d);
        }
        _saved.Clear();
        foreach (var p in list) _saved[p.Id] = ProfileCopy.EditKey(p);
        SetProfiles(list, _selected?.Model.Id);
        PushToEngine();
        if (resetHistory) _history.Reset(ProfileCopy.SerializeAll(Models), HistoryKey());
        else _history.Record(ProfileCopy.SerializeAll(Models), HistoryKey());
        UpdateDirty();
        RaiseHistory();
    }

    /// <summary>
    /// Switching a profile on or off applies at once and is written to the saved file straight
    /// away (only that flag, so other unsaved edits stay unsaved). Used by the checkbox and the CLI.
    /// </summary>
    private void OnEnabledChanged(ProfileVm vm)
    {
        PushToEngine();
        if (_s.Store.Load(vm.Model.Id) is { } saved)
        {
            saved.Enabled = vm.Model.Enabled;
            _s.Store.Save(saved);
        }
        if (!_changeTimer.IsEnabled) _history.Record(ProfileCopy.SerializeAll(Models), HistoryKey());
    }

    // ---- Settings ----

    public bool StartMappingOnLaunch
    {
        get => _s.Settings.StartMappingOnLaunch;
        set { _s.Settings.StartMappingOnLaunch = value; _s.SaveSettings(); Raise(); }
    }

    public bool ShowTrayIcon
    {
        get => _s.Settings.ShowTrayIcon;
        set { _s.Settings.ShowTrayIcon = value; _s.SaveSettings(); Raise(); }
    }

    public bool CloseToTray
    {
        get => _s.Settings.CloseToTray;
        set { _s.Settings.CloseToTray = value; _s.SaveSettings(); Raise(); }
    }

    public bool UseInterception
    {
        get => _s.Settings.UseInterception;
        set
        {
            _s.Settings.UseInterception = value;
            _s.SaveSettings();
            Raise();
            Status = "Restart UCR-Next to " + (value ? "start" : "stop") + " using the Interception driver.";
        }
    }

    public string Theme
    {
        get => _s.Settings.Theme;
        set
        {
            _s.Settings.Theme = value;
            _s.SaveSettings();
            Theming.Apply(value);
            Raise(); Raise(nameof(ThemeSystem)); Raise(nameof(ThemeLight)); Raise(nameof(ThemeDark));
        }
    }

    public bool ThemeSystem => Theming.Normalize(Theme) == "System";
    public bool ThemeLight => Theming.Normalize(Theme) == "Light";
    public bool ThemeDark => Theming.Normalize(Theme) == "Dark";

    public Autostart.Mode AutostartMode => _autostart;
    public bool AutostartOff => _autostart == Autostart.Mode.Off;
    public bool AutostartLogon => _autostart == Autostart.Mode.Logon;
    public bool AutostartElevated => _autostart == Autostart.Mode.Elevated;

    public void SetAutostart(Autostart.Mode mode)
    {
        var err = Autostart.Set(mode);
        _autostart = _s.AutostartMode = Autostart.Current();
        Raise(nameof(AutostartOff)); Raise(nameof(AutostartLogon)); Raise(nameof(AutostartElevated));
        Status = err is null ? $"Start with Windows: {_autostart}" : "Start with Windows: " + err;
    }

    // ---- Profiles ----

    /// <summary>Imports a UCR context.xml into the profile folder (saved at once) and shows the profiles.</summary>
    public UcrImportResult ImportUcr(string path, bool perDevice)
    {
        ApplyPending();
        var mode = !perDevice ? UcrImporter.PerDevice.Any
            : InterceptionRunning ? UcrImporter.PerDevice.Interception : UcrImporter.PerDevice.RawInput;
        var result = new UcrImporter(KeyMapping.ScanToVk, mode).ImportFile(path);
        result.SaveTo(_s.Store);
        if (result.Slots.Count > 0)
        {
            // UCR shadow devices → device slots (an existing slot with the same name is kept).
            var slots = _s.Settings.DeviceSlots;
            foreach (var sl in result.Slots)
                if (!slots.Any(x => string.Equals(x.Name, sl.Name, StringComparison.OrdinalIgnoreCase))) slots.Add(sl);
            _s.SaveSettings();
            _s.Engine.SetDeviceSlots(slots);
        }
        foreach (var imported in result.Profiles)
        {
            var p = _s.Store.Load(imported.Id) ?? imported;
            var vm = NewVm(p);
            var old = Profiles.FirstOrDefault(x => x.Model.Id == p.Id);
            if (old is null) Profiles.Add(vm);
            else
            {
                Profiles[Profiles.IndexOf(old)] = vm;
                if (_selected == old) Selected = vm;
            }
            _saved[p.Id] = ProfileCopy.EditKey(p);
        }
        CommitChange();
        Status = $"Imported {result.Profiles.Count} UCR profile(s) — they start disabled; tick the ones you want";
        return result;
    }

    /// <summary>
    /// Saves the device slots. Bindings follow renamed slots; with <paramref name="convert"/>,
    /// bindings that read a slot's controller directly are switched to the slot.
    /// </summary>
    public void ApplySlots(List<DeviceSlot> slots, IReadOnlyDictionary<string, string> renames, bool convert)
    {
        ApplyPending();
        InputRef? Map(InputRef? r)
        {
            if (r is null) return null;
            if (r.Slot is { } n)
                return renames.TryGetValue(n, out var to) ? new InputRef(InputRef.SlotId(to), r.Kind, r.Index, r.Label) : null;
            if (!convert) return null;
            var slot = slots.FirstOrDefault(sl => sl.Device.DeviceId.Length > 0 && string.Equals(sl.Device.DeviceId, r.DeviceId, StringComparison.OrdinalIgnoreCase));
            return slot is null ? null : new InputRef(InputRef.SlotId(slot.Name), r.Kind, r.Index, r.Label);
        }
        int changed = 0;
        foreach (var p in Profiles)
            foreach (var b in p.Bindings)
            {
                if (Map(b.Model.Input) is { } input) { b.SetInput(input); changed++; }
                for (int i = 0; i < b.Model.ExtraInputs.Count; i++)
                    if (Map(b.Model.ExtraInputs[i]) is { } extra) { b.SetExtra(i, extra); changed++; }
            }
        _s.Settings.DeviceSlots = slots;
        _s.SaveSettings();
        _s.Engine.SetDeviceSlots(slots);
        foreach (var p in Profiles)
        {
            foreach (var b in p.Bindings) b.NotifyDevice();
            p.RequestRebuild();
        }
        Status = $"Saved {slots.Count} device slot(s)" + (changed == 0 ? "" : $"; {changed} input(s) now read a slot (not saved yet)");
    }

    /// <summary>A control of a slot's controller is bound through the slot.</summary>
    public InputRef UseSlot(InputRef input)
    {
        var assigned = _s.Engine.SlotAssignments;
        foreach (var sl in _s.Settings.DeviceSlots)
            if (assigned.TryGetValue(sl.Name, out var ids) && ids[0] is { } id && string.Equals(id, input.DeviceId, StringComparison.OrdinalIgnoreCase))
                return new InputRef(InputRef.SlotId(sl.Name), input.Kind, input.Index, input.Label);
        return input;
    }

    /// <summary>Re-reads every profile from disk (CLI "reload"; unsaved edits are dropped but can be undone).</summary>
    public void Reload()
    {
        Revert();
        Status = $"Reloaded {Profiles.Count} profile(s)";
    }

    public ProfileVm AddProfile(string name = "New profile") => InsertProfile(new Profile { Name = name }, Profiles.Count);

    private ProfileVm InsertProfile(Profile p, int index)
    {
        var vm = NewVm(p);
        Profiles.Insert(Math.Clamp(index, 0, Profiles.Count), vm);
        Selected = vm;
        CommitChange();
        return vm;
    }

    /// <summary>A copy right below the original; it starts switched off so it doesn't double the mapping.</summary>
    public ProfileVm DuplicateProfile(ProfileVm vm)
    {
        ApplyPending();
        var copy = ProfileCopy.CloneAsNew(vm.Model, ProfileCopy.CopyName(vm.Name, Profiles.Select(p => p.Name)));
        copy.Enabled = false;
        var added = InsertProfile(copy, Profiles.IndexOf(vm) + 1);
        Status = $"Duplicated \"{vm.Name}\" — the copy starts switched off";
        return added;
    }

    /// <summary>Removes the profile from the editor; the file is moved to the "deleted" folder when you save.</summary>
    public void DeleteProfile(ProfileVm vm)
    {
        ApplyPending();
        int i = Profiles.IndexOf(vm);
        Profiles.Remove(vm);
        Selected = Profiles.Count == 0 ? null : Profiles[Math.Min(i, Profiles.Count - 1)];
        CommitChange();
        Status = $"Deleted \"{vm.Name}\" — Undo (Ctrl+Z) brings it back; Save makes it final";
    }

    public string ProfileText(ProfileVm vm)
    {
        ApplyPending();
        return ProfileCopy.ProfileToText(vm.Model);
    }

    public string BindingsText(IEnumerable<BindingVm> bindings)
    {
        ApplyPending();
        var list = bindings.ToList();
        return ProfileCopy.BindingsToText(list.Select(b => b.Model), list.Count == 0 ? Array.Empty<BindingGroup>() : list[0].Owner.Model.Groups);
    }

    /// <summary>
    /// Pastes clipboard/file text: a profile becomes a new profile (switched off); bindings are
    /// added to the selected profile (into <paramref name="intoGroup"/> if given). Returns a status line.
    /// </summary>
    public string Paste(string? text, GroupVm? intoGroup = null)
    {
        ApplyPending();
        if (!ProfileCopy.TryParse(text, out var profile, out var bindings, out var groups))
            return "Nothing to paste: copy a profile or bindings in UCR-Next first.";
        if (profile is not null)
        {
            var name = Profiles.Any(p => string.Equals(p.Name, profile.Name, StringComparison.CurrentCultureIgnoreCase))
                ? ProfileCopy.CopyName(profile.Name, Profiles.Select(p => p.Name)) : profile.Name;
            var copy = ProfileCopy.CloneAsNew(profile, name);
            copy.Enabled = false;
            int at = _selected is null ? Profiles.Count : Profiles.IndexOf(_selected) + 1;
            InsertProfile(copy, at);
            return $"Pasted profile \"{name}\" — it starts switched off";
        }
        if (_selected is not { } target) return "Select a profile to paste the bindings into.";
        var added = ProfileCopy.PasteBindings(target.Model, bindings!, groups!, intoGroup?.Model?.Id);
        target.ShowAddedBindings(added);
        return $"Pasted {added.Count} binding(s) into \"{target.Name}\"";
    }

    public void GroupByDevice(ProfileVm p)
    {
        int n = p.GroupByDevice();
        Status = n == 0 ? "Every bound binding is already in a group" : $"Grouped {n} binding(s) by controller";
    }

    /// <summary>"xinput:0" → "Xbox controller 1", "dinput:…" → the stick's name when it is connected.</summary>
    private string DeviceLabel(string deviceId)
    {
        if (InputRef.SlotOf(deviceId) is { } slotName)
        {
            string dev = ProfileVm.SlotDevice(deviceId);
            return $"Slot \"{slotName}\": " + (dev.Length == 0 || dev == deviceId ? "no controller" : DeviceLabel(dev));
        }
        if (Catalog.Alias(deviceId) is { } alias) return alias;
        var d = Devices.FirstOrDefault(x => string.Equals(x.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
        if (d is not null && !string.IsNullOrWhiteSpace(d.Name)) return d.Name;
        if (deviceId == KeyboardMouseHookProvider.KeyboardId) return "Keyboard";
        if (deviceId == KeyboardMouseHookProvider.MouseId) return "Mouse";
        if (deviceId.StartsWith("xinput:") && int.TryParse(deviceId.AsSpan(7), out var slot)) return $"Xbox controller {slot + 1}";
        if (deviceId.StartsWith(RawInputProvider.KeyboardPrefix) || deviceId.StartsWith(InterceptionProvider.KeyboardPrefix)) return "Keyboard " + deviceId[(deviceId.IndexOf(':') + 1)..];
        if (deviceId.StartsWith(RawInputProvider.MousePrefix) || deviceId.StartsWith(InterceptionProvider.MousePrefix)) return "Mouse " + deviceId[(deviceId.IndexOf(':') + 1)..];
        return deviceId;
    }

    private void PushToEngine() => _s.Engine.SetProfiles(Models);

    private void Refresh()
    {
        var now = _s.Inputs.Devices.ToList();
        if (!now.SequenceEqual(Devices))
        {
            Devices.Clear();
            foreach (var d in now) Devices.Add(d);
            SyncDeviceList();
        }
        var active = _s.Engine.ActiveProfileNames;
        var skipped = _s.Engine.SkippedProfileNames;
        var waiting = _s.Engine.WaitingProfileNames;
        LiveText = $"Active: {(active.Count == 0 ? "none" : string.Join(", ", active))}" +
                   (skipped.Count == 0 ? "" : $"   ·   Skipped (device connected): {string.Join(", ", skipped)}") +
                   (waiting.Count == 0 ? "" : $"   ·   Waiting for controllers: {string.Join(", ", waiting)}") +
                   $"   ·   Foreground: {_s.Engine.ForegroundApp ?? "-"}";
        Raise(nameof(PadStatus));
        Raise(nameof(DetailsText));
    }

    // ---- Devices list ----

    private void OnCatalogChanged()
    {
        foreach (var d in DeviceList) d.Refresh();
        SyncDeviceList();
        foreach (var p in Profiles)
        {
            foreach (var b in p.Bindings) b.NotifyDevice();
            p.RequestRebuild(); // group summaries name the devices
        }
    }

    private void SyncDeviceList()
    {
        var byId = DeviceList.ToDictionary(d => d.DeviceId, StringComparer.OrdinalIgnoreCase);
        var wanted = new List<DeviceVm>();
        foreach (var info in Devices)
        {
            if (!ShowHiddenDevices && Catalog.IsHidden(info.DeviceId)) continue;
            if (byId.TryGetValue(info.DeviceId, out var vm)) vm.Update(info);
            else vm = new DeviceVm(info, Catalog);
            if (!wanted.Contains(vm)) wanted.Add(vm);
        }
        if (!wanted.SequenceEqual(DeviceList))
        {
            DeviceList.Clear();
            foreach (var d in wanted) DeviceList.Add(d);
        }
        Raise(nameof(HiddenDeviceCount)); Raise(nameof(HiddenDevicesText)); Raise(nameof(AnyHiddenDevices));
    }

    /// <summary>
    /// Marks devices that just had a button pressed or an axis moved. Paused while a bind dialog is
    /// open: it reads the same inputs (mouse movement is consumed by whoever reads it).
    /// </summary>
    private void CheckActivity()
    {
        long now = Environment.TickCount64;
        if (System.Windows.Application.Current?.Windows.OfType<BindDialog>().Any() == true) return;
        HashSet<string>? active = null;
        try
        {
            foreach (var (input, value) in _s.Inputs.ReadAll())
            {
                bool hit = input.Kind switch
                {
                    InputKind.Button => value >= 0.5,
                    InputKind.Delta => Math.Abs(value) > 0.05,
                    _ => _lastValues.TryGetValue(input, out var last) && Math.Abs(value - last) > 0.08,
                };
                if (input.Kind is InputKind.Axis or InputKind.Trigger) _lastValues[input] = value;
                if (hit) (active ??= new(StringComparer.OrdinalIgnoreCase)).Add(input.DeviceId);
            }
        }
        catch (Exception) { return; } // a device went away mid-read
        foreach (var d in DeviceList)
        {
            if (active?.Contains(d.DeviceId) == true) d.ActiveUntil = now + 350;
            d.IsActive = now < d.ActiveUntil;
        }
    }
}
