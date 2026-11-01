using CommunityToolkit.Mvvm.ComponentModel;
using LightCraftMidi.Core.Mapping;
using LightCraftMidi.Core.Midi;

namespace LightCraftMidi.ViewModels;

/// <summary>Editable wrapper around a <see cref="MidiMapping"/>; every edit is written through to the model.</summary>
public sealed partial class MappingViewModel : ObservableObject
{
    private readonly Action<MappingViewModel> _changed;

    public MappingViewModel(MidiMapping model, Action<MappingViewModel> changed)
    {
        Model = model;
        _changed = changed;
    }

    public MidiMapping Model { get; }
    public bool IsGlobal { get; set; }
    public string Scope => IsGlobal ? "Global mappings" : "Profile mappings";

    [ObservableProperty] private bool _isLearning;
    [ObservableProperty] private string? _warning;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowParams))] private string? _paramsHint;
    public bool ShowParams => IsCommand && !string.IsNullOrWhiteSpace(ParamsHint);
    [ObservableProperty] private string? _rangeHint;
    [ObservableProperty] private bool _isActive;

    public int Channel => Model.Channel;
    public MidiMessageType MessageType => Model.MessageType;
    public int Number => Model.Number;
    public MappingTargetKind TargetKind { get => Model.TargetKind; set => Set(Model.TargetKind, value, v => Model.TargetKind = v); }
    public string TargetId
    {
        get => Model.TargetId;
        set
        {
            // A profile-list rename can temporarily clear ComboBox selection.
            if (value == null && Model.TargetKind == MappingTargetKind.LoadProfile) return;
            Set(Model.TargetId, value ?? "", v => Model.TargetId = v);
        }
    }
    public string? TargetLabel { get => Model.TargetLabel; set => Set(Model.TargetLabel, value, v => Model.TargetLabel = v); }
    public bool Invert { get => Model.Invert; set => Set(Model.Invert, value, v => Model.Invert = v); }
    public double? RangeMin { get => Model.RangeMin; set => Set(Model.RangeMin, value, v => Model.RangeMin = v); }
    public double? RangeMax { get => Model.RangeMax; set => Set(Model.RangeMax, value, v => Model.RangeMax = v); }
    public bool Feedback { get => Model.Feedback; set => Set(Model.Feedback, value, v => Model.Feedback = v); }
    public CommandTrigger Trigger { get => Model.Trigger; set => Set(Model.Trigger, value, v => Model.Trigger = v); }
    public bool HandleAsToggle { get => Model.HandleAsToggle; set => Set(Model.HandleAsToggle, value, v => Model.HandleAsToggle = v); }
    public string? CommandParams { get => Model.CommandParams; set => Set(Model.CommandParams, string.IsNullOrWhiteSpace(value) ? null : value, v => Model.CommandParams = v); }

    public string SourceText => IsLearning ? "Move a control…" : Model.HasSource ? Model.SourceText : "(not assigned)";
    public string TargetText => IsProfileSwitch ? "Load profile: " + Model.TargetId
        : IsResetControl && Model.HasTarget ? "Reset: " + (Model.TargetLabel ?? Model.TargetId)
        : Model.HasTarget ? $"{Model.TargetLabel ?? Model.TargetId}" : "(no target)";
    public string KindText => IsProfileSwitch ? "Load profile" : IsResetControl ? "Reset" : Model.TargetKind == MappingTargetKind.Command ? "Command" : "Slider";
    public bool IsControl => Model.TargetKind == MappingTargetKind.DevelopControl;
    public bool IsResetControl => Model.TargetKind == MappingTargetKind.ResetControl;
    public bool IsCommand => Model.TargetKind == MappingTargetKind.Command;
    public bool IsProfileSwitch => Model.TargetKind == MappingTargetKind.LoadProfile;
    public bool IsTriggerTarget => IsCommand || IsProfileSwitch;
    public bool IsTestable => (IsControl || IsResetControl || IsCommand) && Model.HasTarget;
    partial void OnIsLearningChanged(bool value) => OnPropertyChanged(nameof(SourceText));

    public void AssignSource(MidiMessage m)
    {
        Model.Channel = m.Channel;
        Model.MessageType = m.Type;
        Model.Number = m.Type == MidiMessageType.PitchBend ? 0 : m.Number;
        IsLearning = false;
        RaiseAll();
    }

    public void AssignTarget(MappingTargetKind kind, string id, string label, string? commandParams = null)
    {
        Model.TargetKind = kind;
        Model.TargetId = id;
        Model.TargetLabel = label;
        if (kind is MappingTargetKind.Command or MappingTargetKind.ResetControl)
        {
            if (kind == MappingTargetKind.Command) Model.CommandParams = commandParams;
            Model.RangeMin = Model.RangeMax = null;
        }
        RaiseAll();
    }

    private void RaiseAll()
    {
        OnPropertyChanged(string.Empty);
        _changed(this);
    }

    private void Set<T>(T current, T value, Action<T> apply)
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return;
        apply(value);
        RaiseAll();
    }
}
