using Duxel.Core;

namespace SimplyShare.AppHost;

internal sealed class SimplyShareTextViewerScreen(string text, Action close) : UiScreen
{
    private const float Margin = 12f;
    private const float FooterHeight = 52f;

    public override void Render(UiImmediateContext ui)
    {
        ui.EnableRootViewportContentLayout(contentPadding: 0f);
        var canvas = ui.BeginWindowCanvas(SimplyShareTheme.WindowBackground(ui));
        var draw = ui.GetWindowDrawList();
        var contentWidth = MathF.Max(1f, canvas.Width - (Margin * 2f));
        var footerRect = new UiRect(
            canvas.X + Margin,
            canvas.Y + canvas.Height - Margin - FooterHeight,
            contentWidth,
            FooterHeight);
        var textRect = new UiRect(
            canvas.X + Margin,
            canvas.Y + Margin,
            contentWidth,
            MathF.Max(1f, footerRect.Y - canvas.Y - (Margin * 2f)));

        ui.SetCursorScreenPos(new UiVector2(textRect.X, textRect.Y));
        _ = ui.BeginChild("text-viewer-content", new UiVector2(textRect.Width, textRect.Height), border: true);
        ui.TextWrapped(text);
        ui.EndChild();

        draw.AddRectFilled(
            new UiRect(footerRect.X, footerRect.Y, footerRect.Width, 1f),
            SimplyShareTheme.Border(ui),
            ui.WhiteTextureId,
            footerRect);

        const float buttonWidth = 88f;
        const float buttonGap = 8f;
        ui.SetCursorScreenPos(new UiVector2(
            footerRect.X + footerRect.Width - (buttonWidth * 2f) - buttonGap,
            footerRect.Y + 10f));
        if (ui.Button("복사", new UiVector2(buttonWidth, 32f)))
        {
            ui.SetClipboardText(text);
        }

        ui.SameLine(buttonGap);
        if (ui.Button("닫기", new UiVector2(buttonWidth, 32f)))
        {
            close();
        }

        ui.EndWindowCanvas();
    }
}
