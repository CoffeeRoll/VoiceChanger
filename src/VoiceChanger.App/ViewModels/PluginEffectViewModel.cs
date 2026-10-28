using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceChanger.App.Services;
using VoiceChanger.Plugins;

namespace VoiceChanger.App.ViewModels;

/// <summary>A VST3 plug-in: its parameters, its own window, and what is wrong with it, if anything.</summary>
public sealed partial class PluginEffectViewModel : EffectViewModel
{
    private readonly PluginEffect _plugin;
    private string? _shownProblem;

    public PluginEffectViewModel(PluginEffect plugin, string description)
        : base(plugin, description)
    {
        _plugin = plugin;
        _shownProblem = plugin.Problem;
        plugin.StatusChanged += (_, _) => RefreshStatus();
    }

    public bool HasWindow => _plugin.HasEditor;

    public bool IsWindowOpen => _plugin.IsEditorOpen;

    public string WindowButtonText => IsWindowOpen ? "Close plug-in window" : "Open plug-in window";

    /// <summary>Why the plug-in isn't working, or why its window didn't open.</summary>
    public string? Problem => WindowProblem ?? _plugin.Problem;

    public bool HasProblem => Problem is not null;

    public string HostingText => _plugin.RunsInSeparateProcess
        ? "Runs in a process of its own"
        : "Runs inside VoiceChanger";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Problem))]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial string? WindowProblem { get; set; }

    public override void Tick(bool isRunning)
    {
        // The plug-in may have found a problem while being set up for a new stream.
        if (_plugin.Problem != _shownProblem)
        {
            RefreshStatus();
        }
    }

    [RelayCommand]
    private void ToggleWindow()
    {
        if (_plugin.IsEditorOpen)
        {
            _plugin.CloseEditor();
            WindowProblem = null;
        }
        else
        {
            WindowProblem = _plugin.OpenEditor(PluginWindows.Owner());
        }

        RefreshStatus();
    }

    private void RefreshStatus()
    {
        _shownProblem = _plugin.Problem;
        OnPropertyChanged(nameof(HasWindow));
        OnPropertyChanged(nameof(IsWindowOpen));
        OnPropertyChanged(nameof(WindowButtonText));
        OnPropertyChanged(nameof(Problem));
        OnPropertyChanged(nameof(HasProblem));
        OnPropertyChanged(nameof(HostingText));
    }
}
