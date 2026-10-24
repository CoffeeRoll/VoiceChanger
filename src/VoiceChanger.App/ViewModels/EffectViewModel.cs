using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceChanger.Core.Effects;

namespace VoiceChanger.App.ViewModels;

/// <summary>One effect card in the chain.</summary>
public abstract partial class EffectViewModel : ObservableObject
{
    private bool _applyingPreset;

    protected EffectViewModel(AudioEffect effect, string description)
    {
        Effect = effect;
        Description = description;
        Parameters = effect.Parameters.Select(p => new ParameterViewModel(p)).ToList();
        foreach (var parameter in Parameters)
        {
            parameter.Changed += OnParameterChanged;
        }

        _isEnabled = effect.IsEnabled;
        SelectedPreset = FindMatchingPreset();
    }

    public AudioEffect Effect { get; }

    public string Name => Effect.Name;

    public string Description { get; }

    public IReadOnlyList<ParameterViewModel> Parameters { get; }

    public IReadOnlyList<EffectPreset> Presets => Effect.Presets;

    public bool HasPresets => Presets.Count > 0;

    /// <summary>Position in the chain, starting at 1.</summary>
    [ObservableProperty]
    public partial int Position { get; set; }

    [ObservableProperty]
    public partial bool CanMoveUp { get; set; }

    [ObservableProperty]
    public partial bool CanMoveDown { get; set; }

    /// <summary>The preset that is currently applied, or <c>null</c> once a value was changed by hand.</summary>
    [ObservableProperty]
    public partial EffectPreset? SelectedPreset { get; set; }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (SetProperty(ref _isEnabled, value))
            {
                Effect.IsEnabled = value;
            }
        }
    }

    private bool _isEnabled;

    // Chain operations are handled by the owner; set when the card is added.
    public IRelayCommand? RemoveCommand { get; set; }

    public IRelayCommand? MoveUpCommand { get; set; }

    public IRelayCommand? MoveDownCommand { get; set; }

    public static EffectViewModel Create(AudioEffect effect, string description) => effect switch
    {
        EqualizerEffect equalizer => new EqualizerViewModel(equalizer, description),
        _ => new GenericEffectViewModel(effect, description),
    };

    [RelayCommand]
    private void ResetParameters()
    {
        _applyingPreset = true;
        try
        {
            Effect.ResetParameters();
        }
        finally
        {
            _applyingPreset = false;
        }

        SelectedPreset = FindMatchingPreset();
    }

    /// <summary>The preset whose values equal the current settings, if any.</summary>
    private EffectPreset? FindMatchingPreset() =>
        Presets.FirstOrDefault(preset => Effect.Parameters.All(parameter =>
            parameter.Value.Equals(parameter.Coerce(
                preset.Values.TryGetValue(parameter.Id, out float value) ? value : parameter.DefaultValue))));

    partial void OnSelectedPresetChanged(EffectPreset? value)
    {
        if (value is null)
        {
            return;
        }

        _applyingPreset = true;
        try
        {
            Effect.ApplyPreset(value);
        }
        finally
        {
            _applyingPreset = false;
        }
    }

    protected virtual void OnParameterChanged(object? sender, EventArgs e)
    {
        if (!_applyingPreset)
        {
            SelectedPreset = null;
        }
    }
}

/// <summary>Effects without a dedicated editor: every parameter is shown as a row.</summary>
public sealed class GenericEffectViewModel(AudioEffect effect, string description) : EffectViewModel(effect, description);
