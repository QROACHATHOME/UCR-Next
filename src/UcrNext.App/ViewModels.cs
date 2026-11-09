using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using UcrNext.Core;

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

    public static readonly IReadOnlyList<OutputOption> All = OutputNames.All().Select(o => new OutputOption(o)).ToList();
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
    public IReadOnlyList<OutputOption> Outputs => OutputOption.All;

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
        Raise(nameof(InputText)); Raise(nameof(Name)); Raise(nameof(IsAnalogInput));
        _changed(this);
    }

    public bool IsAnalogInput => Model.Input?.Kind is InputKind.Axis or InputKind.Trigger;

    public OutputOption? SelectedOutput
    {
        get => Model.Output is null ? null : OutputOption.All.FirstOrDefault(o => o.Ref.Equals(Model.Output));
        set { Model.Output = value?.Ref; Raise(); _changed(this); }
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
}

public sealed class ProfileVm : Observable
{
    private readonly Action<ProfileVm> _changed;

    public ProfileVm(Profile model, Action<ProfileVm> changed)
    {
        Model = model; _changed = changed;
        foreach (var b in model.Bindings) Bindings.Add(new BindingVm(b, this, bv => _changed(this)));
    }

    public Profile Model { get; }
    public ObservableCollection<BindingVm> Bindings { get; } = new();

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
    private readonly DispatcherTimer _deviceTimer;
    private ProfileVm? _selected;
    private string _status = "";

    public MainViewModel(AppServices services)
    {
        _s = services;
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveTimer.Tick += (_, _) => Flush();

        foreach (var p in _s.Store.LoadAll()) Profiles.Add(new ProfileVm(p, OnProfileChanged));
        if (Profiles.Count == 0) AddProfile("Default");
        Selected = Profiles[0];

        _deviceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _deviceTimer.Tick += (_, _) => RefreshDevices();
        _deviceTimer.Start();
        RefreshDevices();

        var ui = Dispatcher.CurrentDispatcher; // constructed on the UI thread
        _s.EngineError += ex => ui.InvokeAsync(() => Status = "Engine error: " + ex.Message);
        PushToEngine();
        Status = $"Profiles folder: {_s.Store.Directory}";
    }

    public AppServices Services => _s;
    public ObservableCollection<ProfileVm> Profiles { get; } = new();
    public ObservableCollection<DeviceInfo> Devices { get; } = new();

    public ProfileVm? Selected { get => _selected; set => Set(ref _selected, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public string PadStatus => _s.Pad.Status;

    public bool IsMapping
    {
        get => _s.Runner.IsMapping;
        set
        {
            Flush();
            _s.Runner.IsMapping = value;
            Raise();
            Raise(nameof(MappingButtonText));
            Status = value ? $"Mapping ON ({_s.Engine.ActiveBindingCount} active bindings)" : "Mapping OFF";
        }
    }

    public string MappingButtonText => IsMapping ? "■ Stop mapping" : "▶ Start mapping";

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
        Raise(nameof(PadStatus));
    }

    private void PushToEngine() => _s.Engine.SetProfiles(Profiles.Select(p => p.Model));

    private void RefreshDevices()
    {
        var now = _s.Inputs.Devices.ToList();
        if (!now.SequenceEqual(Devices))
        {
            Devices.Clear();
            foreach (var d in now) Devices.Add(d);
        }
        Raise(nameof(PadStatus));
    }
}
