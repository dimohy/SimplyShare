using Duxel.Core;

namespace SimplyShare.AppHost;

internal sealed class SimplyShareSettingsScreen : UiScreen
{
    private const float SectionBottomGap = 8f;
    private const float LabelToInputGap = 1f;
    private const float FooterSpacing = 8f;
    private const float SaveButtonWidth = 96f;

    private readonly SettingsDraft _draft;
    private readonly Func<SettingsDraft, string?> _save;
    private readonly Action _close;
    private string? _statusMessage;

    public SimplyShareSettingsScreen(SettingsDraft draft, Func<SettingsDraft, string?> save, Action close)
    {
        _draft = draft;
        _save = save;
        _close = close;
    }

    public override void Render(UiImmediateContext ui)
    {
        ui.EnableRootViewportContentLayout(contentPadding: 8f);

        ui.PushDirectTextFontPaths(SimplyShareTheme.TitlePrimaryFontPath, SimplyShareTheme.TitleSecondaryFontPath);
        ui.PushFontSize(18f);
        ui.Text("설정");
        ui.PopFontSize();
        ui.PopDirectTextFontPaths();

        ui.Dummy(new UiVector2(0f, 4f));
        ui.Separator();
        ui.Dummy(new UiVector2(0f, 8f));

        RenderFieldLabel(ui, "닉네임");
        ui.SetNextItemWidth(ui.GetContentRegionAvail().X);
        ui.InputText("##settings-nickname", ref _draft.Nickname, 128);
        EndFieldGroup(ui);

        RenderFieldLabel(ui, "수신 파일 저장 경로");
        ui.SetNextItemWidth(ui.GetContentRegionAvail().X);
        ui.InputText("##settings-download-path", ref _draft.DownloadPath, 512);
        EndFieldGroup(ui);

        RenderFieldLabel(ui, "네트워크 대역 (줄 단위, 예: 192.168.100.*)");
        ui.SetNextItemWidth(ui.GetContentRegionAvail().X);
        var networkRangesHeight = ui.GetTextLineHeight() * 4f + 8f;
        ui.InputTextMultiline("##settings-network-ranges", ref _draft.NetworkRangesText, 512, networkRangesHeight);
        EndFieldGroup(ui);

        RenderFieldLabel(ui, "포트 설정");

        var portAvail = ui.GetContentRegionAvail();
        var frameH = ui.GetFrameHeight();
        var textH = ui.GetTextLineHeight();
        var portRowY = ui.GetCursorPosY();
        var textCenterY = portRowY + (frameH - textH) * 0.5f;

        ui.SetCursorPosY(textCenterY);
        ui.Text("Discovery 포트:");
        ui.SameLine(8f);
        ui.SetCursorPosY(portRowY);
        ui.SetNextItemWidth(60f);
        ui.InputInt("##settings-discovery-port", ref _draft.DiscoveryPort);

        ui.SameLine(16f);
        ui.SetCursorPosY(textCenterY);
        ui.Text("Transfer 포트:");
        ui.SameLine(8f);
        ui.SetCursorPosY(portRowY);
        ui.SetNextItemWidth(60f);
        ui.InputInt("##settings-transfer-port", ref _draft.TransferPort);
        EndFieldGroup(ui);

        ui.Checkbox("Windows 시작 시 자동 실행", ref _draft.RunAtStartup);

        var avail = ui.GetContentRegionAvail();
        var itemSpacingY = ui.GetFrameHeightWithSpacing() - ui.GetFrameHeight();
        var footerItemsH = 4f * itemSpacingY + FooterSpacing + ui.GetFrameHeight();
        var bottomReserve = 8f;
        var gap = MathF.Max(SectionBottomGap, avail.Y - footerItemsH - bottomReserve);
        ui.Dummy(new UiVector2(0f, gap));

        ui.Separator();
        ui.Dummy(new UiVector2(0f, FooterSpacing));

        if (!string.IsNullOrWhiteSpace(_statusMessage))
        {
            ui.TextWrapped(_statusMessage);
            ui.Dummy(new UiVector2(0f, 8f));
        }

        var saveButtonWidth = SaveButtonWidth;
        var buttonX = ui.GetContentRegionMax().X - saveButtonWidth;
        ui.SetCursorPosX(buttonX);
        if (ui.Button("저장", new UiVector2(saveButtonWidth, 0f)))
        {
            var error = _save(_draft);
            if (string.IsNullOrWhiteSpace(error))
            {
                _close();
            }
            else
            {
                _statusMessage = error;
            }
        }
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
