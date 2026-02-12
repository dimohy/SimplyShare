using System.Windows;
using SimplyShare.ViewModels;

namespace SimplyShare.Views;

/// <summary>
/// 최초 실행 닉네임 설정 윈도우
/// </summary>
public partial class SetupWindow : Window
{
    public SetupWindow(SetupViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        viewModel.SetupCompleted += () =>
        {
            DialogResult = true;
            Close();
        };

        NicknameBox.Focus();
    }
}
