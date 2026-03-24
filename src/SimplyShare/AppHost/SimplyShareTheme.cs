using Duxel.Core;

namespace SimplyShare.AppHost;

internal static class SimplyShareTheme
{
    private static readonly string FontsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

    public static UiTheme Light => UiTheme.ImGuiLight with
    {
        WindowBg = new UiColor(255, 255, 255),
        PopupBg = new UiColor(248, 248, 248),
        Border = new UiColor(224, 224, 224),
        FrameBg = new UiColor(250, 250, 250),
        FrameBgHovered = new UiColor(242, 242, 242),
        FrameBgActive = new UiColor(235, 235, 235),
        Header = new UiColor(255, 255, 255),
        HeaderHovered = new UiColor(246, 248, 251),
        HeaderActive = new UiColor(236, 240, 245),
        Button = new UiColor(248, 248, 248),
        ButtonHovered = new UiColor(240, 240, 240),
        ButtonActive = new UiColor(232, 232, 232),
        Separator = new UiColor(224, 224, 224),
        ScrollbarBg = new UiColor(0, 0, 0, 28),
        ScrollbarGrab = new UiColor(180, 180, 180),
        ScrollbarGrabHovered = new UiColor(160, 160, 160),
        ScrollbarGrabActive = new UiColor(140, 140, 140),
    };

    public static UiColor TextPrimary => new(0, 0, 0);
    public static UiColor TextSecondary => new(128, 128, 128);
    public static UiColor Border => new(224, 224, 224);
    public static UiColor BubbleSent => new(220, 248, 198);
    public static UiColor BubbleReceived => new(240, 240, 240);
    public static UiColor StatusOnline => new(33, 180, 78);
    public static UiColor StatusOffline => new(171, 171, 171);
    public static UiColor Accent => new(0, 122, 204);

    public static string TitlePrimaryFontPath => ResolveFontPath("segoeuib.ttf", "seguisb.ttf", "segoeui.ttf");
    public static string TitleSecondaryFontPath => ResolveFontPath("malgunbd.ttf", "malgun.ttf");
    public static string SemiBoldPrimaryFontPath => ResolveFontPath("seguisb.ttf", "segoeuib.ttf", "segoeui.ttf");
    public static string SemiBoldSecondaryFontPath => ResolveFontPath("malgunbd.ttf", "malgun.ttf");

    private static string ResolveFontPath(params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            var path = Path.Combine(FontsDirectory, candidate);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return Path.Combine(FontsDirectory, candidates[^1]);
    }
}