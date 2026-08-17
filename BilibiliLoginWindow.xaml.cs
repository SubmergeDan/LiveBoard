using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using QRCoder;

namespace LiveBoard
{
    public partial class BilibiliLoginWindow : Window
    {
        private readonly BilibiliService _service;
        private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();
        private int _qrGeneration;

        public BilibiliLoginWindow(BilibiliService service)
        {
            if (service == null)
                throw new ArgumentNullException("service");
            _service = service;
            InitializeComponent();
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            await GenerateAndPollAsync();
        }

        private async void RefreshQr_OnClick(object sender, RoutedEventArgs e)
        {
            await GenerateAndPollAsync();
        }

        private async Task GenerateAndPollAsync()
        {
            var generation = ++_qrGeneration;
            QrImage.Source = null;
            QrImage.Opacity = 1;
            QrLoadingText.Visibility = Visibility.Visible;
            QrLoadingText.Text = "正在生成二维码";
            LoginStatusText.Text = "正在连接 Bilibili";

            try
            {
                var session = await _service.BeginQrLoginAsync(_cancellation.Token);
                if (generation != _qrGeneration || _cancellation.IsCancellationRequested)
                    return;
                QrImage.Source = CreateQrImage(session.Url);
                QrLoadingText.Visibility = Visibility.Collapsed;
                LoginStatusText.Text = "请使用哔哩哔哩客户端扫码";
                await PollLoginAsync(session.Key, generation);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                if (generation != _qrGeneration)
                    return;
                QrLoadingText.Visibility = Visibility.Visible;
                QrLoadingText.Text = "二维码生成失败";
                LoginStatusText.Text = Shorten(ex.Message);
            }
        }

        private async Task PollLoginAsync(string key, int generation)
        {
            while (!_cancellation.IsCancellationRequested && generation == _qrGeneration)
            {
                var result = await _service.PollQrLoginAsync(key, _cancellation.Token);
                if (generation != _qrGeneration)
                    return;
                LoginStatusText.Text = result.Message;
                if (result.Success)
                {
                    DialogResult = true;
                    Close();
                    return;
                }
                if (result.Expired)
                {
                    QrImage.Opacity = 0.28;
                    QrLoadingText.Visibility = Visibility.Visible;
                    QrLoadingText.Text = "二维码已过期";
                    return;
                }
                await Task.Delay(1800, _cancellation.Token);
            }
        }

        private static BitmapImage CreateQrImage(string value)
        {
            byte[] png;
            using (var generator = new QRCodeGenerator())
            using (var data = generator.CreateQrCode(value, QRCodeGenerator.ECCLevel.Q))
            using (var qr = new BitmapByteQRCode(data))
            {
                png = qr.GetGraphic(12, "#111615", "#FFFFFF");
            }
            var image = new BitmapImage();
            using (var stream = new MemoryStream(png))
            {
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                image.EndInit();
            }
            image.Freeze();
            return image;
        }

        private static string Shorten(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "连接失败，请刷新重试";
            value = value.Replace("\r", " ").Replace("\n", " ").Trim();
            return value.Length > 34 ? value.Substring(0, 34) + "…" : value;
        }

        private void Header_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                DragMove();
        }

        private void Close_OnClick(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            _qrGeneration++;
            _cancellation.Cancel();
            _cancellation.Dispose();
        }
    }
}
