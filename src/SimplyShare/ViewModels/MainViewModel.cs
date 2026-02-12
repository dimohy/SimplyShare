using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SimplyShare.Models;
using SimplyShare.Services;

namespace SimplyShare.ViewModels;

/// <summary>
/// 메인 화면 ViewModel — 장치 목록 관리
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly IDiscoveryService _discoveryService;

    public MainViewModel(IDiscoveryService discoveryService)
    {
        _discoveryService = discoveryService;
        _discoveryService.DevicesChanged += HandleDevicesChanged;
    }

    /// <summary>발견된 장치 목록</summary>
    public ObservableCollection<DeviceInfo> Devices { get; } = [];

    /// <summary>장치 없음 여부</summary>
    [ObservableProperty]
    private bool _hasNoDevices = true;

    [ObservableProperty]
    private string _statusMessage = "같은 네트워크의 장치를 검색 중...";

    private void HandleDevicesChanged(IReadOnlyList<DeviceInfo> devices)
    {
        _ = App.Current.Dispatcher.InvokeAsync(() =>
        {
            Devices.Clear();
            foreach (var device in devices)
                Devices.Add(device);

            HasNoDevices = Devices.Count is 0;
            StatusMessage = Devices.Count is 0
                ? "같은 네트워크의 장치를 검색 중..."
                : $"온라인 장치 {Devices.Count}대";
        });
    }
}
