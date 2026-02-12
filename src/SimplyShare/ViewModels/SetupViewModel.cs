using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimplyShare.Services;

namespace SimplyShare.ViewModels;

/// <summary>
/// 최초 실행 닉네임 설정 ViewModel
/// </summary>
public sealed partial class SetupViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;

    public SetupViewModel(ISettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CompleteSetupCommand))]
    private string _nickname = string.Empty;

    [ObservableProperty]
    private string _networkRange = string.Empty;

    /// <summary>설정 완료 이벤트</summary>
    public event Action? SetupCompleted;

    [RelayCommand(CanExecute = nameof(CanCompleteSetup))]
    private async Task CompleteSetupAsync(CancellationToken cancellationToken)
    {
        var settings = _settingsService.Settings;
        settings.Nickname = Nickname.Trim();
        settings.IsSetupCompleted = true;

        if (NetworkRange is { Length: > 0 })
        {
            settings.NetworkRanges = [.. NetworkRange
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        }

        await _settingsService.SaveAsync(cancellationToken);
        SetupCompleted?.Invoke();
    }

    private bool CanCompleteSetup() => Nickname.Trim().Length > 0;
}
