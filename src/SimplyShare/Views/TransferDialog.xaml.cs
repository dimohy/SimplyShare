using System.Windows;
using SimplyShare.Models;

namespace SimplyShare.Views;

/// <summary>
/// 전송 수락/거부 다이얼로그
/// </summary>
public partial class TransferDialog : Window
{
    public bool IsAccepted { get; private set; }

    public TransferDialog(TransferRequest request)
    {
        InitializeComponent();

        SenderText.Text = $"{request.SenderNickname}님의 전송 요청";

        ContentText.Text = request.Type switch
        {
            TransferType.Text => $"텍스트: {Truncate(request.TextContent ?? "", 200)}",
            TransferType.File => $"파일: {request.Files[0].RelativePath}",
            TransferType.Files => $"파일 {request.Files.Count}개",
            _ => "알 수 없는 전송"
        };

        SizeText.Text = request.Type is TransferType.Text
            ? ""
            : $"크기: {FormatSize(request.TotalSize)}";
    }

    private void AcceptButton_Click(object sender, RoutedEventArgs e)
    {
        IsAccepted = true;
        DialogResult = true;
        Close();
    }

    private void RejectButton_Click(object sender, RoutedEventArgs e)
    {
        IsAccepted = false;
        DialogResult = false;
        Close();
    }

    private static string Truncate(string text, int maxLength)
        => text.Length <= maxLength ? text : text[..maxLength] + "...";

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var size = (double)bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return $"{size:F1} {units[unit]}";
    }
}
