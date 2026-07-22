using Duxel.Core;

namespace SimplyShare.AppHost;

internal sealed class SimplyShareSetupScreen(
    SetupDraft draft,
    Func<string?> save,
    Action close) : UiScreen
{
    private string? _error;

    public override void Render(UiImmediateContext ui)
    {
        ui.EnableRootViewportContentLayout(contentPadding: 0f);
        var canvas = ui.BeginWindowCanvas(SimplyShareTheme.WindowBackground(ui));
        const float margin = 24f;
        var width = MathF.Max(1f, canvas.Width - (margin * 2f));

        ui.PushDirectTextFontPaths(SimplyShareTheme.TitlePrimaryFontPath, SimplyShareTheme.TitleSecondaryFontPath);
        ui.DrawTextAligned(
            new UiRect(canvas.X + margin, canvas.Y + margin, width, 28f),
            "SimplyShare에 오신 것을 환영합니다!",
            SimplyShareTheme.TextPrimary(ui),
            fontSize: 18f);
        ui.PopDirectTextFontPaths();

        var y = canvas.Y + margin + 48f;
        RenderLabel(ui, canvas.X + margin, y, "닉네임 (필수)");
        y += 26f;
        ui.SetCursorScreenPos(new UiVector2(canvas.X + margin, y));
        ui.SetNextItemWidth(width);
        ui.InputText("##setup-nickname", ref draft.Nickname, 20);
        ui.SetItemDefaultFocus();

        y += 62f;
        RenderLabel(ui, canvas.X + margin, y, "네트워크 대역 (선택, 예: 192.168.100.*)");
        y += 26f;
        ui.SetCursorScreenPos(new UiVector2(canvas.X + margin, y));
        ui.SetNextItemWidth(width);
        ui.InputTextMultiline("##setup-network-ranges", ref draft.NetworkRangesText, 512, 60f);

        if (!string.IsNullOrWhiteSpace(_error))
        {
            ui.SetCursorScreenPos(new UiVector2(canvas.X + margin, y + 68f));
            ui.TextColored(new UiColor(190, 35, 35), _error);
        }

        const float buttonWidth = 112f;
        ui.SetCursorScreenPos(new UiVector2(
            canvas.X + canvas.Width - margin - buttonWidth,
            canvas.Y + canvas.Height - margin - 36f));
        ui.BeginDisabled(string.IsNullOrWhiteSpace(draft.Nickname));
        if (ui.Button("시작하기", new UiVector2(buttonWidth, 36f)))
        {
            _error = save();
            if (string.IsNullOrWhiteSpace(_error))
            {
                close();
            }
        }
        ui.EndDisabled();
        ui.EndWindowCanvas();
    }

    private static void RenderLabel(UiImmediateContext ui, float x, float y, string text)
    {
        ui.SetCursorScreenPos(new UiVector2(x, y));
        ui.PushDirectTextFontPaths(SimplyShareTheme.SemiBoldPrimaryFontPath, SimplyShareTheme.SemiBoldSecondaryFontPath);
        ui.Text(text);
        ui.PopDirectTextFontPaths();
    }
}
