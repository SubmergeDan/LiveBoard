using System;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace LiveBoard
{
    public partial class MainWindow
    {
        private readonly MediaExportService _mediaExporter = new MediaExportService();
        private CancellationTokenSource _mediaCancellation;
        private MediaAnalysisResult _mediaAnalysis;
        private bool _mediaUrlPlaceholder = true;
        private bool _mediaProxyPlaceholder = true;
        private double _mediaProgressFloor;

        private void InitializeMediaWorkspace()
        {
            foreach (var item in new[] { "不使用浏览器登录", "Microsoft Edge", "Google Chrome", "Mozilla Firefox" })
                MediaCookieCombo.Items.Add(item);
            MediaCookieCombo.SelectedIndex = 0;
            MediaOutputPathText.Text = "Videos";
            MediaTaskOutputText.Text = "Videos";
            UpdateMediaBilibiliAccountUi();
        }

        private void LoadMediaWorkspaceSettings()
        {
            if (_config == null)
                return;
            var directory = string.IsNullOrWhiteSpace(_config.MediaOutputDirectory) ? _config.OutputDirectory : _config.MediaOutputDirectory;
            SetMediaOutputDirectory(directory);
            var browser = string.IsNullOrWhiteSpace(_config.MediaCookieBrowser) ? "不使用浏览器登录" : _config.MediaCookieBrowser;
            MediaCookieCombo.SelectedItem = MediaCookieCombo.Items.Cast<object>().Any(item => string.Equals(item as string, browser, StringComparison.OrdinalIgnoreCase))
                ? browser
                : "不使用浏览器登录";
            if (!string.IsNullOrWhiteSpace(_config.MediaProxy))
            {
                MediaProxyBox.Text = _config.MediaProxy;
                MediaProxyBox.Foreground = FindBrush("InkBrush");
                _mediaProxyPlaceholder = false;
            }
            else
            {
                MediaProxyBox.Text = "代理地址（可选，留空使用系统代理）";
                MediaProxyBox.Foreground = FindBrush("MutedBrush");
                _mediaProxyPlaceholder = true;
            }
        }

        private void SaveMediaSettingsToConfig()
        {
            if (_config == null || MediaCookieCombo == null)
                return;
            _config.MediaOutputDirectory = GetMediaOutputDirectory();
            _config.MediaCookieBrowser = MediaCookieCombo.SelectedItem as string ?? "不使用浏览器登录";
            _config.MediaProxy = _mediaProxyPlaceholder ? null : (MediaProxyBox.Text ?? string.Empty).Trim();
        }

        private async void AnalyzeMedia_OnClick(object sender, RoutedEventArgs e)
        {
            if (_mediaCancellation != null)
                return;
            var input = _mediaUrlPlaceholder ? string.Empty : MediaUrlBox.Text;
            if (string.IsNullOrWhiteSpace(MediaExportService.ExtractUrl(input)))
            {
                SetMediaStatus("请输入有效的网页地址", "支持公开视频网页与常见媒体链接", "需要地址", "!", FindBrush("OrangeBrush"));
                MediaUrlBox.Focus();
                return;
            }

            _mediaAnalysis = null;
            MediaAssetItems.ItemsSource = null;
            MediaAssetItems.Visibility = Visibility.Collapsed;
            MediaQualityCombo.ItemsSource = null;
            MediaQualityPanel.Visibility = Visibility.Collapsed;
            MediaCountBadge.Visibility = Visibility.Collapsed;
            MediaProgressBar.Visibility = Visibility.Collapsed;
            MediaProgressBar.IsIndeterminate = false;
            MediaProgressBar.Value = 0;
            StartMediaButton.IsEnabled = false;
            var cancellation = new CancellationTokenSource();
            _mediaCancellation = cancellation;
            SetMediaBusy(true, "解析中");
            SetMediaStatus("正在解析媒体", "正在识别网页中可导出的媒体资源", "解析中", "…", FindBrush("BrightGreenBrush"));

            try
            {
                var result = await _mediaExporter.AnalyzeAsync(
                    input,
                    GetMediaCookieBrowser(),
                    GetMediaProxy(),
                    _bilibili.ExportNetscapeCookies(),
                    cancellation.Token,
                    CreateMediaProgressHandler());
                if (cancellation.IsCancellationRequested)
                {
                    SetMediaStatus("已取消解析", "", "已取消", "·", FindBrush("LineBrush"));
                    return;
                }
                if (!result.Success)
                {
                    SetMediaStatus("解析失败", result.ErrorText, "失败", "!", FindBrush("OrangeBrush"));
                    MediaAnalyzeStateText.Text = "解析失败";
                    return;
                }

                _mediaAnalysis = result;
                MediaTitleText.Text = string.IsNullOrWhiteSpace(result.Title) ? result.Platform + " 媒体" : result.Title.Replace("\r", " ").Replace("\n", " ");
                MediaTitleText.ToolTip = result.Title;
                MediaUrlSummaryText.Text = result.Url;
                MediaUrlSummaryText.ToolTip = result.Url;
                MediaPlatformText.Text = "·  " + result.Platform;
                MediaCountText.Text = result.AssetCount + " 个媒体";
                MediaCountBadge.Visibility = Visibility.Visible;
                MediaAssetItems.ItemsSource = result.Assets;
                MediaAssetItems.Visibility = Visibility.Visible;
                MediaTaskPlatformText.Text = result.Platform;
                MediaTaskTitleText.Text = MediaTitleText.Text;
                MediaTaskTitleText.ToolTip = result.Title;
                MediaAnalyzeStateText.Text = "解析完成";
                if (result.Formats.Count > 0)
                {
                    MediaQualityCombo.ItemsSource = result.Formats;
                    MediaQualityCombo.SelectedIndex = 0;
                    MediaQualityPanel.Visibility = Visibility.Visible;
                    MediaFormatHintText.Text = result.Platform == "Bilibili" ? "B站账号权限决定可选画质" : "按原始媒体流导出";
                }
                else
                    MediaFormatHintText.Text = "全部媒体按原始文件导出";
                StartMediaButton.IsEnabled = true;
                SetMediaStatus("媒体已就绪", result.Platform + " · " + result.AssetCount + " 个可导出媒体", "已就绪", "✓", FindBrush("MintBrush"));
            }
            catch (OperationCanceledException)
            {
                SetMediaStatus("已取消解析", "", "已取消", "·", FindBrush("LineBrush"));
            }
            catch (Exception ex)
            {
                SetMediaStatus("解析失败", ShortenStatus(ex.Message), "失败", "!", FindBrush("OrangeBrush"));
            }
            finally
            {
                if (ReferenceEquals(_mediaCancellation, cancellation))
                    _mediaCancellation = null;
                cancellation.Dispose();
                SetMediaBusy(false, null);
            }
        }

        private async void StartMediaExport_OnClick(object sender, RoutedEventArgs e)
        {
            if (_mediaCancellation != null || _mediaAnalysis == null || !_mediaAnalysis.Success)
                return;
            var directory = GetMediaOutputDirectory();
            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (Exception ex)
            {
                SetMediaStatus("保存位置不可用", ShortenStatus(ex.Message), "失败", "!", FindBrush("OrangeBrush"));
                return;
            }

            var format = MediaQualityCombo.SelectedItem as MediaFormatOption;
            var cancellation = new CancellationTokenSource();
            _mediaCancellation = cancellation;
            _mediaProgressFloor = 0;
            SetMediaBusy(true, "导出中");
                SetMediaStatus("正在导出媒体", _mediaAnalysis.Title, "导出中", "…", FindBrush("BrightGreenBrush"));
            try
            {
                var result = await _mediaExporter.ExportAsync(
                    _mediaAnalysis,
                    format,
                    directory,
                    GetMediaCookieBrowser(),
                    GetMediaProxy(),
                    _bilibili.ExportNetscapeCookies(),
                    cancellation.Token,
                    CreateMediaProgressHandler());
                if (result.Cancelled)
                {
                    SetMediaStatus("已取消导出", "已下载的完整文件会保留", "已取消", "·", FindBrush("LineBrush"));
                    return;
                }
                if (!result.Success)
                {
                    SetMediaStatus("导出失败", result.ErrorText, "失败", "!", FindBrush("OrangeBrush"));
                    AddActivity("媒体导出失败", ShortenStatus(result.ErrorText));
                    return;
                }
                MediaProgressBar.IsIndeterminate = false;
                MediaProgressBar.Value = 100;
                SetMediaStatus("导出完成", result.DownloadedCount + " 个媒体已保存", "已完成", "✓", FindBrush("BrightGreenBrush"));
                AddActivity("媒体导出完成", _mediaAnalysis.Platform + " · " + result.DownloadedCount + " 个文件");
            }
            catch (OperationCanceledException)
            {
                SetMediaStatus("已取消导出", "已下载的完整文件会保留", "已取消", "·", FindBrush("LineBrush"));
            }
            catch (Exception ex)
            {
                SetMediaStatus("导出失败", ShortenStatus(ex.Message), "失败", "!", FindBrush("OrangeBrush"));
            }
            finally
            {
                if (ReferenceEquals(_mediaCancellation, cancellation))
                    _mediaCancellation = null;
                cancellation.Dispose();
                SetMediaBusy(false, null);
                if (_mediaAnalysis == null || !_mediaAnalysis.Success)
                    MediaProgressBar.Visibility = Visibility.Collapsed;
                StartMediaButton.IsEnabled = _mediaAnalysis != null && _mediaAnalysis.Success;
            }
        }

        private void CancelMediaExport_OnClick(object sender, RoutedEventArgs e)
        {
            if (_mediaCancellation == null)
                return;
            SetMediaStatus("已取消导出", "已停止当前下载任务", "已取消", "·", FindBrush("LineBrush"));
            CancelMediaButton.Visibility = Visibility.Collapsed;
            _mediaCancellation.Cancel();
        }

        private void ChooseMediaFolder_OnClick(object sender, RoutedEventArgs e)
        {
            using (var dialog = new Forms.FolderBrowserDialog())
            {
                dialog.Description = "选择媒体保存位置";
                dialog.SelectedPath = Directory.Exists(GetMediaOutputDirectory()) ? GetMediaOutputDirectory() : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
                if (dialog.ShowDialog() != Forms.DialogResult.OK)
                    return;
                SetMediaOutputDirectory(dialog.SelectedPath);
                SaveConfig(null);
            }
        }

        private void OpenMediaFolder_OnClick(object sender, RoutedEventArgs e)
        {
            OpenFolderInExplorer(GetMediaOutputDirectory(), "媒体保存文件夹");
        }

        private void MediaUrl_OnGotFocus(object sender, RoutedEventArgs e)
        {
            if (!_mediaUrlPlaceholder)
                return;
            MediaUrlBox.Clear();
            MediaUrlBox.Foreground = FindBrush("InkBrush");
            _mediaUrlPlaceholder = false;
        }

        private void MediaUrl_OnLostFocus(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(MediaUrlBox.Text))
                return;
            MediaUrlBox.Text = "粘贴任意公开网页或媒体地址";
            MediaUrlBox.Foreground = FindBrush("MutedBrush");
            _mediaUrlPlaceholder = true;
        }

        private void MediaProxy_OnGotFocus(object sender, RoutedEventArgs e)
        {
            if (!_mediaProxyPlaceholder)
                return;
            MediaProxyBox.Clear();
            MediaProxyBox.Foreground = FindBrush("InkBrush");
            _mediaProxyPlaceholder = false;
        }

        private void MediaProxy_OnLostFocus(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(MediaProxyBox.Text))
            {
                SaveConfig(null);
                return;
            }
            MediaProxyBox.Text = "代理地址（可选，留空使用系统代理）";
            MediaProxyBox.Foreground = FindBrush("MutedBrush");
            _mediaProxyPlaceholder = true;
            SaveConfig(null);
        }

        private void MediaCookie_OnChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loadingControls && _config != null)
                SaveConfig(null);
        }

        private void MediaQuality_OnChanged(object sender, SelectionChangedEventArgs e)
        {
            var quality = MediaQualityCombo.SelectedItem as MediaFormatOption;
            if (quality != null)
                MediaFormatHintText.Text = quality.Label;
        }

        private void SetMediaOutputDirectory(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
                directory = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            _config.MediaOutputDirectory = directory;
            MediaOutputPathText.Text = new DirectoryInfo(directory).Name;
            MediaOutputPathText.ToolTip = directory;
            MediaTaskOutputText.Text = new DirectoryInfo(directory).Name;
            MediaTaskOutputText.ToolTip = directory;
        }

        private string GetMediaOutputDirectory()
        {
            if (_config != null && !string.IsNullOrWhiteSpace(_config.MediaOutputDirectory))
                return _config.MediaOutputDirectory;
            return Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        }

        private string GetMediaCookieBrowser()
        {
            return MediaCookieCombo.SelectedItem as string ?? "不使用浏览器登录";
        }

        private void UpdateMediaBilibiliAccountUi()
        {
            if (MediaBilibiliAccountStatusText == null || MediaBilibiliLoginButton == null || MediaBilibiliLogoutButton == null)
                return;
            if (_bilibili.IsLoggedIn)
            {
                MediaBilibiliAccountStatusText.Text = "已登录" + (string.IsNullOrWhiteSpace(_bilibili.UserName) ? string.Empty : " · " + _bilibili.UserName);
                MediaBilibiliLoginButton.Visibility = Visibility.Collapsed;
                MediaBilibiliLogoutButton.Visibility = Visibility.Visible;
            }
            else
            {
                MediaBilibiliAccountStatusText.Text = "游客模式 · 使用游客画质范围";
                MediaBilibiliLoginButton.Visibility = Visibility.Visible;
                MediaBilibiliLogoutButton.Visibility = Visibility.Collapsed;
            }
        }

        private string GetMediaProxy()
        {
            return _mediaProxyPlaceholder ? null : (MediaProxyBox.Text ?? string.Empty).Trim();
        }

        private Action<string> CreateMediaProgressHandler()
        {
            return delegate(string line)
            {
                if (string.IsNullOrWhiteSpace(line))
                    return;
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (line.StartsWith("__RH_PROGRESS__", StringComparison.OrdinalIgnoreCase))
                    {
                        var value = line.Substring("__RH_PROGRESS__".Length).Trim();
                        double percent;
                        string speed;
                        string eta;
                        if (TryReadMediaProgress(value, out percent, out speed, out eta))
                        {
                            SetMediaProgress(percent, speed, eta);
                        }
                        else
                        {
                            MediaProgressBar.IsIndeterminate = true;
                            MediaProgressBar.Visibility = Visibility.Visible;
                            MediaProgressText.Text = "处理中";
                            MediaTaskStateText.Text = "处理中";
                            MediaStatusDetailText.Text = "正在接收媒体数据";
                        }
                    }
                    else
                    {
                        var percentMatch = Regex.Match(line, @"(?<percent>[0-9]+(?:[\.,][0-9]+)?)\s*%", RegexOptions.CultureInvariant);
                        double percent;
                        if (percentMatch.Success && double.TryParse(percentMatch.Groups["percent"].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out percent))
                            SetMediaProgress(percent, null, null);
                        else if (line.IndexOf("Downloading", StringComparison.OrdinalIgnoreCase) >= 0 || line.IndexOf("下载", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            MediaProgressBar.IsIndeterminate = true;
                            MediaProgressBar.Visibility = Visibility.Visible;
                            MediaProgressText.Text = "处理中";
                            MediaTaskStateText.Text = "处理中";
                        }
                    }
                }));
            };
        }

        private void SetMediaProgress(double percent, string speed, string eta)
        {
            var normalizedPercent = Math.Max(0, Math.Min(99.9, percent));
            _mediaProgressFloor = Math.Max(_mediaProgressFloor, normalizedPercent);
            MediaProgressBar.IsIndeterminate = false;
            MediaProgressBar.Visibility = Visibility.Visible;
            MediaProgressBar.Value = _mediaProgressFloor;
            MediaProgressText.Text = _mediaProgressFloor.ToString("0.#", CultureInfo.InvariantCulture) + "%";
            MediaTaskStateText.Text = MediaProgressText.Text;
            var detail = new[] { NormalizeMediaSpeed(speed), NormalizeMediaEta(eta) }
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .ToArray();
            MediaStatusDetailText.Text = detail.Length > 0 ? string.Join(" · ", detail) : "正在接收媒体数据";
        }

        private static string NormalizeMediaSpeed(string value)
        {
            value = (value ?? string.Empty).Trim();
            if (value.Length == 0 || value.Equals("NA", StringComparison.OrdinalIgnoreCase) || value.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                return null;
            return value;
        }

        private static string NormalizeMediaEta(string value)
        {
            value = (value ?? string.Empty).Trim();
            if (value.Length == 0 || value.Equals("NA", StringComparison.OrdinalIgnoreCase) || value.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                return null;

            double seconds;
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds))
            {
                if (seconds < 0 || seconds > TimeSpan.FromDays(1).TotalSeconds)
                    return null;
                var remaining = TimeSpan.FromSeconds(seconds);
                return "剩余 " + (remaining.TotalHours >= 1 ? remaining.ToString(@"hh\:mm\:ss") : remaining.ToString(@"mm\:ss"));
            }

            if (!Regex.IsMatch(value, @"^\d{1,2}:\d{2}(?::\d{2})?$") || value.StartsWith("24:", StringComparison.Ordinal))
                return null;
            return "剩余 " + value;
        }

        private static bool TryReadMediaProgress(string value, out double percent, out string speed, out string eta)
        {
            percent = 0;
            speed = null;
            eta = null;
            var parts = (value ?? string.Empty).Split('|');
            long downloaded;
            long total;
            long estimate;
            if (parts.Length >= 6 && long.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out downloaded))
            {
                long.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out total);
                long.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out estimate);
                var knownTotal = total > 0 ? total : estimate;
                if (knownTotal > 0)
                {
                    percent = 100d * downloaded / knownTotal;
                    speed = parts[4].Trim();
                    eta = parts[5].Trim();
                    return true;
                }
            }

            var percentMatch = Regex.Match(value ?? string.Empty, @"(?<percent>[0-9]+(?:[\.,][0-9]+)?)\s*%", RegexOptions.CultureInvariant);
            if (!percentMatch.Success || !double.TryParse(percentMatch.Groups["percent"].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out percent))
                return false;
            speed = parts.Length > 1 ? parts[1].Trim() : null;
            eta = parts.Length > 2 ? parts[2].Trim() : null;
            return true;
        }

        private void SetMediaBusy(bool busy, string state)
        {
            AnalyzeMediaButton.IsEnabled = !busy;
            StartMediaButton.IsEnabled = !busy && _mediaAnalysis != null && _mediaAnalysis.Success;
            CancelMediaButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            if (busy && state == "导出中")
            {
                MediaProgressBar.Visibility = Visibility.Visible;
                MediaProgressBar.IsIndeterminate = true;
                MediaProgressBar.Value = 0;
            }
            MediaTaskStateText.Text = busy ? (state ?? "运行中") : MediaTaskStateText.Text;
            if (!busy && MediaTaskStateText.Text == "取消中")
                MediaTaskStateText.Text = "已取消";
        }

        private void SetMediaStatus(string title, string detail, string taskState, string icon, Brush brush)
        {
            MediaStatusText.Text = title;
            MediaStatusDetailText.Text = detail ?? string.Empty;
            MediaTaskStateText.Text = taskState;
            MediaProgressText.Text = taskState;
            var isWorking = string.Equals(icon, "…", StringComparison.Ordinal);
            var isWaiting = string.Equals(icon, "·", StringComparison.Ordinal) && title.StartsWith("等待", StringComparison.Ordinal);
            var isSuccess = string.Equals(icon, "✓", StringComparison.Ordinal);
            MediaStatusIdleIcon.Visibility = isWaiting ? Visibility.Visible : Visibility.Collapsed;
            MediaStatusWorkingIcon.Visibility = isWorking ? Visibility.Visible : Visibility.Collapsed;
            MediaStatusSuccessIcon.Visibility = isSuccess ? Visibility.Visible : Visibility.Collapsed;
            MediaStatusIconText.Visibility = !isWaiting && !isWorking && !isSuccess ? Visibility.Visible : Visibility.Collapsed;
            MediaStatusIconText.Text = string.Equals(icon, "·", StringComparison.Ordinal) ? "—" : icon;
            MediaStatusIconText.FontSize = string.Equals(icon, "·", StringComparison.Ordinal) ? 14 : 16;
            MediaStatusIconText.LineHeight = string.Equals(icon, "·", StringComparison.Ordinal) ? 14 : 16;
            MediaStatusDot.Background = brush;
            MediaSidebarDot.Background = brush;
            MediaSidebarStateText.Text = title;
        }

        private void CancelMediaWork()
        {
            if (_mediaCancellation != null)
                _mediaCancellation.Cancel();
        }
    }
}
