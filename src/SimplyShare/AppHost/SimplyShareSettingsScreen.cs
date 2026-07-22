using Duxel.Core;

namespace SimplyShare.AppHost;

internal sealed class SimplyShareSettingsScreen : UiScreen
{
    private const float Margin = 20f;
    private const float HeaderHeight = 38f;
    private const float FooterHeight = 42f;
    private const float SectionBottomGap = 6f;
    private const float LabelToInputGap = 2f;
    private const float SaveButtonWidth = 96f;

    private readonly SettingsDraft _draft;
    private readonly Func<SettingsDraft, string?> _save;
    private string? _statusMessage;
    private bool _statusIsError;

    public SimplyShareSettingsScreen(SettingsDraft draft, Func<SettingsDraft, string?> save)
    {
        _draft = draft;
        _save = save;
    }

    public override void Render(UiImmediateContext ui)
    {
        ui.EnableRootViewportContentLayout(contentPadding: 0f);
        var canvas = ui.BeginWindowCanvas(SimplyShareTheme.WindowBackground(ui));
        var contentWidth = MathF.Max(1f, canvas.Width - (Margin * 2f));
        var headerRect = new UiRect(canvas.X + Margin, canvas.Y + Margin, contentWidth, HeaderHeight);
        var footerRect = new UiRect(
            canvas.X + Margin,
            canvas.Y + canvas.Height - Margin - FooterHeight,
            contentWidth,
            FooterHeight);
        var formRect = new UiRect(
            canvas.X + Margin,
            headerRect.Y + headerRect.Height,
            contentWidth,
            MathF.Max(1f, footerRect.Y - (headerRect.Y + headerRect.Height)));

        ui.PushDirectTextFontPaths(SimplyShareTheme.TitlePrimaryFontPath, SimplyShareTheme.TitleSecondaryFontPath);
        ui.DrawTextAligned(headerRect, "설정", SimplyShareTheme.TextPrimary(ui), UiItemHorizontalAlign.Left, UiItemVerticalAlign.Top, 18f);
        ui.PopDirectTextFontPaths();
        ui.SetCursorScreenPos(new UiVector2(formRect.X, formRect.Y));

        RenderFieldLabel(ui, "닉네임");
        ui.SetNextItemWidth(formRect.Width);
        ui.InputText("##settings-nickname", ref _draft.Nickname, 128);
        EndFieldGroup(ui);

        RenderFieldLabel(ui, "수신 파일 저장 경로");
        ui.SetNextItemWidth(formRect.Width);
        ui.InputText("##settings-download-path", ref _draft.DownloadPath, 512);
        EndFieldGroup(ui);

        RenderFieldLabel(ui, "네트워크 대역 (줄 단위, 예: 192.168.100.*)");
        ui.SetNextItemWidth(formRect.Width);
        ui.InputTextMultiline("##settings-network-ranges", ref _draft.NetworkRangesText, 512, 60f);
        EndFieldGroup(ui);

        RenderPortFields(ui);
        EndFieldGroup(ui);

        ui.Checkbox("Windows 시작 시 자동 실행", ref _draft.RunAtStartup);

        if (!string.IsNullOrWhiteSpace(_statusMessage))
        {
            ui.DrawTextAligned(
                new UiRect(footerRect.X, footerRect.Y + 10f, MathF.Max(1f, footerRect.Width - SaveButtonWidth - 12f), 18f),
                _statusMessage,
                _statusIsError ? new UiColor(190, 35, 35) : SimplyShareTheme.TextSecondary(ui),
                fontSize: 10f);
        }
        ui.SetCursorScreenPos(new UiVector2(
            footerRect.X + footerRect.Width - SaveButtonWidth,
            footerRect.Y + 4f));
        if (ui.Button("저장", new UiVector2(SaveButtonWidth, 34f)))
        {
            var error = _save(_draft);
            if (string.IsNullOrWhiteSpace(error))
            {
                _statusIsError = false;
                _statusMessage = "설정이 저장되었습니다.";
            }
            else
            {
                _statusIsError = true;
                _statusMessage = error;
            }
        }

        ui.EndWindowCanvas();
    }

    private void RenderPortFields(UiImmediateContext ui)
    {
        var rowY = ui.GetCursorPosY();
        var frameHeight = ui.GetFrameHeight();
        var textHeight = ui.GetTextLineHeight();
        ui.SetCursorPosY(rowY + ((frameHeight - textHeight) * 0.5f));
        ui.Text("Discovery 포트:");
        ui.SameLine(8f);
        ui.SetCursorPosY(rowY);
        ui.SetNextItemWidth(76f);
        ui.InputInt("##settings-discovery-port", ref _draft.DiscoveryPort);

        ui.SameLine(14f);
        ui.SetCursorPosY(rowY + ((frameHeight - textHeight) * 0.5f));
        ui.Text("Transfer 포트:");
        ui.SameLine(8f);
        ui.SetCursorPosY(rowY);
        ui.SetNextItemWidth(76f);
        ui.InputInt("##settings-transfer-port", ref _draft.TransferPort);
    }

    private static void RenderFieldLabel(UiImmediateContext ui, string text)
    {
        ui.PushDirectTextFontPaths(SimplyShareTheme.SemiBoldPrimaryFontPath, SimplyShareTheme.SemiBoldSecondaryFontPath);
        ui.PushStyleVarY(UiStyleVar.ItemSpacing, LabelToInputGap);
        ui.Text(text);
        ui.PopDirectTextFontPaths();
    }

    private static void EndFieldGroup(UiImmediateContext ui)
    {
        ui.PopStyleVar();
        ui.Dummy(new UiVector2(0f, SectionBottomGap));
    }
}
