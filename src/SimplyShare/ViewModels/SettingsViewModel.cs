using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimplyShare.Models;
using SimplyShare.Services;

namespace SimplyShare.ViewModels;

/// <summary>
/// 설정 화면 ViewModel
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;

    public SettingsViewModel(ISettingsService settingsService)
    {
        _settingsService = settingsService;
        LoadFromSettings();
    }

    [ObservableProperty]
    private string _nickname = string.Empty;

    [ObservableProperty]
    private string _downloadPath = string.Empty;

    [ObservableProperty]
    private bool _runAtStartup;

    [ObservableProperty]
    private int _discoveryPort = NetworkDefaults.DiscoveryPort;

    [ObservableProperty]
    private int _transferPort = NetworkDefaults.TransferPort;

    [ObservableProperty]
    private string _networkRangesText = string.Empty;

    [RelayCommand]
    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        var settings = _settingsService.Settings;
        settings.Nickname = Nickname;
        settings.DownloadPath = DownloadPath;
        settings.RunAtStartup = RunAtStartup;
        settings.DiscoveryPort = DiscoveryPort;
        settings.TransferPort = TransferPort;
        settings.NetworkRanges = [.. NetworkRangesText
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

        await _settingsService.SaveAsync(cancellationToken);
    }

    private void LoadFromSettings()
    {
        var settings = _settingsService.Settings;
        Nickname = settings.Nickname;
        DownloadPath = settings.DownloadPath;
        RunAtStartup = settings.RunAtStartup;
        DiscoveryPort = settings.DiscoveryPort;
        TransferPort = settings.TransferPort;
        NetworkRangesText = string.Join('\n', settings.NetworkRanges);
    }
}
