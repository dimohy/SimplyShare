using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SimplyShare.ViewModels;

namespace SimplyShare.Views;

/// <summary>
/// 채팅(DM) 윈도우 — 특정 장치와 텍스트/파일 공유
/// </summary>
public partial class ChatWindow : Window
{
    public ChatViewModel ViewModel { get; }
    private ScrollViewer? _scrollViewer;
    private bool _isAtBottom = true;
    private bool _forceClose;
    private bool _closingInProgress;
    private bool _isBeingRemoteControlled;
    private bool _isRemoteInputMode;
    private bool _autoMinimizedByRemoteMode;
    private RemoteCursorOverlayWindow? _remoteCursorOverlay;

    public bool IsRemoteUiInterferenceActive { get; private set; }
    public event Action<bool>? RemoteUiInterferenceChanged;

    public ChatWindow(ChatViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;

        ViewModel.RemoteCloseRequested += OnRemoteCloseRequested;
        ViewModel.RemoteControlStateChanged += OnRemoteControlStateChanged;
        ViewModel.RemoteInputModeChanged += OnRemoteInputModeChanged;
        ViewModel.RemoteCursorActivity += OnRemoteCursorActivity;
        Closed += (_, _) =>
        {
            ViewModel.RemoteCloseRequested -= OnRemoteCloseRequested;
            ViewModel.RemoteControlStateChanged -= OnRemoteControlStateChanged;
            ViewModel.RemoteInputModeChanged -= OnRemoteInputModeChanged;
            ViewModel.RemoteCursorActivity -= OnRemoteCursorActivity;

            _ = ViewModel.CleanupOnWindowClosedAsync();

            _remoteCursorOverlay?.Close();
            _remoteCursorOverlay = null;
        };

        // 메시지 추가 시 스마트 스크롤
        ((INotifyCollectionChanged)viewModel.Messages).CollectionChanged += (_, _) =>
        {
            if (_isAtBottom)
            {
                Dispatcher.InvokeAsync(() =>
                {
                    if (MessageList.Items.Count > 0)
                        MessageList.ScrollIntoView(MessageList.Items[^1]);
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
            else
            {
                // 맨 아래가 아니면 ↓ 버튼 표시
                Dispatcher.InvokeAsync(() =>
                    ScrollToBottomButton.Visibility = Visibility.Visible);
            }
        };

        // ListBox 로드 후 ScrollViewer 추적
        MessageList.Loaded += (_, _) =>
        {
            _scrollViewer = FindScrollViewer(MessageList);
            if (_scrollViewer is not null)
                _scrollViewer.ScrollChanged += ScrollViewer_ScrollChanged;
        };

        InputTextBox.Focus();
    }

    private void OnRemoteControlStateChanged(bool active)
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            _isBeingRemoteControlled = active;
            UpdateRemoteWindowInteractivity();
            if (active)
            {
                _remoteCursorOverlay ??= new RemoteCursorOverlayWindow();
                if (!_remoteCursorOverlay.IsVisible)
                    _remoteCursorOverlay.Show();
                _remoteCursorOverlay.UpdatePositionToCursor();
            }
            else
            {
                if (_remoteCursorOverlay is { IsVisible: true })
                    _remoteCursorOverlay.Hide();
            }
        });
    }

    private void OnRemoteInputModeChanged(bool active)
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            _isRemoteInputMode = active;
            UpdateRemoteWindowInteractivity();
        });
    }

    private void UpdateRemoteWindowInteractivity()
    {
        var shouldReduceUiInterference = _isBeingRemoteControlled || _isRemoteInputMode;

        if (IsRemoteUiInterferenceActive != shouldReduceUiInterference)
        {
            IsRemoteUiInterferenceActive = shouldReduceUiInterference;
            RemoteUiInterferenceChanged?.Invoke(shouldReduceUiInterference);
        }

        if (shouldReduceUiInterference)
        {
            if (WindowState is not WindowState.Minimized)
            {
                _autoMinimizedByRemoteMode = true;
                WindowState = WindowState.Minimized;
            }

            return;
        }

        if (_autoMinimizedByRemoteMode && WindowState is WindowState.Minimized)
        {
            _autoMinimizedByRemoteMode = false;
            WindowState = WindowState.Normal;
        }
    }

    private void OnRemoteCursorActivity()
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (_remoteCursorOverlay is { IsVisible: true })
            {
                _remoteCursorOverlay.UpdatePositionToCursor();
            }
        });
    }

    private async void Window_Closing(object sender, CancelEventArgs e)
    {
        if (App.IsForcedShutdown)
            return;

        if (_forceClose)
            return;

        if (_closingInProgress)
        {
            e.Cancel = true;
            return;
        }

        // 원격 모드(제어측) / 피제어 상태에서는 커서 숨김 + 입력 차단 때문에 확인창을 조작할 수 없어 먹통처럼 보일 수 있다.
        // 따라서 이 상태에서는 확인창 없이 종료 동기화를 진행한다.
        if (!_isBeingRemoteControlled && !ViewModel.IsRemoteInputModeActive)
        {
            var result = MessageBox.Show(
                "대화를 종료할까요?\n(상대방도 함께 종료됩니다)",
                "SimplyShare",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result is not MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        if (!ViewModel.IsChatConnected)
            return;

        // 원격 제어 상태에서 X를 누르면 먹통이 되기 쉬움.
        // 로컬 상태를 즉시 해제한 뒤, 피어에 RemoteControlStop + ChatCloseRequest를
        // 순차 전송(최대 2초)하고 나서 창을 닫는다.
        if (_isBeingRemoteControlled || ViewModel.IsRemoteInputModeActive)
        {
            e.Cancel = true;
            _closingInProgress = true;

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await ViewModel.NotifyPeerAndCloseAsync(cts.Token);
            }
            catch (Exception ex)
            {
                Core.AppLogger.Log("ChatWindow", $"피어 종료 알림 실패: {ex}");
            }
            finally
            {
                _closingInProgress = false;
                _forceClose = true;
                Close();
            }

            return;
        }

        // 닫기 동작이 시작되면 어떤 경우에도 입력이 먹통으로 남지 않도록 즉시 공유 훅을 내린다.
        // (상대 거부/타임아웃 등으로 창이 유지되더라도 사용자는 다시 켤 수 있다)
        ViewModel.IsInputSharingEnabled = false;
        ViewModel.IsClipboardSharingEnabled = false;

        // 창 닫힘 전에 원격 제어를 먼저 해제(상대에도 Stop 전송)해서 입력 먹통 상태를 방지
        ViewModel.PrepareForChatWindowClose();

        e.Cancel = true;
        _closingInProgress = true;

        try
        {
            var accepted = await ViewModel.RequestRemoteCloseAsync();
            if (!accepted)
            {
                ViewModel.StatusMessage = "상대방이 종료를 거부했습니다.";
                return;
            }

            _forceClose = true;
            Close();
        }
        catch (Exception ex)
        {
            Core.AppLogger.Log("ChatWindow", $"종료 동기화 실패: {ex}");
            ViewModel.StatusMessage = "종료 처리 실패";
        }
        finally
        {
            _closingInProgress = false;
        }
    }

    private async void OnRemoteCloseRequested()
    {
        if (_forceClose)
            return;

        // 원격 모드(제어측/피제어측) 상태에서는 먼저 로컬을 해제하여 입력 먹통 방지
        ViewModel.PrepareForChatWindowClose();
        ViewModel.IsInputSharingEnabled = false;
        ViewModel.IsClipboardSharingEnabled = false;

        // 피제어 상태에서는 로컬 입력 차단 때문에 확인창 조작이 어려울 수 있어 자동 처리
        var accepted = true;
        if (!_isBeingRemoteControlled && !ViewModel.IsRemoteInputModeActive)
        {
            var result = MessageBox.Show(
                $"{ViewModel.TargetDevice.Nickname}님이 대화를 종료하려 합니다.\n함께 종료할까요?",
                "SimplyShare",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            accepted = result is MessageBoxResult.Yes;
        }

        try
        {
            // 상대가 종료를 원할 때도, 먼저 원격 제어를 해제해서 입력 먹통 상태를 방지
            ViewModel.PrepareForChatWindowClose();
            await ViewModel.RespondToRemoteCloseAsync(accepted);
        }
        catch (Exception ex)
        {
            Core.AppLogger.Log("ChatWindow", $"종료 응답 전송 실패: {ex}");
        }

        if (accepted)
        {
            _forceClose = true;
            Close();
        }
    }

    private void ScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_scrollViewer is null) return;

        // 맨 아래 판정: 스크롤 가능 영역 끝에서 20px 이내
        _isAtBottom = _scrollViewer.VerticalOffset >= _scrollViewer.ScrollableHeight - 20;
        ScrollToBottomButton.Visibility = _isAtBottom ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ScrollToBottom_Click(object sender, RoutedEventArgs e)
    {
        if (MessageList.Items.Count > 0)
            MessageList.ScrollIntoView(MessageList.Items[^1]);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject obj)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(obj); i++)
        {
            var child = VisualTreeHelper.GetChild(obj, i);
            if (child is ScrollViewer sv) return sv;
            var found = FindScrollViewer(child);
            if (found is not null) return found;
        }
        return null;
    }

    // --- Ctrl+Enter 전송 ---

    private void InputTextBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is System.Windows.Input.Key.Enter && 
            System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control))
        {
            e.Handled = true;
            if (ViewModel.SendTextCommand.CanExecute(null))
            {
                ViewModel.SendTextCommand.Execute(null);
                InputTextBox.Focus();
            }
        }
    }

    // --- 전송 버튼 클릭 ---

    private void SendButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SendTextCommand.CanExecute(null))
        {
            ViewModel.SendTextCommand.Execute(null);
            InputTextBox.Focus();
        }
    }

    // --- 파일 첨부 버튼 ---

    private async void AttachFile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Multiselect = true,
                Title = "전송할 파일 선택"
            };

            if (dialog.ShowDialog() is true)
            {
                var paths = dialog.FileNames.ToList().AsReadOnly();
                await ViewModel.SendFilesCommand.ExecuteAsync(paths);
            }
        }
        catch (Exception ex)
        {
            Core.AppLogger.Log("ChatWindow", $"파일 첨부 전송 실패: {ex}");
        }
    }

    // --- 드래그 앤 드롭 ---

    private void Window_DragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            DropOverlay.Visibility = Visibility.Visible;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void Window_DragLeave(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        e.Handled = true;
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length is 0)
            return;

        try
        {
            await ViewModel.SendFilesCommand.ExecuteAsync(files.ToList().AsReadOnly());
        }
        catch (Exception ex)
        {
            Core.AppLogger.Log("ChatWindow", $"드롭 전송 실패: {ex}");
        }
    }

    // --- 수신 파일 클릭으로 열기 ---

    private void FileMessage_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        try
        {
            if (sender is FrameworkElement { Tag: string filePath } && System.IO.File.Exists(filePath))
            {
                Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            Core.AppLogger.Log("ChatWindow", $"수신 파일 열기 실패: {ex}");
        }
    }

    // --- 텍스트 말풍선 더블클릭 → 전체 텍스트 보기 ---

    private void TextBubble_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount is 2 && sender is FrameworkElement { Tag: string text })
        {
            var viewer = new TextViewerWindow("텍스트 보기", text) { Owner = this };
            viewer.ShowDialog();
            e.Handled = true;
        }
    }
}
