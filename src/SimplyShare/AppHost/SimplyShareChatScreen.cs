using Duxel.Core;
using SimplyShare.Models;
using System.Text;

namespace SimplyShare.AppHost;

internal sealed class SimplyShareChatScreen(ChatWindowState state, Action<string> openTextViewer) : UiScreen
{
    private static readonly string[] BoundarySideNames = ["오른쪽", "왼쪽", "상단", "하단"];
    private const string MessagesChildId = "chat-messages";
    private int _knownMessageCount;
    private bool _wasAtMessageBottom = true;
    private bool _scrollToMessageBottom = true;
    private bool _showScrollToBottom;

    public override void Render(UiImmediateContext ui)
    {
        state.DrainUiActions();
        ui.EnableRootViewportContentLayout(contentPadding: 0f);

        var canvas = ui.BeginWindowCanvas(SimplyShareTheme.WindowBackground(ui));
        var draw = ui.GetWindowDrawList();
        const float headerHeight = 52f;
        const float helperHeight = 23f;
        var composerInputHeight = GetComposerInputHeight(state.InputText);
        var footerHeight = composerInputHeight + 16f;
        var headerRect = new UiRect(canvas.X, canvas.Y, canvas.Width, headerHeight);
        var helperRect = new UiRect(canvas.X, canvas.Y + canvas.Height - footerHeight - helperHeight, canvas.Width, helperHeight);
        var footerRect = new UiRect(canvas.X, canvas.Y + canvas.Height - footerHeight, canvas.Width, footerHeight);
        var messageRect = new UiRect(
            canvas.X + 4f,
            headerRect.Y + headerRect.Height,
            MathF.Max(1f, canvas.Width - 8f),
            MathF.Max(1f, helperRect.Y - (headerRect.Y + headerRect.Height)));

        draw.AddRectFilled(headerRect, SimplyShareTheme.Surface(ui), ui.WhiteTextureId, headerRect);
        DrawHorizontalLine(draw, headerRect.Y + headerRect.Height - 1f, headerRect.X, headerRect.Width, SimplyShareTheme.Border(ui), ui.WhiteTextureId);
        draw.AddRectFilled(helperRect, SimplyShareTheme.Surface(ui), ui.WhiteTextureId, helperRect);
        DrawHorizontalLine(draw, helperRect.Y, helperRect.X, helperRect.Width, SimplyShareTheme.Border(ui), ui.WhiteTextureId);
        draw.AddRectFilled(footerRect, SimplyShareTheme.WindowBackground(ui), ui.WhiteTextureId, footerRect);
        DrawHorizontalLine(draw, footerRect.Y, footerRect.X, footerRect.Width, SimplyShareTheme.Border(ui), ui.WhiteTextureId);

        RenderHeader(ui, draw, headerRect);
        RenderMessages(ui, messageRect);
        ui.DrawTextAligned(helperRect, "파일/폴더를 창에 놓아 전송 · Ctrl+Enter로 전송", SimplyShareTheme.TextSecondary(ui), UiItemHorizontalAlign.Center, UiItemVerticalAlign.Center, 10f);
        RenderComposer(ui, footerRect);

        ui.EndWindowCanvas();
        state.EnsureConnected();
    }

    private void RenderHeader(UiImmediateContext ui, UiDrawListBuilder draw, UiRect rect)
    {
        var dotColor = state.TargetDevice.IsOnline ? SimplyShareTheme.StatusOnline : SimplyShareTheme.StatusOffline;
        draw.AddCircleFilled(new UiVector2(rect.X + 17f, rect.Y + 25f), 5f, dotColor, ui.WhiteTextureId);
        ui.PushDirectTextFontPaths(SimplyShareTheme.SemiBoldPrimaryFontPath, SimplyShareTheme.SemiBoldSecondaryFontPath);
        ui.DrawTextAligned(new UiRect(rect.X + 30f, rect.Y + 8f, 134f, 18f), state.TargetDevice.Nickname, SimplyShareTheme.TextPrimary(ui), fontSize: 14f);
        ui.PopDirectTextFontPaths();
        ui.DrawTextAligned(new UiRect(rect.X + 30f, rect.Y + 29f, 86f, 14f), state.TargetDevice.IpAddress, SimplyShareTheme.TextSecondary(ui), fontSize: 10f);

        var boundaryIndex = state.BoundarySide switch
        {
            BoundarySide.Right => 0,
            BoundarySide.Left => 1,
            BoundarySide.Top => 2,
            BoundarySide.Bottom => 3,
            _ => 0,
        };

        var inputX = rect.X + rect.Width - 65f;
        var clipboardX = inputX - 100f;
        var boundaryX = clipboardX - 87f;
        var inputModeWidth = MathF.Max(1f, boundaryX - (rect.X + 112f) - 8f);
        if (!string.IsNullOrWhiteSpace(state.InputModeLabel))
        {
            ui.DrawTextAligned(
                new UiRect(rect.X + 112f, rect.Y + 28f, inputModeWidth, 14f),
                state.InputModeLabel,
                SimplyShareTheme.TextSecondary(ui),
                UiItemHorizontalAlign.Right,
                UiItemVerticalAlign.Center,
                9f);
        }

        ui.PushFontSize(13f);
        ui.PushStyleVarY(UiStyleVar.FramePadding, 4f);
        var controlHeight = ui.GetFrameHeight();
        var controlY = rect.Y + MathF.Round((rect.Height - controlHeight) * 0.5f);

        ui.SetCursorScreenPos(new UiVector2(boundaryX, controlY));
        ui.SetNextItemWidth(82f);
        ui.BeginDisabled(!state.IsBoundarySideEditable);
        if (ui.Combo(ref boundaryIndex, BoundarySideNames, 4, "chat-boundary"))
        {
            state.SetBoundarySide(boundaryIndex switch
            {
                1 => BoundarySide.Left,
                2 => BoundarySide.Top,
                3 => BoundarySide.Bottom,
                _ => BoundarySide.Right,
            });
        }
        ui.EndDisabled();

        var clipboardEnabled = state.IsClipboardSharingEnabled;
        ui.SetCursorScreenPos(new UiVector2(clipboardX, controlY));
        ui.BeginDisabled(!state.IsChatConnected);
        if (ui.Checkbox("클립보드", ref clipboardEnabled))
        {
            state.SetClipboardSharingEnabled(clipboardEnabled);
        }
        ui.EndDisabled();

        var inputEnabled = state.IsInputSharingEnabled;
        ui.SetCursorScreenPos(new UiVector2(inputX, controlY));
        ui.BeginDisabled(!state.IsChatConnected);
        if (ui.Checkbox("입력", ref inputEnabled))
        {
            state.SetInputSharingEnabled(inputEnabled);
        }
        ui.EndDisabled();

        ui.PopStyleVar();
        ui.PopFontSize();
    }

    private void RenderComposer(UiImmediateContext ui, UiRect rect)
    {
        const float horizontalPadding = 8f;
        const float controlGap = 6f;
        const float attachWidth = 40f;
        const float sendWidth = 64f;
        const float buttonHeight = 40f;
        var inputHeight = MathF.Max(40f, rect.Height - 16f);
        var inputY = rect.Y + MathF.Round((rect.Height - inputHeight) * 0.5f);
        var buttonY = rect.Y + MathF.Round((rect.Height - buttonHeight) * 0.5f);
        var rightAreaWidth = attachWidth + sendWidth + (controlGap * 2f);
        var inputWidth = MathF.Max(120f, rect.Width - rightAreaWidth - (horizontalPadding * 2f));
        ui.SetCursorScreenPos(new UiVector2(rect.X + horizontalPadding, inputY));
        ui.SetNextItemWidth(inputWidth);
        ui.InputTextMultiline("##chat-input", ref state.InputText, 4_096, inputHeight);
        ui.SetItemDefaultFocus();
        var inputFocused = ui.IsItemFocused();

        if (inputFocused && ui.Shortcut(UiKey.Enter, KeyModifiers.Ctrl))
        {
            state.BeginSendText();
        }

        var attachRect = new UiRect(rect.X + rect.Width - sendWidth - attachWidth - controlGap - horizontalPadding, buttonY, attachWidth, buttonHeight);
        ui.SetCursorScreenPos(new UiVector2(attachRect.X, attachRect.Y));
        var attachPressed = ui.InvisibleButton("chat-attach", new UiVector2(attachRect.Width, attachRect.Height));
        var attachHovered = ui.IsItemHovered();
        var attachActive = ui.IsItemActive();
        if (attachPressed)
        {
            var selectedFiles = NativeFileDialog.PickFiles("전송할 파일 선택");
            if (selectedFiles.Count > 0)
            {
                state.BeginSendFiles(selectedFiles);
            }
        }
        DrawAttachButton(ui, attachRect, attachHovered, attachActive, enabled: true);

        ui.SetCursorScreenPos(new UiVector2(rect.X + rect.Width - sendWidth - horizontalPadding, buttonY));
        if (ui.Button("전송", new UiVector2(sendWidth, buttonHeight)))
        {
            state.BeginSendText();
        }
    }

    private void RenderMessages(UiImmediateContext ui, UiRect messageRect)
    {
        if (state.Messages.Count != _knownMessageCount)
        {
            if (_wasAtMessageBottom)
            {
                _scrollToMessageBottom = true;
            }
            else
            {
                _showScrollToBottom = true;
            }

            _knownMessageCount = state.Messages.Count;
        }

        var draw = ui.GetWindowDrawList();
        ui.SetCursorScreenPos(new UiVector2(messageRect.X, messageRect.Y));
        _ = ui.BeginChild(MessagesChildId, new UiVector2(messageRect.Width, messageRect.Height), border: false);
        var contentOrigin = ui.GetCursorScreenPos();
        var contentWidth = MathF.Max(1f, ui.GetContentRegionAvail().X - 12f);

        foreach (var message in state.Messages)
        {
            var start = ui.GetCursorScreenPos();
            if (message.Type is ChatMessageType.System)
            {
                var systemRect = new UiRect(contentOrigin.X + 28f, start.Y + 5f, MathF.Max(1f, contentWidth - 56f), 16f);
                ui.DrawTextAligned(systemRect, message.Text ?? string.Empty, SimplyShareTheme.TextSecondary(ui), UiItemHorizontalAlign.Center, UiItemVerticalAlign.Center, 10f);
                ui.SetCursorScreenPos(new UiVector2(contentOrigin.X, start.Y + 28f));
                continue;
            }

            const float bubblePadX = 10f;
            const float bubblePadY = 6f;
            const float bubbleMarginOuter = 6f;
            if (message.Type is ChatMessageType.Text)
            {
                var maxTextWidth = MathF.Max(56f, MathF.Min(280f, contentWidth - 88f));
                var wrapped = WrapText(ui, message.Text ?? string.Empty, maxTextWidth, 5);
                var textBubbleWidth = Math.Clamp(wrapped.Width + (bubblePadX * 2f), 52f, MathF.Min(300f, contentWidth - 68f));
                var textBubbleX = message.Direction is ChatDirection.Sent
                    ? contentOrigin.X + contentWidth - textBubbleWidth - bubbleMarginOuter
                    : contentOrigin.X + bubbleMarginOuter;
                var bubbleHeight = (wrapped.LineCount * 18f) + (bubblePadY * 2f);
                var bubbleRect = new UiRect(textBubbleX, start.Y + 2f, textBubbleWidth, bubbleHeight);

                ui.SetCursorScreenPos(new UiVector2(bubbleRect.X, bubbleRect.Y));
                _ = ui.InvisibleButton($"text-message##{message.Id}", new UiVector2(bubbleRect.Width, bubbleRect.Height));
                if (ui.IsItemHovered() && ui.IsMouseDoubleClicked((int)UiMouseButton.Left))
                {
                    openTextViewer(message.Text ?? string.Empty);
                }

                DrawMessageBubble(draw, bubbleRect, GetBubbleColor(ui, message), message.Direction, ui.WhiteTextureId, messageRect);
                ui.DrawTextAligned(
                    new UiRect(bubbleRect.X + bubblePadX, bubbleRect.Y + bubblePadY, bubbleRect.Width - (bubblePadX * 2f), bubbleRect.Height - (bubblePadY * 2f)),
                    wrapped.Text,
                    SimplyShareTheme.TextPrimary(ui),
                    fontSize: 13f);
                DrawTimestamp(ui, message, bubbleRect);
                ui.SetCursorScreenPos(new UiVector2(contentOrigin.X, bubbleRect.Y + bubbleRect.Height + 17f));
                continue;
            }

            const float fileBubbleHeight = 56f;
            var fileTextWidth = ui.CalcTextSize(message.FileName ?? string.Empty, 12f).X;
            var fileBubbleWidth = Math.Clamp(fileTextWidth + 58f, 176f, MathF.Min(300f, contentWidth - 68f));
            var fileBubbleX = message.Direction is ChatDirection.Sent
                ? contentOrigin.X + contentWidth - fileBubbleWidth - bubbleMarginOuter
                : contentOrigin.X + bubbleMarginOuter;
            var fileRect = new UiRect(fileBubbleX, start.Y + 2f, fileBubbleWidth, fileBubbleHeight);
            ui.SetCursorScreenPos(new UiVector2(fileRect.X, fileRect.Y));
            var filePressed = ui.InvisibleButton($"file-message##{message.Id}", new UiVector2(fileRect.Width, fileRect.Height));
            if (filePressed && message.Direction is ChatDirection.Received && !string.IsNullOrWhiteSpace(message.FilePath))
            {
                state.OpenReceivedFile(message.FilePath);
            }

            DrawMessageBubble(draw, fileRect, GetBubbleColor(ui, message), message.Direction, ui.WhiteTextureId, messageRect);
            DrawPaperclip(draw, new UiVector2(fileRect.X + 21f, fileRect.Y + 22f), SimplyShareTheme.TextPrimary(ui), 1.7f);
            var fileName = FitTextToWidth(ui, message.FileName ?? string.Empty, MathF.Max(1f, fileRect.Width - 50f), 12f);
            ui.PushDirectTextFontPaths(SimplyShareTheme.SemiBoldPrimaryFontPath, SimplyShareTheme.SemiBoldSecondaryFontPath);
            ui.DrawTextAligned(new UiRect(fileRect.X + 40f, fileRect.Y + 7f, fileRect.Width - 50f, 18f), fileName, SimplyShareTheme.TextPrimary(ui), fontSize: 12f);
            ui.PopDirectTextFontPaths();
            ui.DrawTextAligned(new UiRect(fileRect.X + 40f, fileRect.Y + 27f, fileRect.Width - 50f, 14f), FormatFileSize(message.FileSize), SimplyShareTheme.TextSecondary(ui), fontSize: 10f);
            if (message.Direction is ChatDirection.Received && !string.IsNullOrWhiteSpace(message.FilePath))
            {
                ui.DrawTextAligned(new UiRect(fileRect.X + 40f, fileRect.Y + 40f, fileRect.Width - 50f, 12f), "클릭하여 열기", ui.GetColorU32(UiStyleColor.CheckMark), fontSize: 9f);
            }
            DrawTimestamp(ui, message, fileRect);
            ui.SetCursorScreenPos(new UiVector2(contentOrigin.X, fileRect.Y + fileRect.Height + 17f));
        }

        if (_scrollToMessageBottom)
        {
            ui.SetScrollY(ui.GetScrollMaxY());
            _scrollToMessageBottom = false;
            _wasAtMessageBottom = true;
            _showScrollToBottom = false;
        }
        else
        {
            var maximumScroll = ui.GetScrollMaxY();
            _wasAtMessageBottom = maximumScroll <= 0f || ui.GetScrollY() >= maximumScroll - 4f;
            if (_wasAtMessageBottom)
            {
                _showScrollToBottom = false;
            }
        }

        ui.EndChild();

        if (_showScrollToBottom)
        {
            const float buttonWidth = 36f;
            const float buttonHeight = 28f;
            ui.SetCursorScreenPos(new UiVector2(
                messageRect.X + messageRect.Width - buttonWidth - 14f,
                messageRect.Y + messageRect.Height - buttonHeight - 12f));
            if (ui.Button("↓##scroll-chat-bottom", new UiVector2(buttonWidth, buttonHeight)))
            {
                _scrollToMessageBottom = true;
            }
        }
    }

    private static UiColor GetBubbleColor(UiImmediateContext ui, ChatMessage message)
        => message.Direction is ChatDirection.Sent ? SimplyShareTheme.BubbleSent(ui) : SimplyShareTheme.BubbleReceived(ui);

    private static void DrawTimestamp(UiImmediateContext ui, ChatMessage message, UiRect bubbleRect)
    {
        var timestampRect = message.Direction is ChatDirection.Sent
            ? new UiRect(bubbleRect.X + bubbleRect.Width - 44f, bubbleRect.Y + bubbleRect.Height + 1f, 40f, 12f)
            : new UiRect(bubbleRect.X + 4f, bubbleRect.Y + bubbleRect.Height + 1f, 40f, 12f);
        ui.DrawTextAligned(timestampRect, message.Timestamp.ToString("HH:mm"), SimplyShareTheme.TextSecondary(ui), fontSize: 9f);
    }

    private static WrappedText WrapText(UiImmediateContext ui, string text, float maxWidth, int maxLines)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new WrappedText(string.Empty, 1, 0f);
        }

        var lines = new List<string>(maxLines);
        var current = new StringBuilder();
        var truncated = false;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character == '\r')
            {
                continue;
            }

            if (character == '\n')
            {
                lines.Add(current.ToString());
                current.Clear();
                if (lines.Count == maxLines)
                {
                    truncated = index < text.Length - 1;
                    break;
                }

                continue;
            }

            current.Append(character);
            if (current.Length > 1 && ui.CalcTextSize(current.ToString(), 13f).X > maxWidth)
            {
                current.Length--;
                lines.Add(current.ToString());
                current.Clear();
                current.Append(character);
                if (lines.Count == maxLines)
                {
                    truncated = true;
                    break;
                }
            }
        }

        if (!truncated && current.Length > 0 && lines.Count < maxLines)
        {
            lines.Add(current.ToString());
        }

        if (lines.Count == 0)
        {
            lines.Add(string.Empty);
        }

        if (truncated)
        {
            var lastLine = lines[^1];
            while (lastLine.Length > 0 && ui.CalcTextSize($"{lastLine}…", 13f).X > maxWidth)
            {
                lastLine = lastLine[..^1];
            }

            lines[^1] = $"{lastLine}…";
        }

        var width = lines.Select(line => ui.CalcTextSize(line, 13f).X).DefaultIfEmpty(0f).Max();
        return new WrappedText(string.Join('\n', lines), lines.Count, width);
    }

    private readonly record struct WrappedText(string Text, int LineCount, float Width);

    private static float GetComposerInputHeight(string text)
    {
        var lineCount = 1;
        foreach (var character in text)
        {
            if (character == '\n')
            {
                lineCount++;
            }
        }

        return Math.Clamp((lineCount * 18f) + 16f, 40f, 100f);
    }

    private static string FitTextToWidth(UiImmediateContext ui, string text, float maxWidth, float fontSize)
    {
        if (ui.CalcTextSize(text, fontSize).X <= maxWidth)
        {
            return text;
        }

        var length = text.Length;
        while (length > 0 && ui.CalcTextSize($"{text[..length]}…", fontSize).X > maxWidth)
        {
            length--;
        }

        return length > 0 ? $"{text[..length]}…" : "…";
    }

    private static void DrawAttachButton(
        UiImmediateContext ui,
        UiRect rect,
        bool hovered,
        bool active,
        bool enabled)
    {
        var draw = ui.GetWindowDrawList();
        if (hovered || active)
        {
            var background = active
                ? ui.GetColorU32(UiStyleColor.ButtonActive)
                : ui.GetColorU32(UiStyleColor.ButtonHovered);
            DrawRoundedRectFilled(draw, rect, background, rect.Width * 0.5f, ui.WhiteTextureId, rect);
        }

        var iconColor = enabled
            ? SimplyShareTheme.TextPrimary(ui)
            : SimplyShareTheme.TextSecondary(ui);
        DrawPaperclip(draw, new UiVector2(rect.X + (rect.Width * 0.5f), rect.Y + (rect.Height * 0.5f)), iconColor, 1.8f);
    }

    private static void DrawPaperclip(UiDrawListBuilder draw, UiVector2 center, UiColor color, float thickness)
    {
        draw.AddLine(new UiVector2(center.X - 6f, center.Y + 4f), new UiVector2(center.X + 4f, center.Y - 6f), color, thickness);
        draw.AddLine(new UiVector2(center.X - 2f, center.Y + 7f), new UiVector2(center.X + 7f, center.Y - 2f), color, thickness);
        draw.AddLine(new UiVector2(center.X - 6f, center.Y + 4f), new UiVector2(center.X - 2f, center.Y + 7f), color, thickness);
        draw.AddLine(new UiVector2(center.X + 4f, center.Y - 6f), new UiVector2(center.X + 7f, center.Y - 2f), color, thickness);
        draw.AddLine(new UiVector2(center.X - 3f, center.Y + 2f), new UiVector2(center.X + 2f, center.Y - 3f), color, thickness);
    }

    private static void DrawMessageBubble(
        UiDrawListBuilder draw,
        UiRect rect,
        UiColor color,
        ChatDirection direction,
        UiTextureId textureId,
        UiRect clipRect)
    {
        const float radius = 10f;
        DrawRoundedRectFilled(draw, rect, color, radius, textureId, clipRect);

        var squareCorner = direction is ChatDirection.Sent
            ? new UiRect(rect.X + rect.Width - radius, rect.Y + rect.Height - radius, radius, radius)
            : new UiRect(rect.X, rect.Y + rect.Height - radius, radius, radius);
        draw.AddRectFilled(squareCorner, color, textureId, clipRect);
    }

    private static void DrawRoundedRectFilled(
        UiDrawListBuilder draw,
        UiRect rect,
        UiColor color,
        float radius,
        UiTextureId textureId,
        UiRect clipRect)
    {
        var resolvedRadius = MathF.Min(radius, MathF.Min(rect.Width, rect.Height) * 0.5f);
        if (resolvedRadius <= 0f)
        {
            draw.AddRectFilled(rect, color, textureId, clipRect);
            return;
        }

        draw.AddRectFilled(
            new UiRect(rect.X + resolvedRadius, rect.Y, rect.Width - (resolvedRadius * 2f), rect.Height),
            color,
            textureId,
            clipRect);
        draw.AddRectFilled(
            new UiRect(rect.X, rect.Y + resolvedRadius, rect.Width, rect.Height - (resolvedRadius * 2f)),
            color,
            textureId,
            clipRect);
        draw.AddCircleFilled(new UiVector2(rect.X + resolvedRadius, rect.Y + resolvedRadius), resolvedRadius, color, textureId, clipRect, 12);
        draw.AddCircleFilled(new UiVector2(rect.X + rect.Width - resolvedRadius, rect.Y + resolvedRadius), resolvedRadius, color, textureId, clipRect, 12);
        draw.AddCircleFilled(new UiVector2(rect.X + resolvedRadius, rect.Y + rect.Height - resolvedRadius), resolvedRadius, color, textureId, clipRect, 12);
        draw.AddCircleFilled(new UiVector2(rect.X + rect.Width - resolvedRadius, rect.Y + rect.Height - resolvedRadius), resolvedRadius, color, textureId, clipRect, 12);
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

        return unitIndex == 0 ? $"{size:0} {units[unitIndex]}" : $"{size:0.##} {units[unitIndex]}";
    }

    private static void DrawHorizontalLine(UiDrawListBuilder draw, float y, float x, float width, UiColor color, UiTextureId textureId)
        => draw.AddRectFilled(new UiRect(x, y, width, 1f), color, textureId);
}
