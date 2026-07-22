using System.Reflection;
using Duxel.App;
using Duxel.Core;
using Duxel.Windows.App;
using Microsoft.Extensions.DependencyInjection;
using SimplyShare.AppHost;
using SimplyShare.Core.Clipboard;
using SimplyShare.Core.Discovery;
using SimplyShare.Services;

var appIconData = LoadEmbeddedIcon("app.ico");

var services = new ServiceCollection();
ConfigureServices(services);

using var serviceProvider = services.BuildServiceProvider();
var controller = serviceProvider.GetRequiredService<SimplyShareController>();
controller.IconData = appIconData;

await controller.InitializeAsync();

try
{
    if (!await controller.ShowInitialSetupAsync())
    {
        return;
    }

    DuxelWindowsApp.Run(new DuxelAppOptions
    {
        Window = new DuxelWindowOptions
        {
            Title = "SimplyShare",
            Width = 340,
            Height = 480,
            MinWidth = 340,
            MinHeight = 480,
            VSync = true,
            Resizable = false,
            ShowMinimizeButton = true,
            ShowMaximizeButton = false,
            CenterOnScreen = true,
            IconData = appIconData,
            WindowCreated = controller.AttachMainWindowHandle,
            Tray = new DuxelTrayOptions
            {
                Enabled = true,
                ToolTip = "SimplyShare",
                IconData = appIconData,
                HideWindowOnMinimize = true,
                HideWindowOnClose = true,
                DoubleClick = controller.RestoreMainWindow,
                MenuItems =
                [
                    new DuxelTrayMenuItem { Text = "열기", Invoked = controller.RestoreMainWindow },
                    new DuxelTrayMenuItem
                    {
                        Text = "설정",
                        Invoked = () =>
                        {
                            controller.RestoreMainWindow();
                            controller.OpenSettingsWindow();
                        }
                    },
                    new DuxelTrayMenuItem { IsSeparator = true },
                    new DuxelTrayMenuItem { Text = "종료", Invoked = controller.ExitApplication },
                ],
            },
        },
        Renderer = new DuxelRendererOptions
        {
            Profile = DuxelPerformanceProfile.Display,
            MsaaSamples = 0,
            FontLinearSampling = false,
        },
        Font = new DuxelFontOptions
        {
            FontSize = 16,
            FastStartup = true,
            StartupGlyphs = SimplyShareGlyphCatalog.All,
        },
        Frame = new DuxelFrameOptions
        {
            EnableIdleFrameSkip = true,
            LineHeightScale = 1.2f,
        },
        Screen = new SimplyShareScreen(controller),
    });
}
finally
{
    await controller.ShutdownAsync();
}

static void ConfigureServices(IServiceCollection services)
{
    services.AddSingleton<ISettingsService, SettingsService>();
    services.AddSingleton<IDiscoveryService, DiscoveryService>();
    services.AddSingleton<ITransferService, TransferService>();
    services.AddSingleton<IClipboardService, Win32ClipboardService>();
    services.AddSingleton<SimplyShareController>();
}

static byte[] LoadEmbeddedIcon(string name)
{
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
        ?? throw new InvalidOperationException($"Embedded resource '{name}' not found.");
    var data = new byte[stream.Length];
    stream.ReadExactly(data);
    return data;
}
