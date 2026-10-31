using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceChanger.App.Services;
using VoiceChanger.Plugins;

namespace VoiceChanger.App.ViewModels;

/// <summary>A folder plug-ins are looked for in.</summary>
public sealed record PluginFolderRow(string Path, bool IsStandard, IRelayCommand? RemoveCommand)
{
    public bool CanRemove => !IsStandard;

    public string Kind => IsStandard ? "Standard" : "Added";
}

/// <summary>An installed plug-in.</summary>
public sealed record PluginRow(string Name, string Vendor, string Version, string Path, IRelayCommand BlockCommand)
{
    public string Details => string.IsNullOrEmpty(Version) ? Vendor : $"{Vendor} · {Version}";
}

/// <summary>A module that couldn't be scanned.</summary>
public sealed record FailedModuleRow(string Path, string Reason, IAsyncRelayCommand RetryCommand)
{
    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>A plug-in kept out of the menus.</summary>
public sealed record BlockedPluginRow(string Name, string Vendor, string Reason, IRelayCommand UnblockCommand);

/// <summary>
/// The plug-in manager: where plug-ins come from, how they run, which ones are there, which didn't
/// load and which are kept out of the menus.
/// </summary>
public sealed partial class PluginManagerViewModel : ObservableObject
{
    private readonly PluginService _service;

    public PluginManagerViewModel(PluginService service)
    {
        _service = service;
        service.Changed += (_, _) => Refresh();
        Refresh();
    }

    public ObservableCollection<PluginFolderRow> Folders { get; } = [];

    public ObservableCollection<PluginRow> Plugins { get; } = [];

    public ObservableCollection<FailedModuleRow> Failed { get; } = [];

    public ObservableCollection<BlockedPluginRow> Blocked { get; } = [];

    public bool HasFailed => Failed.Count > 0;

    public bool HasBlocked => Blocked.Count > 0;

    public bool IsAvailable => _service.IsAvailable;

    public bool IsScanning => _service.IsScanning;

    /// <summary>Each plug-in in a process of its own: a crash only stops that plug-in.</summary>
    public bool SeparateProcesses
    {
        get => _service.SeparateProcesses;
        set
        {
            _service.SeparateProcesses = value;
            OnPropertyChanged();
        }
    }

    /// <summary>A line about the plug-ins: how many, scanning, or why there are none.</summary>
    [ObservableProperty]
    public partial string Status { get; set; } = "";

    [RelayCommand]
    private Task ScanAsync() => _service.RescanAsync();

    /// <summary>Adds a folder (the view asks which) and scans it.</summary>
    public async Task AddFolderAsync(string folder)
    {
        if (!await _service.AddFolderAsync(folder))
        {
            Status = "That folder is in the list already.";
        }
    }

    private void Refresh()
    {
        var catalog = _service.Catalog;
        Folders.Clear();
        foreach (string folder in PluginCatalog.DefaultFolders)
        {
            Folders.Add(new PluginFolderRow(folder, true, null));
        }

        foreach (string folder in catalog.ExtraFolders)
        {
            Folders.Add(new PluginFolderRow(folder, false, new AsyncRelayCommand(() => _service.RemoveFolderAsync(folder))));
        }

        Plugins.Clear();
        foreach (var plugin in catalog.Plugins)
        {
            Plugins.Add(new PluginRow(plugin.Name, plugin.Vendor, plugin.Version, plugin.Path, new RelayCommand(() => _service.Block(plugin, "Blocked by you."))));
        }

        Failed.Clear();
        foreach (var module in catalog.FailedModules)
        {
            Failed.Add(new FailedModuleRow(module.Path, module.Error ?? "", new AsyncRelayCommand(() => _service.RetryAsync(module.Path))));
        }

        Blocked.Clear();
        foreach (var blocked in catalog.Blocked)
        {
            Blocked.Add(new BlockedPluginRow(blocked.Name, blocked.Vendor, blocked.Reason, new RelayCommand(() => _service.Unblock(blocked.ClassId))));
        }

        Status = !_service.IsAvailable ? $"Plug-ins can't be loaded: {_service.Problem}"
            : _service.IsScanning ? "Looking for plug-ins…"
            : Plugins.Count switch
            {
                0 => "No plug-ins found.",
                1 => "1 plug-in.",
                int count => $"{count} plug-ins.",
            };
        OnPropertyChanged(nameof(HasFailed));
        OnPropertyChanged(nameof(HasBlocked));
        OnPropertyChanged(nameof(IsScanning));
    }
}
