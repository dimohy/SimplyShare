using System.IO;
using System.Text.Json;
using SimplyShare.Core;
using SimplyShare.Models;

namespace SimplyShare.Services;

/// <summary>
/// JSON 파일 기반 설정 관리 서비스
/// </summary>
public sealed class SettingsService : ISettingsService
{
    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SimplyShare");

    private static readonly string SettingsPath =
        Path.Combine(SettingsDir, "settings.json");

    public AppSettings Settings { get; private set; } = new();

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(SettingsPath))
            return;

        await using var stream = File.OpenRead(SettingsPath);
        var loaded = await JsonSerializer.DeserializeAsync(
            stream,
            AppJsonContext.Default.AppSettings,
            cancellationToken);

        if (loaded is not null)
            Settings = loaded;
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(SettingsDir);

        await using var stream = File.Create(SettingsPath);
        await JsonSerializer.SerializeAsync(
            stream,
            Settings,
            AppJsonContext.Default.AppSettings,
            cancellationToken);
    }
}
