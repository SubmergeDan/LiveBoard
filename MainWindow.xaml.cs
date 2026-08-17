using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Microsoft.Win32;

namespace LiveBoard
{
    public partial class MainWindow : Window
    {
        private const int WmNcLeftButtonDown = 0x00A1;
        private const int WmMouseWheel = 0x020A;
        private const int HitTestCaption = 2;
        private const int MaxRecordingReconnectAttempts = 3;

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr windowHandle, int message, IntPtr wordParameter, IntPtr longParameter);

        private readonly ConfigStore _store = new ConfigStore();
        private readonly BilibiliService _bilibili = new BilibiliService();
        private readonly RecordingService _recorder;
        private readonly M4sMuxService _muxService = new M4sMuxService();
        private readonly ObservableCollection<string> _activities = new ObservableCollection<string>();
        private readonly Dictionary<RoomConfig, RecordingSession> _recordingSessions = new Dictionary<RoomConfig, RecordingSession>();
        private readonly Dictionary<RoomConfig, CancellationTokenSource> _recordingCancellations = new Dictionary<RoomConfig, CancellationTokenSource>();
        private readonly Dictionary<RoomConfig, DateTime> _recordingStartedAt = new Dictionary<RoomConfig, DateTime>();
        private readonly HashSet<RoomConfig> _recordingStarting = new HashSet<RoomConfig>();
        private readonly Dictionary<RoomConfig, int> _recordingReconnectAttempts = new Dictionary<RoomConfig, int>();
        private readonly DispatcherTimer _monitorTimer;
        private readonly DispatcherTimer _recordingClockTimer;
        private AppConfig _config;
        private RoomConfig _selectedRoom;
        private bool _loadingControls;
        private bool _roomInputPlaceholder;
        private bool _remarkInputPlaceholder;
        private bool _titleBarPressed;
        private bool _titleBarDragging;
        private Point _titleBarPressPoint;
        private CancellationTokenSource _monitorCancellation;
        private bool _monitorTickRunning;
        private bool _inlineComboDropDownActive;
        private CancellationTokenSource _muxCancellation;
        private string _muxVideoPath;
        private string _muxAudioPath;
        private string _lastMuxOutputPath;
        private HwndSource _windowSource;

        public ObservableCollection<RoomConfig> Rooms { get; private set; }

        public MainWindow()
        {
            _recorder = new RecordingService(_bilibili);
            InitializeComponent();
            SourceInitialized += Window_OnSourceInitialized;
            Rooms = new ObservableCollection<RoomConfig>();
            DataContext = this;
            _roomInputPlaceholder = true;
            _remarkInputPlaceholder = true;

            PlatformCombo.Items.Add("抖音");
            PlatformCombo.Items.Add("Bilibili");
            PlatformCombo.SelectedItem = "抖音";
            RefreshAddQualityOptions("自动");
            foreach (var format in new[] { "MP4", "FLV", "TS" })
            {
                AddFormatCombo.Items.Add(format);
                RoomFormatCombo.Items.Add(format);
            }

            RoomItems.ItemsSource = Rooms;
            _monitorTimer = new DispatcherTimer();
            _monitorTimer.Tick += MonitorTimer_OnTick;
            _recordingClockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _recordingClockTimer.Tick += RecordingClockTimer_OnTick;
            _recordingClockTimer.Start();
            AddQualityCombo.SelectedIndex = 0;
            AddFormatCombo.SelectedIndex = 0;
            RoomQualityCombo.SelectedIndex = 0;
            RoomFormatCombo.SelectedIndex = 0;
            foreach (var mode in new[] { "关闭", "时间", "大小" })
                SegmentModeCombo.Items.Add(mode);
            SegmentModeCombo.SelectedIndex = 0;
            SegmentMinutesSlider.Value = 60;
            SegmentSizeBox.Text = "2048";
            InitializeMediaWorkspace();
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            _config = _store.Load();
            if (_store.LoadedFromBackup)
                AddActivity("已恢复直播间配置", "主配置为空或不可读取，已自动恢复上一份备份");
            else if (_store.LoadedFromLegacyLocation)
                AddActivity("已迁移直播间配置", "已从旧版 RecordingHelper 配置恢复");
            _bilibili.LoadProtectedCookies(_config.BilibiliCookieData);
            LoadMediaWorkspaceSettings();
            ReplaceRooms(_config.Rooms);
            OutputPathBox.Text = _config.OutputDirectory;
            IntervalText.Text = _config.CheckIntervalSeconds + " 秒";
            _loadingControls = true;
            PlatformCombo.SelectedItem = NormalizePlatform(_config.DefaultPlatform);
            RefreshAddQualityOptions(_config.DefaultQuality);
            UpdateAddQualityEditorVisibility();
            AddFormatCombo.SelectedItem = _config.DefaultFormat;
            IntervalSlider.Value = Math.Max(1, Math.Min(120, _config.CheckIntervalSeconds));
            _loadingControls = false;
            UpdateBilibiliAccountUi();
            RefreshAllRoomQualityOptions(false);
            AddActivity("工作区已加载", "已恢复 " + Rooms.Count + " 个直播间配置");
            UpdateRoomSummary();
            LastSavedText.Text = " · 已从本机恢复";
            _monitorCancellation = new CancellationTokenSource();
            SetMonitorInterval(_config.CheckIntervalSeconds);
            _monitorTimer.Start();
            await _bilibili.ValidateLoginAsync(_monitorCancellation.Token);
            UpdateBilibiliAccountUi();
            RefreshAddQualityOptions(_config.DefaultQuality);
            RefreshAllRoomQualityOptions(false);
            await CheckAllRoomsAsync();
        }

        private async void AddRoom_OnClick(object sender, RoutedEventArgs e)
        {
            var platform = NormalizePlatform(PlatformCombo.SelectedItem as string);
            var roomId = NormalizeRoomId(_roomInputPlaceholder ? string.Empty : RoomInput.Text, platform);
            if (string.IsNullOrWhiteSpace(roomId))
            {
                var hint = platform == "Bilibili"
                    ? "请输入 Bilibili 房间号，或粘贴 live.bilibili.com 直播链接。"
                    : "请输入 6-20 位抖音房间号，或粘贴 live.douyin.com、www.douyin.com/follow/live 直播链接。";
                MessageBox.Show(hint, "无法添加", MessageBoxButton.OK, MessageBoxImage.Information);
                RoomInput.Focus();
                return;
            }

            var quality = platform == "Bilibili" ? "自动" : (AddQualityCombo.SelectedItem as string ?? "自动");
            var format = AddFormatCombo.SelectedItem as string ?? "MP4";
            var existing = Rooms.FirstOrDefault(candidate => candidate.RoomId == roomId && NormalizePlatform(candidate.Platform) == platform);
            if (existing != null)
            {
                if (!_remarkInputPlaceholder && !string.IsNullOrWhiteSpace(RemarkInput.Text))
                    existing.Remark = RemarkInput.Text.Trim();
                existing.Quality = quality;
                existing.OutputFormat = format;
                existing.Platform = platform;
                PopulateRoomQualityOptions(existing, true);
                RefreshRoomCards();
                SelectRoom(existing);
                SaveConfig("已更新直播间配置");
                ResetInputPlaceholders();
                await CheckRoomStatusAsync(existing);
                return;
            }

            var newRoom = new RoomConfig
            {
                RoomId = roomId,
                Remark = _remarkInputPlaceholder ? string.Empty : RemarkInput.Text.Trim(),
                Quality = quality,
                OutputFormat = format,
                SegmentEnabled = false,
                SegmentMode = "关闭",
                SegmentMinutes = 60,
                SegmentSizeMb = 2048,
                Platform = platform
            };
            PopulateRoomQualityOptions(newRoom, true);
            Rooms.Add(newRoom);
            RefreshRoomCards();
            SelectRoom(newRoom);
            AddActivity("已添加直播间", newRoom.DisplayName + " · " + roomId);
            SaveConfig("已保存直播间配置");
            ResetInputPlaceholders();
            await CheckRoomStatusAsync(newRoom);
        }

        private void PlatformCombo_OnChanged(object sender, SelectionChangedEventArgs e)
        {
            if (AddQualityCombo == null)
                return;
            var platform = NormalizePlatform(PlatformCombo.SelectedItem as string);
            var preferredQuality = platform == "抖音" && _config != null ? _config.DefaultQuality : AddQualityCombo.SelectedItem as string;
            RefreshAddQualityOptions(preferredQuality);
            UpdateAddQualityEditorVisibility();
            UpdateBilibiliAccountUi();
            if (RoomInput != null)
                RoomInput.ToolTip = platform == "Bilibili" ? "输入 Bilibili 房间号或 live.bilibili.com 链接" : "输入抖音房间号或 live.douyin.com 链接";
            if (_loadingControls || _config == null)
                return;
            _config.DefaultPlatform = platform;
            SaveConfig(null);
        }

        private void BilibiliLogin_OnClick(object sender, RoutedEventArgs e)
        {
            var dialog = new BilibiliLoginWindow(_bilibili);
            dialog.Owner = this;
            var result = dialog.ShowDialog();
            if (result != true)
            {
                UpdateBilibiliAccountUi();
                return;
            }
            UpdateBilibiliAccountUi();
            RefreshAddQualityOptions(AddQualityCombo.SelectedItem as string);
            RefreshAllRoomQualityOptions(false);
            SaveConfig(null);
            AddActivity("Bilibili 登录成功", string.IsNullOrWhiteSpace(_bilibili.UserName) ? "已开放账号可用画质" : _bilibili.UserName + " · 已开放账号可用画质");
            RunStatusCheckAsync();
        }

        private void BilibiliLogout_OnClick(object sender, RoutedEventArgs e)
        {
            _bilibili.Logout();
            UpdateBilibiliAccountUi();
            RefreshAddQualityOptions(AddQualityCombo.SelectedItem as string);
            RefreshAllRoomQualityOptions(true);
            SaveConfig(null);
            AddActivity("Bilibili 已退出登录", "B站直播间已切换到游客画质范围");
            RunStatusCheckAsync();
        }

        private void WorkspaceButton_OnClick(object sender, RoutedEventArgs e)
        {
            var showMuxWorkspace = ReferenceEquals(sender, MuxWorkspaceButton);
            var showMediaWorkspace = ReferenceEquals(sender, MediaWorkspaceButton);
            var showLiveWorkspace = !showMuxWorkspace && !showMediaWorkspace;
            LiveWorkspaceButton.IsChecked = showLiveWorkspace;
            MuxWorkspaceButton.IsChecked = showMuxWorkspace;
            MediaWorkspaceButton.IsChecked = showMediaWorkspace;
            MainScrollViewer.Visibility = showLiveWorkspace ? Visibility.Visible : Visibility.Collapsed;
            MuxScrollViewer.Visibility = showMuxWorkspace ? Visibility.Visible : Visibility.Collapsed;
            MediaScrollViewer.Visibility = showMediaWorkspace ? Visibility.Visible : Visibility.Collapsed;
            LiveRightPanel.Visibility = showLiveWorkspace ? Visibility.Visible : Visibility.Collapsed;
            MuxRightPanel.Visibility = showMuxWorkspace ? Visibility.Visible : Visibility.Collapsed;
            MediaRightPanel.Visibility = showMediaWorkspace ? Visibility.Visible : Visibility.Collapsed;
            LiveWorkspaceSidebarInfo.Visibility = showLiveWorkspace ? Visibility.Visible : Visibility.Collapsed;
            MuxWorkspaceSidebarInfo.Visibility = showMuxWorkspace ? Visibility.Visible : Visibility.Collapsed;
            MediaWorkspaceSidebarInfo.Visibility = showMediaWorkspace ? Visibility.Visible : Visibility.Collapsed;
            if (showMuxWorkspace)
                MuxScrollViewer.ScrollToHome();
            else if (showMediaWorkspace)
                MediaScrollViewer.ScrollToHome();
            else
                MainScrollViewer.ScrollToHome();
        }

        private void RoomInput_OnGotFocus(object sender, RoutedEventArgs e)
        {
            if (!_roomInputPlaceholder)
                return;
            RoomInput.Clear();
            RoomInput.Foreground = FindBrush("InkBrush");
            _roomInputPlaceholder = false;
        }

        private void RoomInput_OnLostFocus(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(RoomInput.Text))
                return;
            RoomInput.Text = "房间号或直播网址";
            RoomInput.Foreground = FindBrush("MutedBrush");
            _roomInputPlaceholder = true;
        }

        private void RemarkInput_OnGotFocus(object sender, RoutedEventArgs e)
        {
            if (!_remarkInputPlaceholder)
                return;
            RemarkInput.Clear();
            RemarkInput.Foreground = FindBrush("InkBrush");
            _remarkInputPlaceholder = false;
        }

        private void RemarkInput_OnLostFocus(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(RemarkInput.Text))
                return;
            RemarkInput.Text = "备注（可选）";
            RemarkInput.Foreground = FindBrush("MutedBrush");
            _remarkInputPlaceholder = true;
        }

        private void ResetInputPlaceholders()
        {
            RoomInput.Clear();
            RemarkInput.Clear();
            RoomInput_OnLostFocus(null, null);
            RemarkInput_OnLostFocus(null, null);
        }

        private void RemoveRoom_OnClick(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var room = button == null ? null : button.Tag as RoomConfig;
            if (room == null)
                return;

            StopRoomRecording(room, false);

            Rooms.Remove(room);
            if (_selectedRoom == room)
            {
                _selectedRoom = null;
                ClearSelectedRoom();
            }
            RefreshRoomCards();
            AddActivity("已移除直播间", room.DisplayName + " · 配置仍可通过导出文件恢复");
            SaveConfig("已保存当前队列", Rooms.Count == 0);
        }

        private void RoomCard_OnClick(object sender, MouseButtonEventArgs e)
        {
            var border = sender as Border;
            var room = border == null ? null : border.DataContext as RoomConfig;
            if (room == null || !Rooms.Contains(room) || _inlineComboDropDownActive || IsInteractiveRoomCardSource(e.OriginalSource as DependencyObject))
                return;
            if (ReferenceEquals(_selectedRoom, room))
            {
                _selectedRoom = null;
                ClearSelectedRoom();
            }
            else
            {
                SelectRoom(room);
            }
            e.Handled = true;
        }

        private void RoomListScrollViewer_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            var roomList = sender as ScrollViewer;
            if (roomList == null || MainScrollViewer == null || e.Delta == 0)
                return;
            if (_inlineComboDropDownActive)
                return;

            const double edgeTolerance = 0.5;
            var movingUp = e.Delta > 0;
            var roomListCanScroll = movingUp
                ? roomList.VerticalOffset > edgeTolerance
                : roomList.VerticalOffset < roomList.ScrollableHeight - edgeTolerance;
            var target = roomListCanScroll ? roomList : MainScrollViewer;
            var distance = Math.Max(12.0, Math.Abs(e.Delta) * 0.45);
            var nextOffset = target.VerticalOffset + (movingUp ? -distance : distance);
            target.ScrollToVerticalOffset(Math.Max(0, Math.Min(target.ScrollableHeight, nextOffset)));
            e.Handled = true;
        }

        private bool IsInteractiveRoomCardSource(DependencyObject source)
        {
            var current = source;
            while (current != null)
            {
                if (current is ButtonBase || current is TextBoxBase || current is ComboBox || current is ComboBoxItem || current is Slider)
                    return true;
                try
                {
                    current = VisualTreeHelper.GetParent(current);
                }
                catch
                {
                    var content = current as FrameworkContentElement;
                    current = content == null ? null : content.Parent;
                }
            }
            return false;
        }

        private void SelectRoom(RoomConfig room)
        {
            foreach (var candidate in Rooms)
                candidate.IsSelected = ReferenceEquals(candidate, room);
            _selectedRoom = room;
            _loadingControls = true;
            SelectedRoomTitle.Text = room.DisplayName;
            SelectedRoomId.Text = room.RoomId + " · " + (string.IsNullOrWhiteSpace(room.OutputFormat) ? "MP4" : room.OutputFormat) + " 输出";
            RoomQualityCombo.Items.Clear();
            foreach (var quality in GetQualityOptions(room.Platform))
                RoomQualityCombo.Items.Add(quality);
            RoomQualityCombo.SelectedItem = string.IsNullOrWhiteSpace(room.Quality) ? "自动" : room.Quality;
            RoomFormatCombo.SelectedItem = string.IsNullOrWhiteSpace(room.OutputFormat) ? "MP4" : room.OutputFormat;
            SegmentModeCombo.SelectedItem = string.IsNullOrWhiteSpace(room.SegmentMode) ? (room.SegmentEnabled ? "时间" : "关闭") : room.SegmentMode;
            SegmentMinutesSlider.Value = Math.Max(1, Math.Min(180, room.SegmentMinutes <= 0 ? 60 : room.SegmentMinutes));
            SegmentSizeBox.Text = (room.SegmentSizeMb <= 0 ? 2048 : room.SegmentSizeMb).ToString();
            _loadingControls = false;
            UpdateSegmentEditorVisibility();
            UpdateSelectedRoomStatus();
            UpdateTasks();
        }

        private void ClearSelectedRoom()
        {
            foreach (var candidate in Rooms)
                candidate.IsSelected = false;
            SelectedRoomTitle.Text = "还没有选中的直播间";
            SelectedRoomId.Text = "添加一个房间后，这里会显示实时录制状态。";
            SelectedStatus.Text = "待命";
            StageText.Text = "等待直播间";
            UpdateSegmentEditorVisibility();
            UpdateTasks();
        }

        private void RoomQuality_OnChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingControls || _selectedRoom == null || RoomQualityCombo.SelectedItem == null)
                return;
            _selectedRoom.Quality = RoomQualityCombo.SelectedItem as string;
            RefreshRoomCards();
            SaveConfig("已更新画质偏好");
        }

        private void RoomFormat_OnChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingControls || _selectedRoom == null || RoomFormatCombo.SelectedItem == null)
                return;
            _selectedRoom.OutputFormat = RoomFormatCombo.SelectedItem as string;
            SelectedRoomId.Text = _selectedRoom.RoomId + " · " + _selectedRoom.OutputFormat + " 输出";
            RefreshRoomCards();
            SaveConfig("已更新输出格式");
        }

        private void SegmentMode_OnChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingControls || _selectedRoom == null || SegmentModeCombo.SelectedItem == null)
                return;
            _selectedRoom.SegmentMode = SegmentModeCombo.SelectedItem as string ?? "关闭";
            _selectedRoom.SegmentEnabled = _selectedRoom.SegmentMode == "时间";
            UpdateSegmentEditorVisibility();
            RefreshRoomCards();
            SaveConfig("已更新分片设置");
        }

        private void SegmentMinutesSlider_OnChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (SegmentMinutesText == null)
                return;
            var minutes = Math.Max(1, (int)Math.Round(e.NewValue));
            SegmentMinutesText.Text = minutes + " 分钟";
            if (_loadingControls || _selectedRoom == null)
                return;
            _selectedRoom.SegmentMinutes = minutes;
            RefreshRoomCards();
            SaveConfig("已更新时间分片");
        }

        private void SegmentSizeBox_OnLostFocus(object sender, RoutedEventArgs e)
        {
            if (_loadingControls || _selectedRoom == null)
                return;
            int size;
            if (!int.TryParse(SegmentSizeBox.Text, out size))
                size = 2048;
            size = Math.Max(1, Math.Min(102400, size));
            SegmentSizeBox.Text = size.ToString();
            _selectedRoom.SegmentSizeMb = size;
            RefreshRoomCards();
            SaveConfig("已更新大小分片");
        }

        private void InlineQuality_OnChanged(object sender, SelectionChangedEventArgs e)
        {
            var combo = sender as ComboBox;
            var room = combo == null ? null : combo.Tag as RoomConfig;
            if (room == null || _config == null || combo.SelectedItem == null)
                return;
            var quality = combo.SelectedItem as string;
            if (string.Equals(room.Quality, quality, StringComparison.OrdinalIgnoreCase))
                return;
            room.Quality = quality;
            SaveConfig("已更新画质偏好");
        }

        private void InlineCombo_OnDropDownOpened(object sender, EventArgs e)
        {
            _inlineComboDropDownActive = true;
        }

        private void InlineCombo_OnDropDownClosed(object sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
            {
                _inlineComboDropDownActive = false;
            }));
        }

        private void Window_OnSourceInitialized(object sender, EventArgs e)
        {
            _windowSource = PresentationSource.FromVisual(this) as HwndSource;
            if (_windowSource != null)
                _windowSource.AddHook(MainWindowWndProc);
        }

        private IntPtr MainWindowWndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == WmMouseWheel && _inlineComboDropDownActive)
                handled = true;
            return IntPtr.Zero;
        }

        private void InlineFormat_OnChanged(object sender, SelectionChangedEventArgs e)
        {
            var combo = sender as ComboBox;
            var room = combo == null ? null : combo.Tag as RoomConfig;
            if (room == null || _config == null || combo.SelectedValue == null)
                return;
            var format = combo.SelectedValue as string;
            if (string.Equals(room.OutputFormat, format, StringComparison.OrdinalIgnoreCase))
                return;
            room.OutputFormat = format;
            SaveConfig("已更新输出格式");
        }

        private void InlineSegmentMode_OnChanged(object sender, SelectionChangedEventArgs e)
        {
            var combo = sender as ComboBox;
            var room = combo == null ? null : combo.Tag as RoomConfig;
            if (room == null || _config == null || combo.SelectedValue == null)
                return;
            var mode = combo.SelectedValue as string ?? "关闭";
            if (string.Equals(room.SegmentMode, mode, StringComparison.OrdinalIgnoreCase))
                return;
            room.SegmentMode = mode;
            room.SegmentEnabled = room.SegmentMode == "时间";
            SaveConfig("已更新分片设置");
        }

        private void InlineSegmentMinutes_OnChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            var slider = sender as Slider;
            var room = slider == null ? null : slider.Tag as RoomConfig;
            if (room == null || _config == null)
                return;
            var minutes = Math.Max(1, Math.Min(180, (int)Math.Round(e.NewValue)));
            if (room.SegmentMinutes == minutes)
                return;
            room.SegmentMinutes = minutes;
            SaveConfig("已更新时间分片");
        }

        private void InlineSegmentSize_OnLostFocus(object sender, RoutedEventArgs e)
        {
            var box = sender as TextBox;
            var room = box == null ? null : box.Tag as RoomConfig;
            if (room == null || _config == null)
                return;
            int size;
            if (!int.TryParse(box.Text, out size))
                size = 2048;
            room.SegmentSizeMb = Math.Max(1, Math.Min(102400, size));
            box.Text = room.SegmentSizeMb.ToString();
            SaveConfig("已更新大小分片");
        }

        private void UpdateSegmentEditorVisibility()
        {
            var mode = SegmentModeCombo.SelectedItem as string ?? "关闭";
            SegmentMinutesPanel.Visibility = mode == "时间" ? Visibility.Visible : Visibility.Collapsed;
            SegmentSizePanel.Visibility = mode == "大小" ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void StartRoomRecording_OnClick(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var room = button == null ? null : button.Tag as RoomConfig;
            if (room == null)
                return;
            await CheckRoomStatusAsync(room);
            if (room.IsRecording || _recordingStarting.Contains(room))
                return;
            if (room.LiveStatus != "开播")
            {
                AddActivity("未开始录制", room.DisplayName + " · " + room.LiveStatus);
                return;
            }
            await StartRoomRecordingAsync(room, 1);
        }

        private async void AutoRecordToggle_OnClick(object sender, RoutedEventArgs e)
        {
            var toggle = sender as CheckBox;
            var room = toggle == null ? null : toggle.Tag as RoomConfig;
            if (room == null)
                return;
            room.AutoRecordEnabled = toggle.IsChecked == true;
            room.AutoRecordSuppressed = false;
            SaveConfig(null);
            if (room.AutoRecordEnabled && room.LiveStatus == "开播" && !room.IsRecording)
            {
                AddActivity("自动录制已触发", room.DisplayName + " · " + room.RoomId);
                await StartRoomRecordingAsync(room, 1);
            }
        }

        private void StopRoomRecording_OnClick(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var room = button == null ? null : button.Tag as RoomConfig;
            if (room != null)
            {
                if (room.AutoRecordEnabled)
                {
                    room.AutoRecordSuppressed = true;
                    AddActivity("自动录制本次暂停", room.DisplayName + " · 重启工作台或重新开关后恢复");
                }
                StopRoomRecording(room, true);
            }
        }

        private async Task StartRoomRecordingAsync(RoomConfig room, int segmentIndex, bool reconnecting = false)
        {
            if (room == null || _recordingSessions.ContainsKey(room) || _recordingStarting.Contains(room))
                return;
            if (!reconnecting)
                _recordingReconnectAttempts.Remove(room);

            var ffmpegPath = FindFfmpegPath();
            if (ffmpegPath == null)
            {
                room.RecordingStatus = "引擎不可用";
                RefreshRoomCards();
                MessageBox.Show("内置录制引擎释放失败，请重启程序后重试。", "录制引擎不可用", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var cancellation = new CancellationTokenSource();
            _recordingCancellations[room] = cancellation;
            _recordingStarting.Add(room);
            room.IsRecording = true;
            room.RecordingStatus = "检测中";
            UpdateSelectedRoomStatus();
            RefreshRoomCards();
            AddActivity("开始检测直播流", room.DisplayName + " · " + room.RoomId);
            UpdateTasks();

            try
            {
                var session = await _recorder.StartAsync(room, _config.OutputDirectory, ffmpegPath, cancellation.Token, segmentIndex);
                if (cancellation.IsCancellationRequested)
                {
                    session.Stop();
                    session.Dispose();
                    return;
                }
                _recordingSessions[room] = session;
                session.Process.Exited += delegate { RecordingProcess_Exited(room, session); };
                if (session.Process.HasExited)
                    RecordingProcess_Exited(room, session);
                room.RecordingStatus = "录制中";
                room.LiveStatus = "直播中";
                if (!_recordingStartedAt.ContainsKey(room))
                    _recordingStartedAt[room] = DateTime.Now;
                UpdateRecordingElapsed(room);
                AddActivity("已开始真实录制", Path.GetFileName(session.OutputPath));
                ResetRecordingReconnectCounterWhenStable(room, session);
            }
            catch (OperationCanceledException)
            {
                room.RecordingStatus = "已取消";
            }
            catch (Exception ex)
            {
                if (!QueueRecordingReconnect(room, segmentIndex, ex.Message))
                {
                    _recordingReconnectAttempts.Remove(room);
                    room.RecordingStatus = "启动失败";
                    room.LiveStatusDetail = ex.Message;
                    AddActivity("录制启动失败", room.DisplayName + " · " + ex.Message);
                }
            }
            finally
            {
                _recordingStarting.Remove(room);
                if (!_recordingSessions.ContainsKey(room))
                {
                    room.IsRecording = _recordingReconnectAttempts.ContainsKey(room);
                    CancellationTokenSource pending;
                    if (_recordingCancellations.TryGetValue(room, out pending))
                    {
                        _recordingCancellations.Remove(room);
                        pending.Dispose();
                    }
                }
                UpdateSelectedRoomStatus();
                RefreshRoomCards();
                UpdateTasks();
            }
        }

        private void StopRoomRecording(RoomConfig room, bool addActivity)
        {
            if (room == null)
                return;
            CancellationTokenSource cancellation;
            if (_recordingCancellations.TryGetValue(room, out cancellation))
            {
                _recordingCancellations.Remove(room);
                cancellation.Cancel();
                cancellation.Dispose();
            }
            RecordingSession session;
            if (_recordingSessions.TryGetValue(room, out session))
            {
                _recordingSessions.Remove(room);
                room.RecordingStatus = "正在封装 MP4";
                UpdateSelectedRoomStatus();
                RefreshRoomCards();
                UpdateTasks();
                var finalized = session.Stop();
                session.Dispose();
                if (finalized)
                {
                    AddActivity("录制文件已封装", Path.GetFileName(session.OutputPath));
                }
                else
                {
                    AddActivity("录制停止超时", "FFmpeg 未能正常写完文件尾部，已强制停止：" + Path.GetFileName(session.OutputPath));
                }
            }
            _recordingStarting.Remove(room);
            _recordingReconnectAttempts.Remove(room);
            _recordingStartedAt.Remove(room);
            room.IsRecording = false;
            room.RecordingStatus = "待命";
            room.RecordingElapsed = string.Empty;
            if (addActivity)
                AddActivity("已停止任务", room.DisplayName);
            UpdateSelectedRoomStatus();
            RefreshRoomCards();
            UpdateTasks();
        }

        private void RecordingProcess_Exited(RoomConfig room, RecordingSession session)
        {
            Dispatcher.BeginInvoke(new Action(delegate
            {
                RecordingSession current;
                if (!_recordingSessions.TryGetValue(room, out current) || !ReferenceEquals(session, current))
                    return;
                _recordingSessions.Remove(room);
                CancellationTokenSource cancellation;
                if (_recordingCancellations.TryGetValue(room, out cancellation))
                {
                    _recordingCancellations.Remove(room);
                    cancellation.Dispose();
                }
                if (!session.StopRequested && session.ReachedSizeLimit && (room.SegmentMode == "大小"))
                {
                    room.RecordingStatus = "切换分片";
                    session.Dispose();
                    RefreshRoomCards();
                    RestartRecordingAfterRotation(room, session.SegmentIndex + 1);
                    return;
                }
                var stopRequested = session.StopRequested;
                var errorText = session.ErrorText;
                var failedOutputPath = session.OutputPath;
                var retrySegmentIndex = session.SegmentIndex;
                session.Dispose();
                if (!stopRequested && QueueRecordingReconnect(room, retrySegmentIndex, errorText))
                {
                    DeleteSmallFailedOutput(failedOutputPath);
                    return;
                }
                _recordingReconnectAttempts.Remove(room);
                room.IsRecording = false;
                room.RecordingStatus = stopRequested ? "待命" : "已结束";
                _recordingStartedAt.Remove(room);
                room.RecordingElapsed = string.Empty;
                AddActivity("录制进程已结束", string.IsNullOrWhiteSpace(errorText) ? Path.GetFileName(failedOutputPath) : errorText);
                UpdateSelectedRoomStatus();
                RefreshRoomCards();
                UpdateTasks();
            }));
        }

        private bool QueueRecordingReconnect(RoomConfig room, int segmentIndex, string errorText)
        {
            if (room == null || !IsRetryableRecordingError(errorText))
                return false;
            int attempts;
            if (!_recordingReconnectAttempts.TryGetValue(room, out attempts))
                attempts = 0;
            if (attempts >= MaxRecordingReconnectAttempts)
            {
                _recordingReconnectAttempts.Remove(room);
                AddActivity("录制重连已停止", room.DisplayName + " · 已达到 " + MaxRecordingReconnectAttempts + " 次上限");
                return false;
            }

            attempts++;
            _recordingReconnectAttempts[room] = attempts;
            room.IsRecording = true;
            room.RecordingStatus = "重连中 " + attempts + "/" + MaxRecordingReconnectAttempts;
            room.LiveStatusDetail = ShortenStatus(errorText);
            AddActivity("录制连接中断，正在重连", room.DisplayName + " · 第 " + attempts + " 次重新获取直播流");
            UpdateSelectedRoomStatus();
            RefreshRoomCards();
            RestartRecordingAfterFailure(room, segmentIndex);
            return true;
        }

        private bool IsRetryableRecordingError(string errorText)
        {
            if (string.IsNullOrWhiteSpace(errorText))
                return false;
            var value = errorText.ToLowerInvariant();
            return value.Contains("403 forbidden") ||
                   value.Contains("401 unauthorized") ||
                   value.Contains("http error 5") ||
                   value.Contains("connection") ||
                   value.Contains("timed out") ||
                   value.Contains("i/o error") ||
                   value.Contains("server returned") ||
                   value.Contains("end of file");
        }

        private async void ResetRecordingReconnectCounterWhenStable(RoomConfig room, RecordingSession session)
        {
            await Task.Delay(20000);
            RecordingSession current;
            if (_recordingSessions.TryGetValue(room, out current) && ReferenceEquals(current, session))
                _recordingReconnectAttempts.Remove(room);
        }

        private void DeleteSmallFailedOutput(string outputPath)
        {
            if (string.IsNullOrWhiteSpace(outputPath) || outputPath.IndexOf('%') >= 0)
                return;
            try
            {
                var file = new FileInfo(outputPath);
                if (file.Exists && file.Length < 128 * 1024)
                    file.Delete();
            }
            catch
            {
            }
        }

        private string FindFfmpegPath()
        {
            try
            {
                return RecordingService.EnsureBundledFfmpeg();
            }
            catch
            {
                return null;
            }
        }

        private void ChooseFolder_OnClick(object sender, RoutedEventArgs e)
        {
            using (var dialog = new Forms.FolderBrowserDialog())
            {
                dialog.Description = "选择录制文件保存位置";
                dialog.SelectedPath = Directory.Exists(OutputPathBox.Text) ? OutputPathBox.Text : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
                if (dialog.ShowDialog() == Forms.DialogResult.OK)
                {
                    OutputPathBox.Text = dialog.SelectedPath;
                    SaveConfig("已更新保存路径");
                }
            }
        }

        private void OpenRecordingFolder_OnClick(object sender, RoutedEventArgs e)
        {
            var path = NormalizeOutputDirectory(OutputPathBox.Text);
            OutputPathBox.Text = path;
            if (_config != null)
                SaveConfig(null);
            OpenFolderInExplorer(path, "录制文件夹");
        }

        private void OpenConfigFolder_OnClick(object sender, RoutedEventArgs e)
        {
            OpenFolderInExplorer(_store.ConfigDirectory, "配置文件夹");
        }

        private void OpenFolderInExplorer(string path, string label)
        {
            try
            {
                Directory.CreateDirectory(path);
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "\"" + path + "\"",
                    UseShellExecute = true
                });
                AddActivity("已打开" + label, path);
            }
            catch (Exception ex)
            {
                MessageBox.Show("无法打开" + label + "：" + ex.Message, "打开失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ChooseMuxVideo_OnClick(object sender, RoutedEventArgs e)
        {
            ChooseMuxInputFile(true);
        }

        private void ChooseMuxAudio_OnClick(object sender, RoutedEventArgs e)
        {
            ChooseMuxInputFile(false);
        }

        private void ChooseMuxInputFile(bool video)
        {
            var currentPath = video ? _muxVideoPath : _muxAudioPath;
            var dialog = new OpenFileDialog
            {
                Filter = "B站缓存文件 (*.m4s)|*.m4s|所有文件 (*.*)|*.*",
                Title = video ? "选择视频 M4S" : "选择音频 M4S",
                CheckFileExists = true,
                Multiselect = false
            };
            if (!string.IsNullOrWhiteSpace(currentPath) && File.Exists(currentPath))
                dialog.InitialDirectory = Path.GetDirectoryName(currentPath);
            if (dialog.ShowDialog() == true)
                SetMuxInputFile(dialog.FileName, video);
        }

        private void MuxFile_OnDragOver(object sender, DragEventArgs e)
        {
            e.Effects = DragDropEffects.None;
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files != null && files.Length == 1 && string.Equals(Path.GetExtension(files[0]), ".m4s", StringComparison.OrdinalIgnoreCase))
                    e.Effects = DragDropEffects.Copy;
            }
            e.Handled = true;
        }

        private void MuxFile_OnDrop(object sender, DragEventArgs e)
        {
            var target = sender as FrameworkElement;
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (target == null || files == null || files.Length != 1)
                return;
            SetMuxInputFile(files[0], string.Equals(target.Tag as string, "Video", StringComparison.OrdinalIgnoreCase));
            e.Handled = true;
        }

        private void SetMuxInputFile(string path, bool video)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || !string.Equals(Path.GetExtension(path), ".m4s", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("请选择有效的 .m4s 文件。", "文件无效", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            path = Path.GetFullPath(path);
            var fileName = Path.GetFileName(path);
            if (video)
            {
                _muxVideoPath = path;
                MuxVideoFileNameText.Text = fileName;
                MuxVideoPathText.Text = Path.GetDirectoryName(path);
                MuxVideoFileNameText.ToolTip = path;
                MuxTaskVideoText.Text = fileName;
                MuxTaskVideoText.ToolTip = path;
                var outputPath = Path.Combine(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path) + "_有声.mp4");
                MuxOutputPathBox.Text = outputPath;
                MuxTaskOutputText.Text = Path.GetFileName(outputPath);
                MuxTaskOutputText.ToolTip = outputPath;
            }
            else
            {
                _muxAudioPath = path;
                MuxAudioFileNameText.Text = fileName;
                MuxAudioPathText.Text = Path.GetDirectoryName(path);
                MuxAudioFileNameText.ToolTip = path;
                MuxTaskAudioText.Text = fileName;
                MuxTaskAudioText.ToolTip = path;
            }
            UpdateMuxReadyState();
        }

        private void ChooseMuxOutput_OnClick(object sender, RoutedEventArgs e)
        {
            var suggestedPath = GetMuxOutputPath();
            var dialog = new SaveFileDialog
            {
                Filter = "MP4 视频 (*.mp4)|*.mp4",
                DefaultExt = ".mp4",
                AddExtension = true,
                OverwritePrompt = true,
                Title = "选择导出位置",
                FileName = string.IsNullOrWhiteSpace(suggestedPath) ? "B站缓存_有声.mp4" : Path.GetFileName(suggestedPath)
            };
            if (!string.IsNullOrWhiteSpace(suggestedPath))
                dialog.InitialDirectory = Path.GetDirectoryName(suggestedPath);
            if (dialog.ShowDialog() != true)
                return;
            MuxOutputPathBox.Text = dialog.FileName;
            MuxTaskOutputText.Text = Path.GetFileName(dialog.FileName);
            MuxTaskOutputText.ToolTip = dialog.FileName;
            UpdateMuxReadyState();
        }

        private async void StartMux_OnClick(object sender, RoutedEventArgs e)
        {
            if (_muxCancellation != null)
                return;
            if (!File.Exists(_muxVideoPath) || !File.Exists(_muxAudioPath))
            {
                MessageBox.Show("请先选择视频和音频两个 M4S 文件。", "文件未就绪", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (string.Equals(_muxVideoPath, _muxAudioPath, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("视频和音频不能使用同一个文件。", "文件无效", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string outputPath;
            try
            {
                outputPath = GetMuxOutputPath();
                if (string.IsNullOrWhiteSpace(outputPath))
                    throw new InvalidOperationException("请选择导出位置。" );
                if (!string.Equals(Path.GetExtension(outputPath), ".mp4", StringComparison.OrdinalIgnoreCase))
                    outputPath += ".mp4";
                outputPath = Path.GetFullPath(outputPath);
                if (string.Equals(outputPath, _muxVideoPath, StringComparison.OrdinalIgnoreCase) || string.Equals(outputPath, _muxAudioPath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("导出文件不能覆盖输入文件。" );
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "导出路径无效", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (File.Exists(outputPath) && MessageBox.Show("导出文件已存在，是否覆盖？", "确认覆盖", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            var ffmpegPath = FindFfmpegPath();
            if (ffmpegPath == null)
            {
                MessageBox.Show("内置 FFmpeg 无法使用。", "导出失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            MuxOutputPathBox.Text = outputPath;
            MuxTaskOutputText.Text = Path.GetFileName(outputPath);
            MuxTaskOutputText.ToolTip = outputPath;
            var cancellation = new CancellationTokenSource();
            _muxCancellation = cancellation;
            StartMuxButton.IsEnabled = false;
            CancelMuxButton.Visibility = Visibility.Visible;
            SetMuxStatus("正在无损合成", "视频和音频轨道正在写入 MP4", "运行中", "…", FindBrush("BrightGreenBrush"));

            try
            {
                var result = await _muxService.MuxAsync(ffmpegPath, _muxVideoPath, _muxAudioPath, outputPath, cancellation.Token);
                if (result.Success)
                {
                    _lastMuxOutputPath = result.OutputPath;
                    var file = new FileInfo(result.OutputPath);
                    SetMuxStatus("导出完成", Path.GetFileName(result.OutputPath), "已完成", FormatFileSize(file.Length), FindBrush("BrightGreenBrush"));
                    AddActivity("B站缓存导出完成", Path.GetFileName(result.OutputPath));
                }
                else if (result.Cancelled)
                {
                    SetMuxStatus("已取消导出", "未保留不完整的输出文件", "已取消", "待命", FindBrush("LineBrush"));
                }
                else
                {
                    SetMuxStatus("导出失败", ShortenStatus(result.ErrorText), "失败", "错误", FindBrush("OrangeBrush"));
                    AddActivity("B站缓存导出失败", ShortenStatus(result.ErrorText));
                }
            }
            catch (OperationCanceledException)
            {
                SetMuxStatus("已取消导出", "未保留不完整的输出文件", "已取消", "待命", FindBrush("LineBrush"));
            }
            catch (Exception ex)
            {
                SetMuxStatus("导出失败", ShortenStatus(ex.Message), "失败", "错误", FindBrush("OrangeBrush"));
                AddActivity("B站缓存导出失败", ShortenStatus(ex.Message));
            }
            finally
            {
                if (ReferenceEquals(_muxCancellation, cancellation))
                    _muxCancellation = null;
                cancellation.Dispose();
                CancelMuxButton.Visibility = Visibility.Collapsed;
                StartMuxButton.IsEnabled = File.Exists(_muxVideoPath) && File.Exists(_muxAudioPath);
            }
        }

        private void CancelMux_OnClick(object sender, RoutedEventArgs e)
        {
            if (_muxCancellation == null)
                return;
            MuxStatusText.Text = "正在取消";
            MuxTaskStateText.Text = "取消中";
            MuxSidebarStateText.Text = "正在取消";
            _muxCancellation.Cancel();
        }

        private void OpenMuxOutputFolder_OnClick(object sender, RoutedEventArgs e)
        {
            var outputPath = !string.IsNullOrWhiteSpace(_lastMuxOutputPath) ? _lastMuxOutputPath : GetMuxOutputPath();
            if (string.IsNullOrWhiteSpace(outputPath) || string.IsNullOrWhiteSpace(Path.GetDirectoryName(outputPath)))
            {
                MessageBox.Show("请先设置导出文件位置。", "没有导出位置", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            OpenFolderInExplorer(Path.GetDirectoryName(outputPath), "导出文件夹");
        }

        private string GetMuxOutputPath()
        {
            var value = MuxOutputPathBox.Text == null ? string.Empty : MuxOutputPathBox.Text.Trim();
            if (!string.IsNullOrWhiteSpace(value))
                return value;
            if (string.IsNullOrWhiteSpace(_muxVideoPath))
                return null;
            return Path.Combine(Path.GetDirectoryName(_muxVideoPath), Path.GetFileNameWithoutExtension(_muxVideoPath) + "_有声.mp4");
        }

        private void UpdateMuxReadyState()
        {
            if (_muxCancellation != null)
                return;
            var ready = File.Exists(_muxVideoPath) && File.Exists(_muxAudioPath);
            StartMuxButton.IsEnabled = ready;
            if (ready)
                SetMuxStatus("文件已就绪", "可以开始无损合成", "已就绪", "待命", FindBrush("MintBrush"));
            else
                SetMuxStatus("等待选择缓存文件", "", "待命", "待命", FindBrush("LineBrush"));
        }

        private void SetMuxStatus(string title, string detail, string taskState, string sizeText, Brush dotBrush)
        {
            MuxStatusText.Text = title;
            MuxStatusDetailText.Text = detail ?? string.Empty;
            MuxTaskStateText.Text = taskState;
            MuxOutputSizeText.Text = sizeText;
            MuxStatusDot.Background = dotBrush;
            MuxSidebarDot.Background = dotBrush;
            MuxSidebarStateText.Text = title;
            var isWaiting = taskState == "待命" && title.StartsWith("等待", StringComparison.Ordinal);
            var isWorking = taskState == "运行中";
            MuxStatusIdleIcon.Visibility = isWaiting ? Visibility.Visible : Visibility.Collapsed;
            MuxStatusIconText.Visibility = isWaiting ? Visibility.Collapsed : Visibility.Visible;
            MuxStatusIconText.Text = isWorking ? "···" : (taskState == "已完成" ? "✓" : (taskState == "失败" ? "!" : "—"));
            MuxStatusIconText.FontSize = isWorking ? 12 : (taskState == "已完成" || taskState == "失败" ? 16 : 14);
            MuxStatusIconText.LineHeight = isWorking ? 12 : (taskState == "已完成" || taskState == "失败" ? 16 : 14);
        }

        private string FormatFileSize(long bytes)
        {
            if (bytes >= 1024L * 1024L * 1024L)
                return (bytes / (1024d * 1024d * 1024d)).ToString("0.00") + " GB";
            if (bytes >= 1024L * 1024L)
                return (bytes / (1024d * 1024d)).ToString("0.0") + " MB";
            return (bytes / 1024d).ToString("0") + " KB";
        }

        private async void ImportConfig_OnClick(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "LiveBoard 配置 (*.json)|*.json|所有文件 (*.*)|*.*",
                Title = "导入直播间配置"
            };
            if (dialog.ShowDialog() != true)
                return;

            try
            {
                var imported = _store.LoadFrom(dialog.FileName);
                _config = imported;
                _bilibili.LoadProtectedCookies(imported.BilibiliCookieData);
                ReplaceRooms(imported.Rooms);
                OutputPathBox.Text = imported.OutputDirectory;
                LoadMediaWorkspaceSettings();
                IntervalText.Text = imported.CheckIntervalSeconds + " 秒";
                _loadingControls = true;
                PlatformCombo.SelectedItem = NormalizePlatform(imported.DefaultPlatform);
                RefreshAddQualityOptions(imported.DefaultQuality);
                AddFormatCombo.SelectedItem = imported.DefaultFormat;
                IntervalSlider.Value = Math.Max(1, Math.Min(120, imported.CheckIntervalSeconds));
                _loadingControls = false;
                SetMonitorInterval(imported.CheckIntervalSeconds);
                UpdateBilibiliAccountUi();
                var token = _monitorCancellation == null ? CancellationToken.None : _monitorCancellation.Token;
                await _bilibili.ValidateLoginAsync(token);
                UpdateBilibiliAccountUi();
                RefreshAddQualityOptions(imported.DefaultQuality);
                RefreshAllRoomQualityOptions(false);
                SaveConfig("已导入直播间配置", Rooms.Count == 0);
                AddActivity("已导入配置", Path.GetFileName(dialog.FileName) + " · " + Rooms.Count + " 个直播间");
                RunStatusCheckAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show("配置文件无法读取：" + ex.Message, "导入失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ExportConfig_OnClick(object sender, RoutedEventArgs e)
        {
            SaveConfig(null);
            var dialog = new SaveFileDialog
            {
                Filter = "LiveBoard 配置 (*.json)|*.json",
                FileName = "liveboard-config.json",
                Title = "导出直播间配置"
            };
            if (dialog.ShowDialog() != true)
                return;

            try
            {
                _store.SaveTo(_config, dialog.FileName);
                AddActivity("已导出配置", Path.GetFileName(dialog.FileName));
            }
            catch (Exception ex)
            {
                MessageBox.Show("配置文件无法写入：" + ex.Message, "导出失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void TopNav_OnClick(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button == null)
                return;

            SetActiveNav(button, OverviewNav, RoomsNav, RecordNav, LogNav);
            var target = button.Tag as string ?? button.Content as string ?? "视图";
            AddActivity("已切换视图", target);

            if (target == "直播间")
            {
                MainScrollViewer.ScrollToHome();
                RoomInput.Focus();
            }
            else if (target == "录制" && _selectedRoom != null)
            {
                SelectedRoomTitle.BringIntoView();
            }
            else if (target == "日志")
            {
                ActivityItems.BringIntoView();
            }
        }

        private void SideNav_OnClick(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button == null)
                return;

            SetActiveNav(button, SideOverviewNav, SideRoomsNav, SideHistoryNav, SideSettingsNav);
            var target = button.Tag as string ?? button.Content as string ?? "导航";
            AddActivity("已切换导航", target);

            if (target == "录制概览")
            {
                MainScrollViewer.ScrollToHome();
            }
            else if (target == "直播间队列")
            {
                RoomItems.BringIntoView();
            }
            else if (target == "运行任务")
            {
                TaskItems.BringIntoView();
            }
            else if (target == "设置")
            {
                OutputPathBox.BringIntoView();
                OutputPathBox.Focus();
                OutputPathBox.SelectAll();
            }
        }

        private void SetActiveNav(Button active, params Button[] buttons)
        {
            foreach (var button in buttons)
            {
                button.Background = Brushes.Transparent;
                button.Foreground = FindBrush("MutedBrush");
            }

            active.Background = new SolidColorBrush(Color.FromRgb(229, 231, 225));
            active.Foreground = FindBrush("InkBrush");
        }

        private void Search_OnClick(object sender, RoutedEventArgs e)
        {
            MainScrollViewer.ScrollToHome();
            RoomInput.Focus();
            RoomInput.SelectAll();
            AddActivity("已聚焦房间输入", "请输入房间号或直播网址");
        }

        private void More_OnClick(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu();
            menu.FontFamily = FontFamily;
            var openFolder = new MenuItem { Header = "打开配置文件夹" };
            openFolder.Click += delegate
            {
                try
                {
                    Directory.CreateDirectory(_store.ConfigDirectory);
                    Process.Start(new ProcessStartInfo("explorer.exe", "\"" + _store.ConfigDirectory + "\""));
                    AddActivity("已打开配置文件夹", _store.ConfigDirectory);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("无法打开配置文件夹：" + ex.Message, "打开失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            };

            var reload = new MenuItem { Header = "重新载入已保存配置" };
            reload.Click += delegate
            {
                ReloadSavedConfigAsync();
            };

            var about = new MenuItem { Header = "关于 LiveBoard" };
            about.Click += delegate
            {
                MessageBox.Show("LiveBoard\n抖音 / Bilibili 直播录制工作台\n单文件版：配置、监控与录制任务", "关于", MessageBoxButton.OK, MessageBoxImage.Information);
            };

            menu.Items.Add(openFolder);
            menu.Items.Add(reload);
            menu.Items.Add(new Separator());
            menu.Items.Add(about);
            menu.PlacementTarget = sender as UIElement;
            menu.IsOpen = true;
        }

        private async void ReloadSavedConfigAsync()
        {
            _config = _store.Load();
            _bilibili.LoadProtectedCookies(_config.BilibiliCookieData);
            ReplaceRooms(_config.Rooms);
            OutputPathBox.Text = _config.OutputDirectory;
            IntervalText.Text = _config.CheckIntervalSeconds + " 秒";
            _loadingControls = true;
            PlatformCombo.SelectedItem = NormalizePlatform(_config.DefaultPlatform);
            RefreshAddQualityOptions(_config.DefaultQuality);
            AddFormatCombo.SelectedItem = _config.DefaultFormat;
            IntervalSlider.Value = Math.Max(1, Math.Min(120, _config.CheckIntervalSeconds));
            _loadingControls = false;
            SetMonitorInterval(_config.CheckIntervalSeconds);
            UpdateBilibiliAccountUi();
            var token = _monitorCancellation == null ? CancellationToken.None : _monitorCancellation.Token;
            await _bilibili.ValidateLoginAsync(token);
            UpdateBilibiliAccountUi();
            RefreshAddQualityOptions(_config.DefaultQuality);
            RefreshAllRoomQualityOptions(false);
            AddActivity("已重新载入配置", "已恢复 " + Rooms.Count + " 个直播间");
            LastSavedText.Text = " · 已从本机恢复";
            RunStatusCheckAsync();
        }

        private void OutputPathBox_OnLostFocus(object sender, RoutedEventArgs e)
        {
            if (_config == null)
                return;

            var path = OutputPathBox.Text == null ? string.Empty : OutputPathBox.Text.Trim();
            if (!string.Equals(_config.OutputDirectory, path, StringComparison.OrdinalIgnoreCase))
                SaveConfig("已更新保存路径");
        }

        private void IntervalSlider_OnChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (IntervalText == null)
                return;
            var seconds = Math.Max(1, Math.Min(120, (int)Math.Round(e.NewValue)));
            IntervalText.Text = seconds + " 秒";
            if (_loadingControls || _config == null)
                return;
            _config.CheckIntervalSeconds = seconds;
            SetMonitorInterval(seconds);
            SaveConfig(null);
        }

        private void SetMonitorInterval(int seconds)
        {
            var value = Math.Max(1, Math.Min(120, seconds <= 0 ? 16 : seconds));
            _monitorTimer.Interval = TimeSpan.FromSeconds(value);
            if (_config != null)
                _config.CheckIntervalSeconds = value;
        }

        private void MonitorTimer_OnTick(object sender, EventArgs e)
        {
            RunStatusCheckAsync();
        }

        private async void RestartRecordingAfterRotation(RoomConfig room, int segmentIndex)
        {
            await StartRoomRecordingAsync(room, segmentIndex);
        }

        private async void RestartRecordingAfterFailure(RoomConfig room, int segmentIndex)
        {
            await Task.Delay(1500);
            if (room == null || !room.IsRecording || !_recordingReconnectAttempts.ContainsKey(room))
                return;
            await StartRoomRecordingAsync(room, segmentIndex, true);
        }

        private async void RunStatusCheckAsync()
        {
            await CheckAllRoomsAsync();
        }

        private async Task CheckAllRoomsAsync()
        {
            if (_monitorTickRunning || _monitorCancellation == null)
                return;
            _monitorTickRunning = true;
            try
            {
                var rooms = Rooms.ToList();
                var checks = rooms.Select(CheckRoomStatusAsync).ToArray();
                await Task.WhenAll(checks);
            }
            finally
            {
                _monitorTickRunning = false;
            }
        }

        private async Task CheckRoomStatusAsync(RoomConfig room)
        {
            if (room == null || _monitorCancellation == null || _monitorCancellation.IsCancellationRequested)
                return;
            room.LiveStatus = "检测中";
            room.LiveStatusDetail = "正在请求直播状态";
            RefreshRoomCards();
            try
            {
                var result = await _recorder.ProbeAsync(room, _monitorCancellation.Token);
                var capturedName = false;
                var qualitySelectionChanged = false;
                var shouldAutoStart = false;
                var shouldAutoStop = false;
                if (NormalizePlatform(room.Platform) == "Bilibili" && result.AvailableQualities != null && result.AvailableQualities.Length > 0)
                    qualitySelectionChanged = ApplyBilibiliQualityOptions(room, result.AvailableQualities);
                if (NeedsAutomaticRoomName(room.Remark) && !string.IsNullOrWhiteSpace(result.DisplayName))
                {
                    room.Remark = result.DisplayName.Trim();
                    capturedName = true;
                }
                if (result.HasError)
                {
                    room.LiveStatus = "检查失败";
                    room.LiveStatusDetail = ShortenStatus(result.Message);
                }
                else if (result.IsLive)
                {
                    room.LiveStatus = "开播";
                    room.LiveStatusDetail = "已发现直播流";
                    room.ConsecutiveOfflineChecks = 0;
                    shouldAutoStart = room.AutoRecordEnabled && !room.AutoRecordSuppressed && !room.IsRecording && !_recordingStarting.Contains(room);
                }
                else
                {
                    room.LiveStatus = "未开播";
                    room.LiveStatusDetail = "暂未发现直播流";
                    room.ConsecutiveOfflineChecks++;
                    shouldAutoStop = room.IsRecording && room.ConsecutiveOfflineChecks >= 2;
                }
                if (capturedName)
                    SaveConfig("已获取主播名称");
                else if (qualitySelectionChanged)
                    SaveConfig(null);
                UpdateSelectedRoomStatus();
                RefreshRoomCards();
                if (shouldAutoStop)
                {
                    AddActivity("直播已结束", room.DisplayName + " · 已自动停止录制并封装文件");
                    StopRoomRecording(room, false);
                    room.RecordingStatus = "已结束";
                }
                else if (shouldAutoStart)
                {
                    AddActivity("自动录制已触发", room.DisplayName + " · " + room.RoomId);
                    await StartRoomRecordingAsync(room, 1);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                room.LiveStatus = "检查失败";
                room.LiveStatusDetail = ShortenStatus(ex.Message);
            }
            UpdateSelectedRoomStatus();
            RefreshRoomCards();
            AddActivity("直播状态更新", room.DisplayName + " · " + room.RoomId + " · " + room.LiveStatus);
        }

        private string ShortenStatus(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "网络请求失败";
            value = value.Replace("\r", " ").Replace("\n", " ").Trim();
            return value.Length > 60 ? value.Substring(0, 60) + "…" : value;
        }

        private bool NeedsAutomaticRoomName(string value)
        {
            return string.IsNullOrWhiteSpace(value) ||
                   value.IndexOf("undefined", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   value == "未命名直播间" ||
                   value == "正在获取主播信息";
        }

        private void SaveConfig(string activity, bool queueExplicitlyCleared = false)
        {
            if (_config == null)
                return;
            _config.OutputDirectory = NormalizeOutputDirectory(OutputPathBox.Text);
            OutputPathBox.Text = _config.OutputDirectory;
            _config.DefaultPlatform = NormalizePlatform(PlatformCombo.SelectedItem as string);
            if (_config.DefaultPlatform == "抖音")
                _config.DefaultQuality = AddQualityCombo.SelectedItem as string ?? "自动";
            _config.DefaultFormat = AddFormatCombo.SelectedItem as string ?? "MP4";
            _config.BilibiliCookieData = _bilibili.ExportProtectedCookies();
            _config.BilibiliUserName = _bilibili.IsLoggedIn ? _bilibili.UserName : null;
            _config.Rooms = new ObservableCollection<RoomConfig>(Rooms.Select(room => room.Clone()));
            if (_config.Rooms.Count > 0)
                _config.QueueExplicitlyCleared = false;
            else if (queueExplicitlyCleared)
                _config.QueueExplicitlyCleared = true;
            SaveMediaSettingsToConfig();
            try
            {
                if (_store.Save(_config))
                {
                    LastSavedText.Text = " · " + DateTime.Now.ToString("HH:mm") + " 已保存";
                    if (!string.IsNullOrWhiteSpace(activity))
                        AddActivity(activity, "配置已写入本机");
                }
                else
                    LastSavedText.Text = " · 已保护已保存的直播间队列";
            }
            catch (Exception ex)
            {
                LastSavedText.Text = " · 保存失败";
                AddActivity("配置保存失败", ex.Message);
            }
        }

        private string NormalizeOutputDirectory(string value)
        {
            var fallback = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            var candidate = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
            try
            {
                var hasDrivePrefix = candidate.Length >= 3 && char.IsLetter(candidate[0]) && candidate[1] == ':' && (candidate[2] == '\\' || candidate[2] == '/');
                if (candidate.IndexOf(':') > 0 && !hasDrivePrefix)
                    throw new ArgumentException("保存路径格式无效");
                Directory.CreateDirectory(candidate);
                return candidate;
            }
            catch
            {
                AddActivity("保存路径已回退", "原路径无效，已使用 Videos 文件夹");
                Directory.CreateDirectory(fallback);
                return fallback;
            }
        }

        private void ReplaceRooms(ObservableCollection<RoomConfig> rooms)
        {
            foreach (var runningRoom in _recordingSessions.Keys.ToList())
                StopRoomRecording(runningRoom, false);
            Rooms.Clear();
            if (rooms != null)
            {
                foreach (var room in rooms)
                {
                    if (room != null && !string.IsNullOrWhiteSpace(room.RoomId))
                    {
                        room.Platform = NormalizePlatform(room.Platform);
                        PopulateRoomQualityOptions(room, false);
                        Rooms.Add(room);
                    }
                }
            }
            _selectedRoom = null;
            ClearSelectedRoom();
            RefreshRoomCards();
        }

        private void RefreshRoomCards()
        {
            RoomItems.Items.Refresh();
            UpdateRoomSummary();
        }

        private void UpdateRoomSummary()
        {
            RoomCountText.Text = Rooms.Count.ToString();
            EmptyRoomsText.Visibility = Rooms.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            OpenTaskText.Text = _recordingSessions.Count.ToString() + " 个打开";
        }

        private void UpdateTasks()
        {
            TaskItems.Children.Clear();
            if (_recordingSessions.Count == 0)
            {
                TaskItems.Children.Add(new TextBlock { Text = "暂时没有运行中的任务", Foreground = new SolidColorBrush(Color.FromRgb(170, 186, 178)), FontSize = 12, TextWrapping = TextWrapping.Wrap });
                OpenTaskText.Text = "0 个打开";
                return;
            }

            foreach (var entry in _recordingSessions)
                TaskItems.Children.Add(CreateTaskRow(entry.Key.DisplayName, entry.Key.RoomId + " · " + entry.Key.RecordingStatus, true));
            OpenTaskText.Text = _recordingSessions.Count + " 个打开";
        }

        private void RecordingClockTimer_OnTick(object sender, EventArgs e)
        {
            foreach (var entry in _recordingStartedAt.ToList())
            {
                if (!entry.Key.IsRecording)
                {
                    _recordingStartedAt.Remove(entry.Key);
                    entry.Key.RecordingElapsed = string.Empty;
                    continue;
                }
                UpdateRecordingElapsed(entry.Key);
            }
        }

        private void UpdateRecordingElapsed(RoomConfig room)
        {
            DateTime started;
            if (room == null || !_recordingStartedAt.TryGetValue(room, out started))
                return;
            var elapsed = DateTime.Now - started;
            if (elapsed.TotalSeconds < 0)
                elapsed = TimeSpan.Zero;
            var totalSeconds = Math.Max(0, (int)elapsed.TotalSeconds);
            var hours = totalSeconds / 3600;
            var minutes = (totalSeconds % 3600) / 60;
            var seconds = totalSeconds % 60;
            room.RecordingElapsed = hours.ToString("00") + ":" + minutes.ToString("00") + ":" + seconds.ToString("00");
        }

        private void UpdateSelectedRoomStatus()
        {
            if (_selectedRoom == null)
                return;
            var recording = _selectedRoom.IsRecording;
            SelectedStatus.Text = recording ? _selectedRoom.RecordingStatus : _selectedRoom.LiveStatus;
            StageText.Text = recording ? "录制中 · " + _selectedRoom.LiveStatus : "监控 · " + _selectedRoom.LiveStatus;
            StatusPill.Background = recording ? FindBrush("BrightGreenBrush") : (_selectedRoom.LiveStatus == "开播" ? FindBrush("MintBrush") : FindBrush("LineBrush"));
            SelectedStatus.Foreground = recording ? FindBrush("GreenBrush") : FindBrush("InkBrush");
            SelectedRoomId.Text = _selectedRoom.RoomId + " · " + (_selectedRoom.OutputFormat ?? "MP4") + " 输出 · " + _selectedRoom.LiveStatusDetail;
        }

        private UIElement CreateTaskRow(string title, string subtitle, bool running)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
            var titleRow = new Grid();
            titleRow.Children.Add(new TextBlock { Text = "□  " + title, Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.Medium });
            titleRow.Children.Add(new TextBlock { Text = running ? "现在" : "待命", Foreground = running ? FindBrush("BrightGreenBrush") : new SolidColorBrush(Color.FromRgb(170, 186, 178)), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right });
            panel.Children.Add(titleRow);
            panel.Children.Add(new TextBlock { Text = subtitle, Foreground = new SolidColorBrush(Color.FromRgb(170, 186, 178)), FontSize = 11, Margin = new Thickness(22, 5, 0, 0) });
            return panel;
        }

        private void AddActivity(string title, string detail)
        {
            var panel = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var icon = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Background = FindBrush("MintBrush") };
            icon.Child = new TextBlock { Text = "✓", Foreground = FindBrush("GreenBrush"), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(icon, 0);
            panel.Children.Add(icon);
            var text = new StackPanel { Margin = new Thickness(10, 0, 0, 0) };
            text.Children.Add(new TextBlock { Text = title, Foreground = FindBrush("InkBrush"), FontSize = 12, FontWeight = FontWeights.Medium });
            text.Children.Add(new TextBlock { Text = detail, Foreground = FindBrush("MutedBrush"), FontSize = 11, Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(text, 1);
            panel.Children.Add(text);
            var time = new TextBlock { Text = DateTime.Now.ToString("HH:mm"), Foreground = FindBrush("MutedBrush"), FontSize = 10, VerticalAlignment = VerticalAlignment.Top };
            Grid.SetColumn(time, 2);
            panel.Children.Add(time);
            ActivityItems.Items.Insert(0, panel);
            while (ActivityItems.Items.Count > 5)
                ActivityItems.Items.RemoveAt(ActivityItems.Items.Count - 1);
        }

        private string[] GetQualityOptions(string platform)
        {
            if (NormalizePlatform(platform) == "Bilibili")
                return new[] { "自动" };
            return new[] { "自动", "原画", "蓝光", "超清", "高清", "标清", "流畅" };
        }

        private void RefreshAddQualityOptions(string preferred)
        {
            if (PlatformCombo == null || AddQualityCombo == null)
                return;
            var options = GetQualityOptions(PlatformCombo.SelectedItem as string);
            AddQualityCombo.Items.Clear();
            foreach (var quality in options)
                AddQualityCombo.Items.Add(quality);
            AddQualityCombo.SelectedItem = options.Contains(preferred) ? preferred : "自动";
        }

        private void PopulateRoomQualityOptions(RoomConfig room, bool normalizeSelection)
        {
            if (room == null)
                return;
            if (NormalizePlatform(room.Platform) == "Bilibili")
            {
                var selectedQuality = string.IsNullOrWhiteSpace(room.Quality) ? "自动" : room.Quality;
                if (normalizeSelection)
                {
                    selectedQuality = "自动";
                    room.Quality = "自动";
                }
                room.AvailableQualities.Clear();
                room.AvailableQualities.Add("自动");
                if (selectedQuality != "自动")
                    room.AvailableQualities.Add(selectedQuality);
                return;
            }
            var options = GetQualityOptions(room.Platform);
            room.AvailableQualities.Clear();
            foreach (var quality in options)
                room.AvailableQualities.Add(quality);
            if (normalizeSelection && !options.Contains(string.IsNullOrWhiteSpace(room.Quality) ? "自动" : room.Quality))
                room.Quality = "自动";
        }

        private bool ApplyBilibiliQualityOptions(RoomConfig room, IEnumerable<string> availableQualities)
        {
            var options = availableQualities
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct()
                .ToList();
            options.Remove("自动");
            options.Insert(0, "自动");

            if (!room.AvailableQualities.SequenceEqual(options))
            {
                room.AvailableQualities.Clear();
                foreach (var option in options)
                    room.AvailableQualities.Add(option);
            }

            var selectedQuality = string.IsNullOrWhiteSpace(room.Quality) ? "自动" : room.Quality;
            if (options.Contains(selectedQuality))
                return false;
            room.Quality = "自动";
            return true;
        }

        private void RefreshAllRoomQualityOptions(bool normalizeSelection)
        {
            foreach (var room in Rooms)
                PopulateRoomQualityOptions(room, normalizeSelection);
            if (_selectedRoom != null)
                SelectRoom(_selectedRoom);
            RefreshRoomCards();
        }

        private void UpdateAddQualityEditorVisibility()
        {
            if (PlatformCombo == null || AddQualityCombo == null || AddQualityColumn == null)
                return;
            var visible = NormalizePlatform(PlatformCombo.SelectedItem as string) == "抖音";
            AddQualityCombo.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            AddQualityColumn.Width = visible ? new GridLength(110) : new GridLength(0);
        }

        private void UpdateBilibiliAccountUi()
        {
            if (BilibiliAccountPanel != null && PlatformCombo != null)
            {
                var bilibiliSelected = NormalizePlatform(PlatformCombo.SelectedItem as string) == "Bilibili";
                BilibiliAccountPanel.Visibility = bilibiliSelected ? Visibility.Visible : Visibility.Collapsed;
                if (_bilibili.IsLoggedIn)
                {
                    BilibiliAccountStatusText.Text = "已登录" + (string.IsNullOrWhiteSpace(_bilibili.UserName) ? string.Empty : " · " + _bilibili.UserName);
                    BilibiliLoginButton.Visibility = Visibility.Collapsed;
                    BilibiliLogoutButton.Visibility = Visibility.Visible;
                }
                else
                {
                    BilibiliAccountStatusText.Text = "游客模式 · 使用游客画质范围";
                    BilibiliLoginButton.Visibility = Visibility.Visible;
                    BilibiliLogoutButton.Visibility = Visibility.Collapsed;
                }
            }
            UpdateMediaBilibiliAccountUi();
        }

        private static string NormalizePlatform(string platform)
        {
            return string.Equals(platform, "Bilibili", StringComparison.OrdinalIgnoreCase) ? "Bilibili" : "抖音";
        }

        private string NormalizeRoomId(string input, string platform)
        {
            if (string.IsNullOrWhiteSpace(input))
                return null;
            input = input.Trim();
            if (NormalizePlatform(platform) == "Bilibili")
            {
                if (Regex.IsMatch(input, "^\\d{1,20}$"))
                    return input;
                var bilibiliMatch = Regex.Match(input, "live\\.bilibili\\.com/(?:blanc/)?(\\d{1,20})", RegexOptions.IgnoreCase);
                return bilibiliMatch.Success ? bilibiliMatch.Groups[1].Value : null;
            }
            if (Regex.IsMatch(input, "^\\d{6,20}$"))
                return input;
            var match = Regex.Match(input, "(?:live\\.douyin\\.com/|www\\.douyin\\.com/follow/live/)(\\d{6,20})(?:[/?#]|$)", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value : null;
        }

        private Brush FindBrush(string key)
        {
            return (Brush)FindResource(key);
        }

        private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left)
                return;

            _titleBarPressed = true;
            _titleBarDragging = false;
            _titleBarPressPoint = e.GetPosition(this);

            if (e.ClickCount == 2 && !IsInteractiveTitleBarSource(e.OriginalSource as DependencyObject))
            {
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                _titleBarPressed = false;
                e.Handled = true;
            }
        }

        private void TitleBar_OnMouseMove(object sender, MouseEventArgs e)
        {
            if (!_titleBarPressed || _titleBarDragging || e.LeftButton != MouseButtonState.Pressed)
                return;

            var point = e.GetPosition(this);
            if (Math.Abs(point.X - _titleBarPressPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(point.Y - _titleBarPressPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            _titleBarDragging = true;
            _titleBarPressed = false;
            e.Handled = true;
            ReleaseCapture();
            var handle = new WindowInteropHelper(this).Handle;
            SendMessage(handle, WmNcLeftButtonDown, new IntPtr(HitTestCaption), IntPtr.Zero);
            _titleBarDragging = false;
        }

        private void TitleBar_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _titleBarPressed = false;
            _titleBarDragging = false;
        }

        private bool IsInteractiveTitleBarSource(DependencyObject source)
        {
            var current = source;
            while (current != null)
            {
                if (current is ButtonBase || current is TextBoxBase || current is ComboBox)
                    return true;
                current = VisualTreeHelper.GetParent(current);
            }
            return false;
        }

        private void Minimize_OnClick(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void Close_OnClick(object sender, RoutedEventArgs e)
        {
            SaveConfig(null);
            if (_muxCancellation != null)
                _muxCancellation.Cancel();
            CancelMediaWork();
            if (_monitorTimer != null)
                _monitorTimer.Stop();
            if (_recordingClockTimer != null)
                _recordingClockTimer.Stop();
            if (_monitorCancellation != null)
            {
                _monitorCancellation.Cancel();
                _monitorCancellation.Dispose();
                _monitorCancellation = null;
            }
            foreach (var room in Rooms.ToList())
                StopRoomRecording(room, false);
            Close();
        }
    }
}
