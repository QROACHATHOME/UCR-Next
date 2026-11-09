using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
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

    public string InputText => Model.Input?.ToString() ?? "(press Bind…)";

    public void SetInput(InputRef input)
    {
        Model.Input = input;
        if (Model.Name is "" or "New binding") Model.Name = input.Label;
        if (!IsBlockable) Model.Block = false;
        Raise(nameof(InputText)); Raise(nameof(Name)); Raise(nameof(IsAnalogInput)); Raise(nameof(IsBlockable)); Raise(nameof(Block));
        Raise(nameof(CanUseFullRange));
        _changed(this);
    }

    public bool IsAnalogInput => Model.Input?.Kind is InputKind.Axis or InputKind.Trigger;

    /// <summary>Keyboard keys and mouse buttons can be blocked; gamepads cannot.</summary>
    public bool IsBlockable => Model.Input?.DeviceId is { } id &&
        (id == KeyboardMouseHookProvider.KeyboardId || id == KeyboardMouseHookProvider.MouseId ||
         id.StartsWith(InterceptionProvider.KeyboardPrefix) || id.StartsWith(InterceptionProvider.MousePrefix));

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
            Raise(); Raise(nameof(Extras)); Raise(nameof(ModeHint));
            _changed(this);
        }
    }

    public string ModeHint => Model.Mode switch
    {
        BindingMode.Direct => "",
        BindingMode.ButtonsToAxis => "Input alone = +1, 2nd button alone = -1",
        BindingMode.StickPair => "Two axes as one stick (circular deadzone, rotation); outputs the X or Y part",
        BindingMode.StickTrim => "Stick with trim: hold Trim to add the current position, Reset clears it",
        BindingMode.AxesRange => "Pressed while both axes are inside the box",
        BindingMode.DeltaToAxis => "Mouse/trackball movement → axis (input: movement X or Y)",
        BindingMode.DeltaToButtons => "Mouse/trackball movement → button for one direction",
        BindingMode.LongPress => "Short press or long press of the input",
        BindingMode.Increment => "Axis moves the output gradually",
        _ => "",
    };

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

public sealed class ProfileVm : Observable
{
    private readonly Action<ProfileVm> _changed;

    public ProfileVm(Profile model, Action<ProfileVm> changed)
    {
        Model = model; _changed = changed;
        foreach (var b in model.Bindings) Bindings.Add(new BindingVm(b, this, bv => _changed(this)));
        foreach (var d in model.SkipIfDevicesPresent) SkipDevices.Add(d);
    }

    public Profile Model { get; }
    public ObservableCollection<BindingVm> Bindings { get; } = new();
    public ObservableCollection<DeviceMatch> SkipDevices { get; } = new();

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

    public bool Enabled
    {
        get => Model.Enabled;
        set { if (Model.Enabled == value) return; Model.Enabled = value; Raise(); _changed(this); }
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

    public BindingVm AddBinding()
    {
        var b = new Binding();
        Model.Bindings.Add(b);
        var vm = new BindingVm(b, this, bv => _changed(this));
        Bindings.Add(vm);
        _changed(this);
        return vm;
    }

    public void RemoveBinding(BindingVm vm)
    {
        Model.Bindings.Remove(vm.Model);
        Bindings.Remove(vm);
        _changed(this);
    }
}

public sealed class MainViewModel : Observable
{
    private readonly AppServices _s;
    private readonly HashSet<ProfileVm> _dirty = new();
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _refreshTimer;
    private ProfileVm? _selected;
    private string _status = "";
    private string _liveText = "";
    private Autostart.Mode _autostart;

    public MainViewModel(AppServices services)
    {
        _s = services;
        OutputOption.Initialize(_s.VJoy.Devices);

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveTimer.Tick += (_, _) => Flush();

        LoadProfiles();

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshTimer.Tick += (_, _) => Refresh();
        _refreshTimer.Start();
        _autostart = Autostart.Current();
        Refresh();

        var ui = Dispatcher.CurrentDispatcher; // constructed on the UI thread
        _s.EngineError += ex => ui.InvokeAsync(() => Status = "Engine error: " + ex.Message);
        Status = $"Profiles folder: {_s.Store.Directory}";
    }

    public AppServices Services => _s;
    public ObservableCollection<ProfileVm> Profiles { get; } = new();
    public ObservableCollection<DeviceInfo> Devices { get; } = new();

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
            Flush();
            _s.Runner.IsMapping = value;
            Raise();
            Raise(nameof(MappingButtonText));
            Status = value ? $"Mapping ON ({_s.Engine.ActiveBindingCount} active bindings) — Ctrl+Alt+Pause stops it" : "Mapping OFF";
            Refresh();
        }
    }

    public string MappingButtonText => IsMapping ? "■ Stop mapping" : "▶ Start mapping";

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

    public Autostart.Mode AutostartMode => _autostart;
    public bool AutostartOff => _autostart == Autostart.Mode.Off;
    public bool AutostartLogon => _autostart == Autostart.Mode.Logon;
    public bool AutostartElevated => _autostart == Autostart.Mode.Elevated;

    public void SetAutostart(Autostart.Mode mode)
    {
        var err = Autostart.Set(mode);
        _autostart = Autostart.Current();
        Raise(nameof(AutostartOff)); Raise(nameof(AutostartLogon)); Raise(nameof(AutostartElevated));
        Status = err is null ? $"Start with Windows: {_autostart}" : "Start with Windows: " + err;
    }

    // ---- Profiles ----

    private void LoadProfiles()
    {
        Guid? keep = _selected?.Model.Id;
        Profiles.Clear();
        foreach (var p in _s.Store.LoadAll()) Profiles.Add(new ProfileVm(p, OnProfileChanged));
        if (Profiles.Count == 0) AddProfile("Default");
        Selected = Profiles.FirstOrDefault(p => p.Model.Id == keep) ?? Profiles[0];
        PushToEngine();
    }

    /// <summary>Imports a UCR context.xml into the profile folder and reloads.</summary>
    public UcrImportResult ImportUcr(string path, bool perDevice)
    {
        Flush();
        var mode = !perDevice ? UcrImporter.PerDevice.Any
            : InterceptionRunning ? UcrImporter.PerDevice.Interception : UcrImporter.PerDevice.RawInput;
        var result = new UcrImporter(KeyMapping.ScanToVk, mode).ImportFile(path);
        result.SaveTo(_s.Store);
        LoadProfiles();
        Status = $"Imported {result.Profiles.Count} UCR profile(s) — they start disabled; tick the ones you want";
        return result;
    }

    /// <summary>Re-reads every profile from disk (unsaved edits are saved first).</summary>
    public void Reload()
    {
        Flush();
        LoadProfiles();
        Status = $"Reloaded {Profiles.Count} profile(s)";
    }

    public ProfileVm AddProfile(string name = "New profile")
    {
        var vm = new ProfileVm(new Profile { Name = name }, OnProfileChanged);
        Profiles.Add(vm);
        OnProfileChanged(vm);
        Selected = vm;
        return vm;
    }

    public void DeleteProfile(ProfileVm vm)
    {
        _dirty.Remove(vm);
        Profiles.Remove(vm);
        _s.Store.Delete(vm.Model);
        Selected = Profiles.FirstOrDefault();
        PushToEngine();
    }

    private void OnProfileChanged(ProfileVm vm)
    {
        _dirty.Add(vm);
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>Saves changed profiles and hands the new bindings to the engine.</summary>
    public void Flush()
    {
        _saveTimer.Stop();
        if (_dirty.Count == 0) return;
        foreach (var p in _dirty) _s.Store.Save(p.Model);
        _dirty.Clear();
        PushToEngine();
        Status = $"Saved at {DateTime.Now:T}" + (IsMapping ? $" — {_s.Engine.ActiveBindingCount} active bindings" : "");
        Refresh();
    }

    private void PushToEngine() => _s.Engine.SetProfiles(Profiles.Select(p => p.Model));

    private void Refresh()
    {
        var now = _s.Inputs.Devices.ToList();
        if (!now.SequenceEqual(Devices))
        {
            Devices.Clear();
            foreach (var d in now) Devices.Add(d);
        }
        var active = _s.Engine.ActiveProfileNames;
        var skipped = _s.Engine.SkippedProfileNames;
        LiveText = $"Active profiles: {(active.Count == 0 ? "none" : string.Join(", ", active))}" +
                   (skipped.Count == 0 ? "" : $"   ·   Skipped (device connected): {string.Join(", ", skipped)}") +
                   $"   ·   Foreground: {_s.Engine.ForegroundApp ?? "-"}";
        Raise(nameof(PadStatus));
        Raise(nameof(DetailsText));
    }
}
