using Duxel.Core;
using SimplyShare.Models;

namespace SimplyShare.AppHost;

internal sealed class SimplyShareScreen(SimplyShareController controller) : UiScreen
{
    private const float Margin = 12f;
    private const float HeaderHeight = 60f;
    private const float FooterHeight = 26f;
    private const float SectionGap = 8f;
    private const float HeaderTitleVersionGap = 8f;
    private string? _lastClickedDeviceId;
    private long _lastClickedTicks;

    public override void Render(UiImmediateContext ui)
    {
        controller.DrainUiActions();
        ui.EnableRootViewportContentLayout(contentPadding: 0f);

        if (controller.IsSetupRequired)
        {
            RenderSetupView(ui);
        }
        else
        {
            RenderMainView(ui);
        }
    }

    private void RenderMainView(UiImmediateContext ui)
    {
        var canvas = ui.BeginWindowCanvas(SimplyShareTheme.WindowBackground(ui));
        var draw = ui.GetWindowDrawList();
        var contentRect = new UiRect(
            canvas.X + Margin,
            canvas.Y + Margin,
            MathF.Max(1f, canvas.Width - (Margin * 2f)),
            MathF.Max(1f, canvas.Height - (Margin * 2f)));
        var headerRect = new UiRect(contentRect.X, contentRect.Y, contentRect.Width, HeaderHeight);
        var footerRect = new UiRect(
            contentRect.X,
            contentRect.Y + contentRect.Height - FooterHeight,
            contentRect.Width,
            FooterHeight);
        var deviceRect = new UiRect(
            contentRect.X,
            headerRect.Y + headerRect.Height + 10f,
            contentRect.Width,
            MathF.Max(48f, footerRect.Y - SectionGap - (headerRect.Y + headerRect.Height + 10f)));

        RenderHeader(ui, draw, headerRect);
        RenderDevicesSection(ui, deviceRect);

        DrawHorizontalLine(draw, footerRect.Y, footerRect.X, footerRect.Width, SimplyShareTheme.Border(ui), ui.WhiteTextureId);
        ui.DrawTextAligned(
            new UiRect(footerRect.X, footerRect.Y + 7f, footerRect.Width, 12f),
            controller.StatusMessage,
            SimplyShareTheme.TextSecondary(ui),
            UiItemHorizontalAlign.Left,
            UiItemVerticalAlign.Top,
            10f);

        ui.EndWindowCanvas();
    }

    private void RenderHeader(UiImmediateContext ui, UiDrawListBuilder draw, UiRect headerRect)
    {
        var gearRect = new UiRect(headerRect.X + headerRect.Width - 30f, headerRect.Y + 2f, 28f, 28f);
        var titleText = "SimplyShare";
        ui.PushDirectTextFontPaths(SimplyShareTheme.TitlePrimaryFontPath, SimplyShareTheme.TitleSecondaryFontPath);
        var titleSize = ui.CalcTextSize(titleText, 20f);
        ui.DrawTextAligned(new UiRect(headerRect.X, headerRect.Y + 1f, titleSize.X, 25f), titleText, SimplyShareTheme.TextPrimary(ui), fontSize: 20f);
        ui.PopDirectTextFontPaths();

        var versionX = headerRect.X + titleSize.X + HeaderTitleVersionGap;
        ui.DrawTextAligned(
            new UiRect(versionX, headerRect.Y + 7f, 72f, 14f),
            $"v{SimplyShare.Core.AppVersion.CurrentString}",
            SimplyShareTheme.TextSecondary(ui),
            fontSize: 11f);
        ui.DrawTextAligned(new UiRect(headerRect.X, headerRect.Y + 29f, 260f, 14f), $"내 닉네임: {controller.Settings.Nickname}", SimplyShareTheme.TextSecondary(ui), fontSize: 11f);
        ui.DrawTextAligned(new UiRect(headerRect.X, headerRect.Y + 45f, 260f, 14f), $"내 IP: {controller.LocalIpAddress}", SimplyShareTheme.TextSecondary(ui), fontSize: 11f);

        ui.SetCursorScreenPos(new UiVector2(gearRect.X, gearRect.Y));
        var settingsPressed = ui.InvisibleButton("open-settings", new UiVector2(gearRect.Width, gearRect.Height));
        var settingsHovered = ui.IsItemHovered();
        var settingsActive = ui.IsItemActive();
        if (settingsPressed)
        {
            controller.OpenSettingsWindow();
        }

        DrawSettingsButton(ui, draw, gearRect, ui.WhiteTextureId, settingsHovered, settingsActive);
    }

    private void RenderSetupView(UiImmediateContext ui)
    {
        var canvas = ui.BeginWindowCanvas(SimplyShareTheme.WindowBackground(ui));
        var draw = ui.GetWindowDrawList();
        var contentRect = new UiRect(
            canvas.X + 18f,
            canvas.Y + 16f,
            MathF.Max(1f, canvas.Width - 36f),
            MathF.Max(1f, canvas.Height - 32f));
        var footerRect = new UiRect(contentRect.X, contentRect.Y + contentRect.Height - 50f, contentRect.Width, 50f);
        var formRect = new UiRect(contentRect.X, contentRect.Y + 78f, contentRect.Width, MathF.Max(1f, footerRect.Y - contentRect.Y - 86f));

        ui.PushDirectTextFontPaths(SimplyShareTheme.TitlePrimaryFontPath, SimplyShareTheme.TitleSecondaryFontPath);
        ui.DrawTextAligned(new UiRect(contentRect.X, contentRect.Y, contentRect.Width, 26f), "SimplyShare에 오신 것을 환영합니다!", SimplyShareTheme.TextPrimary(ui), fontSize: 18f);
        ui.PopDirectTextFontPaths();
        ui.DrawTextAligned(new UiRect(contentRect.X, contentRect.Y + 32f, contentRect.Width, 32f), "초기 설정을 완료하면 같은 네트워크의 장치를 검색합니다.", SimplyShareTheme.TextSecondary(ui), fontSize: 11f);
        DrawHorizontalLine(draw, formRect.Y - 10f, formRect.X, formRect.Width, SimplyShareTheme.Border(ui), ui.WhiteTextureId);

        ui.SetCursorScreenPos(new UiVector2(formRect.X, formRect.Y));
        _ = ui.BeginChild("setup-form", new UiVector2(formRect.Width, formRect.Height), border: false);
        RenderSetupFieldLabel(ui, "닉네임 (필수)");
        ui.SetNextItemWidth(MathF.Max(1f, ui.GetContentRegionAvail().X - 2f));
        ui.InputText("##setup-nickname", ref controller.SetupDraft.Nickname, 128);
        ui.Dummy(new UiVector2(0f, 14f));

        RenderSetupFieldLabel(ui, "수신 파일 저장 경로");
        ui.SetNextItemWidth(MathF.Max(1f, ui.GetContentRegionAvail().X - 2f));
        ui.InputText("##setup-download-path", ref controller.SetupDraft.DownloadPath, 512);
        ui.Dummy(new UiVector2(0f, 14f));

        RenderSetupFieldLabel(ui, "네트워크 대역 (선택, 예: 192.168.100.*)");
        ui.SetNextItemWidth(MathF.Max(1f, ui.GetContentRegionAvail().X - 2f));
        ui.InputTextMultiline("##setup-network-ranges", ref controller.SetupDraft.NetworkRangesText, 512, 4);
        ui.Dummy(new UiVector2(0f, 12f));
        ui.TextColored(
            controller.LastError is null ? SimplyShareTheme.TextSecondary(ui) : new UiColor(190, 35, 35),
            controller.StatusMessage);
        ui.EndChild();

        DrawHorizontalLine(draw, footerRect.Y, footerRect.X, footerRect.Width, SimplyShareTheme.Border(ui), ui.WhiteTextureId);
        const float buttonWidth = 120f;
        ui.SetCursorScreenPos(new UiVector2(footerRect.X + footerRect.Width - buttonWidth, footerRect.Y + 10f));
        ui.BeginDisabled(string.IsNullOrWhiteSpace(controller.SetupDraft.Nickname));
        if (ui.Button("시작하기", new UiVector2(buttonWidth, 32f)))
        {
            controller.BeginCompleteSetup();
        }
        ui.EndDisabled();
        ui.EndWindowCanvas();
    }

    private static void RenderSetupFieldLabel(UiImmediateContext ui, string text)
    {
        ui.PushDirectTextFontPaths(SimplyShareTheme.SemiBoldPrimaryFontPath, SimplyShareTheme.SemiBoldSecondaryFontPath);
        ui.Text(text);
        ui.PopDirectTextFontPaths();
        ui.Dummy(new UiVector2(0f, 3f));
    }

    private void RenderDevicesSection(UiImmediateContext ui, UiRect listRect)
    {
        ui.SetCursorScreenPos(new UiVector2(listRect.X, listRect.Y));
        _ = ui.BeginChild("device-list", new UiVector2(listRect.Width, listRect.Height), border: false);
        var rowWidth = MathF.Max(1f, ui.GetContentRegionAvail().X);

        if (controller.Devices.Count is 0)
        {
            ui.DrawTextAligned(listRect, "같은 네트워크의 장치를 검색 중...", SimplyShareTheme.TextSecondary(ui), UiItemHorizontalAlign.Center, UiItemVerticalAlign.Center, 12f);
            ui.EndChild();
            return;
        }

        var draw = ui.GetWindowDrawList();
        foreach (var device in controller.Devices)
        {
            var selected = string.Equals(controller.SelectedDeviceId, device.DeviceId, StringComparison.Ordinal);
            var rowStart = ui.GetCursorScreenPos();
            var rowRect = new UiRect(rowStart.X, rowStart.Y, rowWidth, 41f);

            ui.SetCursorScreenPos(new UiVector2(rowRect.X, rowRect.Y));
            var pressed = ui.InvisibleButton($"device-row##{device.DeviceId}", new UiVector2(rowRect.Width, rowRect.Height));
            var hovered = ui.IsItemHovered();
            var active = ui.IsItemActive();
            if (pressed)
            {
                controller.SelectDevice(device.DeviceId);
                if (IsDoubleClick(device.DeviceId))
                {
                    controller.OpenChatWindow(device.DeviceId);
                }
            }

            if (selected)
            {
                draw.AddRectFilled(rowRect, SimplyShareTheme.Selection(ui), ui.WhiteTextureId, rowRect);
            }
            else if (active)
            {
                draw.AddRectFilled(rowRect, SimplyShareTheme.Selection(ui), ui.WhiteTextureId, rowRect);
            }
            else if (hovered)
            {
                draw.AddRectFilled(rowRect, SimplyShareTheme.SelectionHovered(ui), ui.WhiteTextureId, rowRect);
            }

            draw.AddCircleFilled(new UiVector2(rowRect.X + 13f, rowRect.Y + 20f), 5f, device.IsOnline ? SimplyShareTheme.StatusOnline : SimplyShareTheme.StatusOffline, ui.WhiteTextureId, rowRect);
            ui.PushDirectTextFontPaths(SimplyShareTheme.SemiBoldPrimaryFontPath, SimplyShareTheme.SemiBoldSecondaryFontPath);
            ui.DrawTextAligned(new UiRect(rowRect.X + 28f, rowRect.Y + 5f, rowRect.Width - 34f, 16f), device.Nickname, SimplyShareTheme.TextPrimary(ui), fontSize: 13f);
            ui.PopDirectTextFontPaths();
            ui.DrawTextAligned(new UiRect(rowRect.X + 28f, rowRect.Y + 22f, rowRect.Width - 34f, 14f), device.IpAddress, SimplyShareTheme.TextSecondary(ui), fontSize: 10f);
            ui.SetCursorScreenPos(new UiVector2(rowStart.X, rowRect.Y + 42f));
        }

        ui.EndChild();
    }

    private static void DrawHorizontalLine(UiDrawListBuilder draw, float y, float x, float width, UiColor color, UiTextureId textureId)
        => draw.AddRectFilled(new UiRect(x, y, width, 1f), color, textureId);

    private static void DrawSettingsButton(UiImmediateContext ui, UiDrawListBuilder draw, UiRect rect, UiTextureId textureId, bool hovered, bool active)
    {
        if (active)
        {
            draw.AddCircleFilled(new UiVector2(rect.X + (rect.Width * 0.5f), rect.Y + (rect.Height * 0.5f)), 13f, SimplyShareTheme.Selection(ui), textureId, rect);
        }
        else if (hovered)
        {
            draw.AddCircleFilled(new UiVector2(rect.X + (rect.Width * 0.5f), rect.Y + (rect.Height * 0.5f)), 13f, SimplyShareTheme.SelectionHovered(ui), textureId, rect);
        }

        var center = new UiVector2(rect.X + (rect.Width * 0.5f), rect.Y + (rect.Height * 0.5f));
        const float outerRadius = 6.1f;
        const float innerRadius = 2.7f;
        const float toothLength = 2.5f;
        const float toothThickness = 1.25f;
        var spokeColor = SimplyShareTheme.TextPrimary(ui);

        for (var i = 0; i < 8; i++)
        {
            var angle = (MathF.Tau / 8f) * i;
            var sin = MathF.Sin(angle);
            var cos = MathF.Cos(angle);
            var start = new UiVector2(center.X + (cos * outerRadius), center.Y + (sin * outerRadius));
            var end = new UiVector2(center.X + (cos * (outerRadius + toothLength)), center.Y + (sin * (outerRadius + toothLength)));
            draw.AddLine(start, end, spokeColor, toothThickness, textureId);
        }

        draw.AddCircle(center, outerRadius, spokeColor, 24, 1.35f);
        draw.AddCircle(center, innerRadius, spokeColor, 18, 1.25f);
    }

    private bool IsDoubleClick(string deviceId)
    {
        var now = DateTime.UtcNow.Ticks;
        var isDoubleClick = string.Equals(_lastClickedDeviceId, deviceId, StringComparison.Ordinal)
            && TimeSpan.FromTicks(now - _lastClickedTicks).TotalMilliseconds <= 450;
        _lastClickedDeviceId = deviceId;
        _lastClickedTicks = now;
        return isDoubleClick;
    }
}
