using Duxel.Core;
using SimplyShare.Models;

namespace SimplyShare.AppHost;

internal sealed class SimplyShareTransferRequestScreen(TransferRequest request, Action<bool> complete) : UiScreen
{
    public override void Render(UiImmediateContext ui)
    {
        ui.EnableRootViewportContentLayout(contentPadding: 0f);
        var canvas = ui.BeginWindowCanvas(SimplyShareTheme.WindowBackground(ui));
        const float margin = 20f;
        var width = MathF.Max(1f, canvas.Width - (margin * 2f));

        ui.PushDirectTextFontPaths(SimplyShareTheme.SemiBoldPrimaryFontPath, SimplyShareTheme.SemiBoldSecondaryFontPath);
        ui.DrawTextAligned(new UiRect(canvas.X + margin, canvas.Y + margin, width, 20f), $"{request.SenderNickname}님의 전송 요청", SimplyShareTheme.TextPrimary(ui), fontSize: 14f);
        ui.PopDirectTextFontPaths();
        ui.DrawTextAligned(new UiRect(canvas.X + margin, canvas.Y + margin + 40f, width, 42f), GetContentText(request), SimplyShareTheme.TextPrimary(ui), fontSize: 12f);

        if (request.Type is not TransferType.Text)
        {
            ui.DrawTextAligned(new UiRect(canvas.X + margin, canvas.Y + margin + 92f, width, 16f), $"크기: {FormatSize(request.TotalSize)}", SimplyShareTheme.TextSecondary(ui), fontSize: 11f);
        }

        const float buttonWidth = 80f;
        const float buttonGap = 8f;
        var buttonsX = canvas.X + canvas.Width - margin - (buttonWidth * 2f) - buttonGap;
        var buttonsY = canvas.Y + canvas.Height - margin - 34f;
        ui.SetCursorScreenPos(new UiVector2(buttonsX, buttonsY));
        if (ui.Button("거부", new UiVector2(buttonWidth, 34f))) complete(false);
        ui.SameLine(buttonGap);
        if (ui.Button("수락", new UiVector2(buttonWidth, 34f))) complete(true);
        ui.EndWindowCanvas();
    }

    private static string GetContentText(TransferRequest value)
        => value.Type switch
        {
            TransferType.Text => $"텍스트: {Truncate(value.TextContent ?? string.Empty, 200)}",
            TransferType.File => value.Files.Count > 0 ? $"파일: {value.Files[0].RelativePath}" : "파일 1개",
            TransferType.Files => $"파일 {value.Files.Count}개",
            TransferType.AppUpdate => "애플리케이션 업데이트",
            _ => "알 수 없는 전송",
        };

    private static string Truncate(string text, int maxLength)
        => text.Length <= maxLength ? text : $"{text[..maxLength]}...";

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var size = (double)bytes;
        var unit = 0;
        while (size >= 1024d && unit < units.Length - 1) { size /= 1024d; unit++; }
        return $"{size:F1} {units[unit]}";
    }
}
