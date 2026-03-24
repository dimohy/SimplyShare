using Duxel.Core;
using SimplyShare.Models;

namespace SimplyShare.AppHost;

internal sealed class SimplyShareScreen : UiScreen
{
    private readonly SimplyShareController _controller;
    private string? _lastClickedDeviceId;
    private long _lastClickedTicks;
    private const float HeaderTitleVersionGap = 8f;

    public SimplyShareScreen(SimplyShareController controller)
    {
        _controller = controller;
    }

    public override void Render(UiImmediateContext ui)
    {
        _controller.DrainUiActions();

        if (_controller.IsSetupRequired)
        {
            ui.EnableRootViewportContentLayout();
            RenderSetupView(ui);
        }
        else
        {
            ui.EnableRootViewportContentLayout(contentPadding: 0f);
            RenderMainView(ui);
        }

    }

    private void RenderMainView(UiImmediateContext ui)
    {
        var canvas = ui.BeginWindowCanvas(new UiColor(255, 255, 255));
        var draw = ui.GetWindowDrawList();
        const float horizontalMargin = 8f;
        const float verticalMargin = 8f;
        const float headerHeight = 63f;
        const float headerBottomGap = 10f;
        const float footerTopGap = 8f;
        const float footerHeight = 23f;

        var contentRect = new UiRect(
            canvas.X + horizontalMargin,
            canvas.Y + verticalMargin,
            canvas.Width - (horizontalMargin * 2f),
            canvas.Height - (verticalMargin * 2f));
        var headerRect = new UiRect(contentRect.X, contentRect.Y, contentRect.Width, headerHeight);
        var footerRect = new UiRect(contentRect.X, contentRect.Y + contentRect.Height - footerHeight, contentRect.Width, footerHeight);
        var listRect = new UiRect(
            contentRect.X,
            headerRect.Y + headerRect.Height + headerBottomGap,
            contentRect.Width,
            footerRect.Y - footerTopGap - (headerRect.Y + headerRect.Height + headerBottomGap));
        var gearRect = new UiRect(contentRect.X + contentRect.Width - 30f, contentRect.Y + 2f, 26f, 26f);
        var versionText = $"v{SimplyShare.Core.AppVersion.CurrentString}";
        var titleText = "SimplyShare";

        ui.PushDirectTextFontPaths(SimplyShareTheme.TitlePrimaryFontPath, SimplyShareTheme.TitleSecondaryFontPath);
        var titleSize = ui.CalcTextSize(titleText, 20f);
        ui.DrawTextAligned(new UiRect(headerRect.X, headerRect.Y + 1f, titleSize.X, 25f), titleText, SimplyShareTheme.TextPrimary, fontSize: 20f);
        ui.PopDirectTextFontPaths();

        var versionX = headerRect.X + titleSize.X + HeaderTitleVersionGap;
        ui.DrawTextAligned(new UiRect(versionX, headerRect.Y + 7f, 64f, 14f), versionText, SimplyShareTheme.TextSecondary, fontSize: 11f);
        ui.DrawTextAligned(new UiRect(headerRect.X, headerRect.Y + 27f, 230f, 14f), $"내 닉네임: {_controller.Settings.Nickname}", SimplyShareTheme.TextSecondary, fontSize: 11f);
        ui.DrawTextAligned(new UiRect(headerRect.X, headerRect.Y + 43f, 230f, 14f), $"내 IP: {_controller.LocalIpAddress}", SimplyShareTheme.TextSecondary, fontSize: 11f);

        ui.SetCursorScreenPos(new UiVector2(gearRect.X, gearRect.Y));
        var settingsPressed = ui.InvisibleButton("open-settings", new UiVector2(gearRect.Width, gearRect.Height));
        var settingsHovered = ui.IsItemHovered();
        var settingsActive = ui.IsItemActive();
        if (settingsPressed)
        {
            _controller.OpenSettingsWindow();
        }

        DrawSettingsButton(draw, gearRect, ui.WhiteTextureId, settingsHovered, settingsActive);

        DrawBorder(draw, listRect, ui.WhiteTextureId);
        RenderDevicesSection(ui, listRect);

        DrawHorizontalLine(draw, footerRect.Y, footerRect.X, footerRect.Width, SimplyShareTheme.Border, ui.WhiteTextureId);
        ui.DrawTextAligned(new UiRect(footerRect.X, footerRect.Y + 6f, footerRect.Width, 12f), _controller.StatusMessage, new UiColor(153, 153, 153), UiItemHorizontalAlign.Left, UiItemVerticalAlign.Top, 10f);

        ui.EndWindowCanvas();
    }

    private void RenderSetupView(UiImmediateContext ui)
    {
        ui.Text("SimplyShare");
        ui.TextDisabled($"v{SimplyShare.Core.AppVersion.CurrentString}");
        ui.Separator();
        ui.Text("초기 설정");
        ui.TextWrapped("초기 설정을 완료하면 검색과 전송을 시작합니다.");
        ui.InputText("닉네임", ref _controller.SetupDraft.Nickname, 128);
        ui.InputText("다운로드 경로", ref _controller.SetupDraft.DownloadPath, 512);
        ui.InputTextMultiline("네트워크 대역", ref _controller.SetupDraft.NetworkRangesText, 512, 6);

        if (ui.Button("초기 설정 완료"))
        {
            _controller.BeginCompleteSetup();
        }
    }

    private void RenderDevicesSection(UiImmediateContext ui, UiRect listRect)
    {
        ui.SetCursorScreenPos(new UiVector2(listRect.X, listRect.Y));
        _ = ui.BeginChild("device-list", new UiVector2(listRect.Width, listRect.Height), border: false);

        if (_controller.Devices.Count is 0)
        {
            ui.DrawTextAligned(listRect, "같은 네트워크의 장치를 검색 중...", new UiColor(187, 187, 187), UiItemHorizontalAlign.Center, UiItemVerticalAlign.Center, 13f);
            ui.EndChild();
            return;
        }

        var draw = ui.GetWindowDrawList();
        foreach (var device in _controller.Devices)
        {
            var selected = string.Equals(_controller.SelectedDeviceId, device.DeviceId, StringComparison.Ordinal);
            var rowStart = ui.GetCursorScreenPos();
            var rowRect = new UiRect(listRect.X + 1f, rowStart.Y, listRect.Width - 2f, 46f);

            ui.SetCursorScreenPos(new UiVector2(rowRect.X, rowRect.Y));
            if (ui.InvisibleButton($"device-row##{device.DeviceId}", new UiVector2(rowRect.Width, rowRect.Height)))
            {
                _controller.SelectDevice(device.DeviceId);

                if (IsDoubleClick(device.DeviceId))
                {
                    _controller.OpenChatWindow(device.DeviceId);
                }
            }

            if (selected)
            {
                draw.AddRectFilled(rowRect, new UiColor(248, 249, 251), ui.WhiteTextureId, rowRect);
            }

            draw.AddCircleFilled(new UiVector2(rowRect.X + 15f, rowRect.Y + 22f), 5f, device.IsOnline ? SimplyShareTheme.StatusOnline : SimplyShareTheme.StatusOffline, ui.WhiteTextureId, rowRect);
            ui.PushDirectTextFontPaths(SimplyShareTheme.SemiBoldPrimaryFontPath, SimplyShareTheme.SemiBoldSecondaryFontPath);
            ui.DrawTextAligned(new UiRect(rowRect.X + 28f, rowRect.Y + 8f, rowRect.Width - 34f, 16f), device.Nickname, SimplyShareTheme.TextPrimary, fontSize: 13f);
            ui.PopDirectTextFontPaths();
            ui.DrawTextAligned(new UiRect(rowRect.X + 28f, rowRect.Y + 24f, rowRect.Width - 34f, 14f), device.IpAddress, SimplyShareTheme.TextSecondary, fontSize: 10f);
            ui.Dummy(new UiVector2(listRect.Width, 47f));
        }

        ui.EndChild();
    }

    private static void DrawBorder(UiDrawListBuilder draw, UiRect rect, UiTextureId textureId)
    {
        DrawHorizontalLine(draw, rect.Y, rect.X, rect.Width, SimplyShareTheme.Border, textureId);
        DrawHorizontalLine(draw, rect.Y + rect.Height - 1f, rect.X, rect.Width, SimplyShareTheme.Border, textureId);
        draw.AddRectFilled(new UiRect(rect.X, rect.Y, 1f, rect.Height), SimplyShareTheme.Border, textureId, rect);
        draw.AddRectFilled(new UiRect(rect.X + rect.Width - 1f, rect.Y, 1f, rect.Height), SimplyShareTheme.Border, textureId, rect);
    }

    private static void DrawHorizontalLine(UiDrawListBuilder draw, float y, float x, float width, UiColor color, UiTextureId textureId)
        => draw.AddRectFilled(new UiRect(x, y, width, 1f), color, textureId);

    private static void DrawSettingsButton(UiDrawListBuilder draw, UiRect rect, UiTextureId textureId, bool hovered, bool active)
    {
        if (active)
        {
            draw.AddCircleFilled(new UiVector2(rect.X + rect.Width * 0.5f, rect.Y + rect.Height * 0.5f), 12f, new UiColor(234, 234, 234), textureId, rect);
        }
        else if (hovered)
        {
            draw.AddCircleFilled(new UiVector2(rect.X + rect.Width * 0.5f, rect.Y + rect.Height * 0.5f), 12f, new UiColor(244, 244, 244), textureId, rect);
        }

        var center = new UiVector2(rect.X + rect.Width * 0.5f, rect.Y + rect.Height * 0.5f);
        var outerRadius = 6.1f;
        var innerRadius = 2.7f;
        var toothLength = 2.5f;
        var toothThickness = 1.25f;
        var spokeColor = new UiColor(40, 40, 40);

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