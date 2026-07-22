using Duxel.Core;

namespace SimplyShare.AppHost;

internal static class SimplyShareTheme
{
    private static readonly string FontsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

    public static UiColor WindowBackground(UiImmediateContext ui) => ui.GetColorU32(UiStyleColor.WindowBg);
    public static UiColor Surface(UiImmediateContext ui) => ui.GetColorU32(UiStyleColor.Header);
    public static UiColor TextPrimary(UiImmediateContext ui) => ui.GetColorU32(UiStyleColor.Text);
    public static UiColor TextSecondary(UiImmediateContext ui) => ui.GetColorU32(UiStyleColor.TextDisabled);
    public static UiColor Border(UiImmediateContext ui) => ui.GetColorU32(UiStyleColor.Separator);
    public static UiColor Selection(UiImmediateContext ui) => ui.GetColorU32(UiStyleColor.HeaderActive);
    public static UiColor SelectionHovered(UiImmediateContext ui) => ui.GetColorU32(UiStyleColor.HeaderHovered);
    public static UiColor BubbleSent(UiImmediateContext ui) => ui.GetColorU32(UiStyleColor.HeaderActive);
    public static UiColor BubbleReceived(UiImmediateContext ui) => ui.GetColorU32(UiStyleColor.FrameBg);
    public static UiColor StatusOnline => new(33, 180, 78);
    public static UiColor StatusOffline => new(171, 171, 171);

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
