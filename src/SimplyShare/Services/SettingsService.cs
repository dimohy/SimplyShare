using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using SimplyShare.Core;
using SimplyShare.Models;

namespace SimplyShare.Services;

/// <summary>
/// JSON 파일 기반 설정 관리 서비스
/// </summary>
public sealed class SettingsService : ISettingsService
{
    private const string StartupRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupValueName = "SimplyShare";
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
        var temporaryPath = $"{SettingsPath}.tmp";

        try
        {
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    Settings,
                    AppJsonContext.Default.AppSettings,
                    cancellationToken);
            }

            ApplyStartupRegistration(Settings.RunAtStartup);
            File.Move(temporaryPath, SettingsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void ApplyStartupRegistration(bool enabled)
    {
        using var startupKey = Registry.CurrentUser.CreateSubKey(StartupRegistryPath, writable: true)
            ?? throw new InvalidOperationException("Windows 시작프로그램 레지스트리를 열 수 없습니다.");

        if (!enabled)
        {
            startupKey.DeleteValue(StartupValueName, throwOnMissingValue: false);
            return;
        }

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new InvalidOperationException("현재 실행 파일 경로를 확인할 수 없습니다.");
        }

        startupKey.SetValue(StartupValueName, $"\"{executablePath}\"", RegistryValueKind.String);
    }
}
