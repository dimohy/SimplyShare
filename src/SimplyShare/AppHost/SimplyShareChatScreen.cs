using Duxel.Core;
using SimplyShare.Models;

namespace SimplyShare.AppHost;

internal sealed class SimplyShareChatScreen : UiScreen
{
    private static readonly string[] BoundarySideNames = ["오른쪽", "왼쪽", "상단", "하단"];
    private readonly ChatWindowState _state;

    public SimplyShareChatScreen(ChatWindowState state)
    {
        _state = state;
    }

    public override void Render(UiImmediateContext ui)
    {
        _state.DrainUiActions();
        ui.EnableRootViewportContentLayout();

        var canvas = ui.BeginWindowCanvas(new UiColor(255, 255, 255));
        var draw = ui.GetWindowDrawList();

        var headerHeight = 56f;
        var helperHeight = 28f;
        var footerHeight = 84f;
        var headerRect = new UiRect(canvas.X, canvas.Y, canvas.Width, headerHeight);
        var helperRect = new UiRect(canvas.X, canvas.Y + canvas.Height - footerHeight - helperHeight, canvas.Width, helperHeight);
        var footerRect = new UiRect(canvas.X, canvas.Y + canvas.Height - footerHeight, canvas.Width, footerHeight);
        var messageRect = new UiRect(canvas.X + 4f, headerRect.Y + headerRect.Height, canvas.Width - 8f, helperRect.Y - (headerRect.Y + headerRect.Height));

        // Header background
        draw.AddRectFilled(headerRect, new UiColor(248, 248, 248), ui.WhiteTextureId, headerRect);
        DrawHorizontalLine(draw, headerRect.Y + headerRect.Height - 1f, headerRect.X, headerRect.Width, SimplyShareTheme.Border, ui.WhiteTextureId);

        // Helper area
        draw.AddRectFilled(helperRect, new UiColor(248, 248, 248), ui.WhiteTextureId, helperRect);
        DrawHorizontalLine(draw, helperRect.Y, helperRect.X, helperRect.Width, SimplyShareTheme.Border, ui.WhiteTextureId);

        // Footer background
        draw.AddRectFilled(footerRect, new UiColor(250, 250, 250), ui.WhiteTextureId, footerRect);
        DrawHorizontalLine(draw, footerRect.Y, footerRect.X, footerRect.Width, SimplyShareTheme.Border, ui.WhiteTextureId);

        // Header left: status dot + nickname + IP
        var dotColor = _state.TargetDevice.IsOnline ? SimplyShareTheme.StatusOnline : SimplyShareTheme.StatusOffline;
        draw.AddCircleFilled(new UiVector2(headerRect.X + 16f, headerRect.Y + 22f), 5f, dotColor, ui.WhiteTextureId);

        ui.PushDirectTextFontPaths(SimplyShareTheme.SemiBoldPrimaryFontPath, SimplyShareTheme.SemiBoldSecondaryFontPath);
        ui.DrawTextAligned(new UiRect(headerRect.X + 28f, headerRect.Y + 10f, 150f, 18f), _state.TargetDevice.Nickname, SimplyShareTheme.TextPrimary, fontSize: 14f);
        ui.PopDirectTextFontPaths();
        ui.DrawTextAligned(new UiRect(headerRect.X + 28f, headerRect.Y + 30f, 150f, 14f), _state.TargetDevice.IpAddress, SimplyShareTheme.TextSecondary, fontSize: 10f);

        // Header right: checkbox(input) + checkbox(clipboard) + combo(boundary) — right-aligned like WPF
        var rightEdge = headerRect.X + headerRect.Width - 8f;

        var inputEnabled = _state.IsInputSharingEnabled;
        ui.SetCursorScreenPos(new UiVector2(rightEdge - 56f, headerRect.Y + 20f));
        if (ui.Checkbox("입력", ref inputEnabled))
        {
            _state.SetInputSharingEnabled(inputEnabled);
        }

        var clipboardEnabled = _state.IsClipboardSharingEnabled;
        ui.SetCursorScreenPos(new UiVector2(rightEdge - 130f, headerRect.Y + 20f));
        if (ui.Checkbox("클립보드", ref clipboardEnabled))
        {
            _state.SetClipboardSharingEnabled(clipboardEnabled);
        }

        var boundaryIndex = _state.BoundarySide switch
        {
            BoundarySide.Right => 0,
            BoundarySide.Left => 1,
            BoundarySide.Top => 2,
            BoundarySide.Bottom => 3,
            _ => 0,
        };

        ui.SetCursorScreenPos(new UiVector2(rightEdge - 220f, headerRect.Y + 16f));
        if (_state.IsBoundarySideEditable && ui.Combo(ref boundaryIndex, BoundarySideNames, 4, "chat-boundary"))
        {
            _state.SetBoundarySide(boundaryIndex switch
            {
                1 => BoundarySide.Left,
                2 => BoundarySide.Top,
                3 => BoundarySide.Bottom,
                _ => BoundarySide.Right,
            });
        }

        // Input mode label — between left info and right controls
        if (!string.IsNullOrWhiteSpace(_state.InputModeLabel))
        {
            ui.DrawTextAligned(new UiRect(headerRect.X + 180f, headerRect.Y + 22f, 80f, 14f), _state.InputModeLabel, SimplyShareTheme.TextSecondary, fontSize: 10f);
        }

        // Messages
        RenderMessages(ui, messageRect);

        // Helper text
        ui.DrawTextAligned(helperRect, "파일을 드래그하여 전송 · Ctrl+Enter로 전송", new UiColor(170, 170, 170), UiItemHorizontalAlign.Center, UiItemVerticalAlign.Center, 10f);

        // Footer: input textbox + buttons
        ui.SetCursorScreenPos(new UiVector2(footerRect.X + 8f, footerRect.Y + 8f));
        ui.InputTextMultiline("##chat-input", ref _state.InputText, 4_096, 60f);

        ui.SetCursorScreenPos(new UiVector2(footerRect.X + footerRect.Width - 98f, footerRect.Y + 46f));
        if (ui.Button("전송", new UiVector2(58f, 30f)))
        {
            _state.BeginSendText();
        }

        ui.SetCursorScreenPos(new UiVector2(footerRect.X + footerRect.Width - 138f, footerRect.Y + 46f));
        if (ui.Button("📎", new UiVector2(32f, 30f)))
        {
            var selectedFiles = NativeFileDialog.PickFiles("전송할 파일 선택");
            if (selectedFiles.Count > 0)
            {
                _state.BeginSendFiles(selectedFiles);
            }
        }

        ui.EndWindowCanvas();
        _state.EnsureConnected();
    }

    private void RenderMessages(UiImmediateContext ui, UiRect messageRect)
    {
        var draw = ui.GetWindowDrawList();
        ui.SetCursorScreenPos(new UiVector2(messageRect.X, messageRect.Y));
        _ = ui.BeginChild("chat-messages", new UiVector2(messageRect.Width, messageRect.Height), border: false);

        foreach (var message in _state.Messages)
        {
            var start = ui.GetCursorScreenPos();

            if (message.Type is ChatMessageType.System)
            {
                var systemRect = new UiRect(messageRect.X + 60f, start.Y + 4f, messageRect.Width - 120f, 16f);
                ui.DrawTextAligned(systemRect, message.Text ?? string.Empty, new UiColor(170, 170, 170), UiItemHorizontalAlign.Center, UiItemVerticalAlign.Center, 10f);
                ui.Dummy(new UiVector2(messageRect.Width, 24f));
                continue;
            }

            const float maxBubbleWidth = 300f;
            const float bubblePadX = 10f;
            const float bubblePadY = 6f;
            const float bubbleMarginOuter = 4f;
            const float bubbleMarginInner = 60f;

            if (message.Type is ChatMessageType.Text)
            {
                var wrappedText = WrapText(message.Text ?? string.Empty, 28);
                var lineCount = Math.Max(1, wrappedText.Split('\n').Length);
                var textHeight = lineCount * 18f;
                var bubbleHeight = textHeight + bubblePadY * 2f;
                var bubbleWidth = MathF.Min(maxBubbleWidth, messageRect.Width - bubbleMarginOuter - bubbleMarginInner);
                var bubbleX = message.Direction is ChatDirection.Sent
                    ? messageRect.X + messageRect.Width - bubbleWidth - bubbleMarginOuter
                    : messageRect.X + bubbleMarginOuter;
                var bubbleRect = new UiRect(bubbleX, start.Y + 2f, bubbleWidth, bubbleHeight);
                var bubbleColor = message.Direction is ChatDirection.Sent ? SimplyShareTheme.BubbleSent : SimplyShareTheme.BubbleReceived;
                draw.AddRectFilled(bubbleRect, bubbleColor, ui.WhiteTextureId, messageRect);

                ui.DrawTextAligned(new UiRect(bubbleRect.X + bubblePadX, bubbleRect.Y + bubblePadY, bubbleRect.Width - bubblePadX * 2f, bubbleRect.Height - bubblePadY * 2f), wrappedText, SimplyShareTheme.TextPrimary, fontSize: 13f);

                var timestampRect = message.Direction is ChatDirection.Sent
                    ? new UiRect(bubbleRect.X + bubbleRect.Width - 44f, bubbleRect.Y + bubbleRect.Height + 1f, 40f, 12f)
                    : new UiRect(bubbleRect.X + 4f, bubbleRect.Y + bubbleRect.Height + 1f, 40f, 12f);
                ui.DrawTextAligned(timestampRect, message.Timestamp.ToString("HH:mm"), new UiColor(170, 170, 170), fontSize: 9f);

                ui.Dummy(new UiVector2(messageRect.Width, bubbleRect.Height + 16f));
            }
            else
            {
                // File message
                var bubbleHeight = 52f;
                var bubbleWidth = MathF.Min(246f, messageRect.Width - bubbleMarginOuter - bubbleMarginInner);
                var bubbleX = message.Direction is ChatDirection.Sent
                    ? messageRect.X + messageRect.Width - bubbleWidth - bubbleMarginOuter
                    : messageRect.X + bubbleMarginOuter;
                var bubbleRect = new UiRect(bubbleX, start.Y + 2f, bubbleWidth, bubbleHeight);
                var bubbleColor = message.Direction is ChatDirection.Sent ? SimplyShareTheme.BubbleSent : SimplyShareTheme.BubbleReceived;
                draw.AddRectFilled(bubbleRect, bubbleColor, ui.WhiteTextureId, messageRect);

                ui.DrawTextAligned(new UiRect(bubbleRect.X + 10f, bubbleRect.Y + 6f, 24f, 24f), "📎", SimplyShareTheme.TextPrimary, UiItemHorizontalAlign.Left, UiItemVerticalAlign.Top, 18f);

                ui.PushDirectTextFontPaths(SimplyShareTheme.SemiBoldPrimaryFontPath, SimplyShareTheme.SemiBoldSecondaryFontPath);
                ui.DrawTextAligned(new UiRect(bubbleRect.X + 36f, bubbleRect.Y + 6f, bubbleRect.Width - 46f, 18f), message.FileName ?? string.Empty, SimplyShareTheme.TextPrimary, fontSize: 12f);
                ui.PopDirectTextFontPaths();

                ui.DrawTextAligned(new UiRect(bubbleRect.X + 36f, bubbleRect.Y + 26f, bubbleRect.Width - 46f, 14f), FormatFileSize(message.FileSize), new UiColor(119, 119, 119), fontSize: 10f);

                if (message.Direction is ChatDirection.Received && !string.IsNullOrWhiteSpace(message.FilePath))
                {
                    ui.DrawTextAligned(new UiRect(bubbleRect.X + 36f, bubbleRect.Y + 38f, bubbleRect.Width - 46f, 12f), "클릭하여 열기", new UiColor(30, 144, 255), fontSize: 9f);
                    ui.SetCursorScreenPos(new UiVector2(bubbleRect.X, bubbleRect.Y));
                    if (ui.InvisibleButton($"open-file##{message.Id}", new UiVector2(bubbleRect.Width, bubbleRect.Height)))
                    {
                        _state.OpenReceivedFile(message.FilePath);
                    }
                }

                var timestampRect = message.Direction is ChatDirection.Sent
                    ? new UiRect(bubbleRect.X + bubbleRect.Width - 44f, bubbleRect.Y + bubbleRect.Height + 1f, 40f, 12f)
                    : new UiRect(bubbleRect.X + 4f, bubbleRect.Y + bubbleRect.Height + 1f, 40f, 12f);
                ui.DrawTextAligned(timestampRect, message.Timestamp.ToString("HH:mm"), new UiColor(170, 170, 170), fontSize: 9f);

                ui.Dummy(new UiVector2(messageRect.Width, bubbleRect.Height + 16f));
            }
        }

        ui.EndChild();
    }

    private static void DrawHorizontalLine(UiDrawListBuilder draw, float y, float x, float width, UiColor color, UiTextureId textureId)
        => draw.AddRectFilled(new UiRect(x, y, width, 1f), color, textureId);

    private static string WrapText(string text, int maxCharsPerLine)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length <= maxCharsPerLine)
        {
            return text;
        }

        var lines = new List<string>();
        var current = new List<char>(maxCharsPerLine);

        foreach (var ch in text)
        {
            if (ch == '\n')
            {
                lines.Add(new string([.. current]));
                current.Clear();
                continue;
            }

            current.Add(ch);
            if (current.Count < maxCharsPerLine)
            {
                continue;
            }

            lines.Add(new string([.. current]));
            current.Clear();
        }

        if (current.Count > 0)
        {
            lines.Add(new string([.. current]));
        }

        return string.Join('\n', lines);
    }

    private static string FormatFileSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double size = bytes;
        var unitIndex = 0;

        while (size >= 1024d && unitIndex < units.Length - 1)
        {
            size /= 1024d;
            unitIndex++;
        }

        return unitIndex is 0 ? $"{size:0} {units[unitIndex]}" : $"{size:0.##} {units[unitIndex]}";
    }
}