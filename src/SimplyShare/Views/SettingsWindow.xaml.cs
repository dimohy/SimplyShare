using System.Windows;
using SimplyShare.ViewModels;

namespace SimplyShare.Views;

/// <summary>
/// 설정 윈도우
/// </summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
