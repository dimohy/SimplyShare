using System.Windows;

namespace SimplyShare.Views;

/// <summary>
/// 장문 텍스트 보기 윈도우 (스크롤 지원)
/// </summary>
public partial class TextViewerWindow : Window
{
    public TextViewerWindow(string title, string text)
    {
        InitializeComponent();
        Title = title;
        ContentBox.Text = text;
    }

    private void SelectAllButton_Click(object sender, RoutedEventArgs e)
    {
        ContentBox.SelectAll();
        ContentBox.Focus();
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        System.Windows.Clipboard.SetText(ContentBox.Text);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
