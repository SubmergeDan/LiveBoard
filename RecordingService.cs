using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace LiveBoard
{
    public sealed class RecordingSession : IDisposable
    {
        private const int GracefulStopTimeoutMilliseconds = 60000;
        private readonly object _errorLock = new object();
        private readonly object _stopLock = new object();
        private string _errorText = string.Empty;

        internal RecordingSession(Process process, string streamUrl, string outputPath, int segmentIndex, long maxBytes)
        {
            Process = process;
            StreamUrl = streamUrl;
            OutputPath = outputPath;
            SegmentIndex = segmentIndex;
            MaxBytes = maxBytes;
        }

        public Process Process { get; private set; }
        public string StreamUrl { get; private set; }
        public string OutputPath { get; private set; }
        public int SegmentIndex { get; private set; }
        public long MaxBytes { get; private set; }
        public bool StopRequested { get; private set; }

        public bool ReachedSizeLimit
        {
            get
            {
                if (MaxBytes <= 0 || string.IsNullOrWhiteSpace(OutputPath))
                    return false;
                try
                {
                    return File.Exists(OutputPath) && new FileInfo(OutputPath).Length >= (long)(MaxBytes * 0.90);
                }
                catch
                {
                    return false;
                }
            }
        }

        public string ErrorText
        {
            get
            {
                lock (_errorLock)
                    return _errorText;
            }
        }

        internal void AppendError(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return;
            lock (_errorLock)
            {
                _errorText = (_errorText + " " + line).Trim();
                if (_errorText.Length > 1200)
                    _errorText = _errorText.Substring(_errorText.Length - 1200);
            }
        }

        public bool Stop()
        {
            lock (_stopLock)
            {
                StopRequested = true;
                if (Process == null)
                    return true;
                try
                {
                    if (Process.HasExited)
                        return true;

                    if (Process.StartInfo.RedirectStandardInput)
                    {
                        Process.StandardInput.WriteLine("q");
                        Process.StandardInput.Flush();
                    }

                    // FFmpeg writes the MP4 trailer only after it receives "q". Do not
                    // terminate it during that write, otherwise the file has no moov atom.
                    if (Process.WaitForExit(GracefulStopTimeoutMilliseconds))
                    {
                        Process.WaitForExit();
                        return true;
                    }
                }
                catch
                {
                    if (Process.HasExited)
                        return true;
                }

                try
                {
                    if (!Process.HasExited)
                        Process.Kill();
                }
                catch
                {
                }
                return false;
            }
        }

        public void Dispose()
        {
            Stop();
            if (Process != null)
            {
                Process.Dispose();
                Process = null;
            }
        }
    }

    public sealed class LiveProbeResult
    {
        public bool IsLive { get; set; }
        public bool HasError { get; set; }
        public string Message { get; set; }
        public string StreamUrl { get; set; }
        public string DisplayName { get; set; }
        public string RoomTitle { get; set; }
        public string[] AvailableQualities { get; set; }
    }

    internal sealed class RoomPageResult
    {
        public string StreamUrl { get; set; }
        public string DisplayName { get; set; }
        public string RoomTitle { get; set; }
    }

    public sealed class RecordingService
    {
        private const string BundledFfmpegResource = "LiveBoard.Resources.ffmpeg.exe";
        private const string DouyinUserAgent = "Mozilla/5.0 (Windows NT 10.0; WOW64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/116.0.5845.97 Safari/537.36 Core/1.116.567.400 QQBrowser/19.7.6764.400";
        private static readonly object FfmpegExtractionLock = new object();
        private readonly BilibiliService _bilibili;
        private readonly CookieContainer _douyinCookies = new CookieContainer();
        private readonly SemaphoreSlim _douyinSessionLock = new SemaphoreSlim(1, 1);

        public RecordingService(BilibiliService bilibili)
        {
            if (bilibili == null)
                throw new ArgumentNullException("bilibili");
            _bilibili = bilibili;
        }

        public static string EnsureBundledFfmpeg()
        {
            var toolsDirectory = GetWritableToolsDirectory();
            var targetPath = Path.Combine(toolsDirectory, "ffmpeg.exe");
            lock (FfmpegExtractionLock)
            {
                if (IsUsableFfmpeg(targetPath))
                    return targetPath;

                Directory.CreateDirectory(toolsDirectory);
                var temporaryPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(BundledFfmpegResource))
                    {
                        if (resource == null)
                            throw new InvalidOperationException("内置录制引擎资源不存在。");
                        using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        {
                            resource.CopyTo(output);
                            output.Flush();
                        }
                    }

                    if (File.Exists(targetPath))
                    {
                        try
                        {
                            File.Replace(temporaryPath, targetPath, null, true);
                        }
                        catch (PlatformNotSupportedException)
                        {
                            File.Delete(targetPath);
                            File.Move(temporaryPath, targetPath);
                        }
                    }
                    else
                    {
                        File.Move(temporaryPath, targetPath);
                    }
                }
                finally
                {
                    if (File.Exists(temporaryPath))
                        File.Delete(temporaryPath);
                }

                if (!IsUsableFfmpeg(targetPath))
                    throw new InvalidOperationException("内置录制引擎释放后无法使用。");
                return targetPath;
            }
        }

        private static string GetWritableToolsDirectory()
        {
            var roots = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Path.GetTempPath()
            };
            foreach (var root in roots)
            {
                if (string.IsNullOrWhiteSpace(root))
                    continue;
                var directory = Path.Combine(root, "LiveBoard", "tools");
                try
                {
                    Directory.CreateDirectory(directory);
                    return directory;
                }
                catch (UnauthorizedAccessException)
                {
                }
                catch (IOException)
                {
                }
            }
            throw new UnauthorizedAccessException("没有可写的录制引擎缓存目录。");
        }

        private static bool IsUsableFfmpeg(string path)
        {
            try
            {
                return File.Exists(path) && new FileInfo(path).Length > 10 * 1024 * 1024;
            }
            catch
            {
                return false;
            }
        }

        public async Task<LiveProbeResult> ProbeAsync(RoomConfig room, CancellationToken cancellationToken)
        {
            if (room == null || string.IsNullOrWhiteSpace(room.RoomId))
                return new LiveProbeResult { HasError = true, Message = "房间号无效" };
            if (string.Equals(room.Platform, "Bilibili", StringComparison.OrdinalIgnoreCase))
            {
                var bilibiliResult = await _bilibili.ProbeAsync(room.RoomId, room.Quality, cancellationToken);
                return new LiveProbeResult
                {
                    IsLive = bilibiliResult.IsLive,
                    HasError = bilibiliResult.HasError,
                    Message = bilibiliResult.Message,
                    StreamUrl = bilibiliResult.StreamUrl,
                    DisplayName = bilibiliResult.DisplayName,
                    RoomTitle = bilibiliResult.RoomTitle,
                    AvailableQualities = bilibiliResult.AvailableQualities
                };
            }
            try
            {
                var page = await FetchRoomPageAsync(room.RoomId, room.Quality, cancellationToken);
                return new LiveProbeResult
                {
                    IsLive = !string.IsNullOrWhiteSpace(page.StreamUrl),
                    Message = string.IsNullOrWhiteSpace(page.StreamUrl) ? "未开播" : "直播中",
                    StreamUrl = page.StreamUrl,
                    DisplayName = page.DisplayName,
                    RoomTitle = page.RoomTitle
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new LiveProbeResult { HasError = true, Message = ex.Message };
            }
        }

        public async Task<RecordingSession> StartAsync(RoomConfig room, string outputDirectory, string ffmpegPath, CancellationToken cancellationToken, int segmentIndex = 1)
        {
            if (room == null || string.IsNullOrWhiteSpace(room.RoomId))
                throw new InvalidOperationException("没有有效的直播间房间号。");
            if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath))
                throw new FileNotFoundException("内置录制引擎不可用。", ffmpegPath);

            string streamUrl;
            if (string.Equals(room.Platform, "Bilibili", StringComparison.OrdinalIgnoreCase))
            {
                var bilibiliResult = await _bilibili.ProbeAsync(room.RoomId, room.Quality, cancellationToken);
                if (bilibiliResult.HasError)
                    throw new InvalidOperationException(bilibiliResult.Message);
                streamUrl = bilibiliResult.StreamUrl;
            }
            else
            {
                var page = await FetchRoomPageAsync(room.RoomId, room.Quality, cancellationToken);
                streamUrl = page.StreamUrl;
            }
            if (string.IsNullOrWhiteSpace(streamUrl))
                throw new InvalidOperationException("没有获取到当前平台可用的直播流，可能尚未开播或平台暂时拒绝访问。");

            if (string.IsNullOrWhiteSpace(outputDirectory))
                outputDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            try
            {
                Directory.CreateDirectory(outputDirectory);
            }
            catch
            {
                outputDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
                Directory.CreateDirectory(outputDirectory);
            }

            var extension = GetExtension(room.OutputFormat);
            var stem = SanitizeFileName((string.IsNullOrWhiteSpace(room.DisplayName) ? "直播间" : room.DisplayName) + "_" + room.RoomId + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            var segmentMode = string.IsNullOrWhiteSpace(room.SegmentMode) ? (room.SegmentEnabled ? "时间" : "关闭") : room.SegmentMode;
            var timeSegmented = segmentMode == "时间";
            var sizeSegmented = segmentMode == "大小";
            var outputName = stem;
            if (timeSegmented)
                outputName += "_part_%03d";
            else if (sizeSegmented)
                outputName += "_part_" + Math.Max(1, segmentIndex).ToString("D3");
            var outputPath = Path.Combine(outputDirectory, outputName + "." + extension);
            var maxBytes = sizeSegmented ? (long)Math.Max(1, room.SegmentSizeMb <= 0 ? 2048 : room.SegmentSizeMb) * 1024L * 1024L : 0L;
            var arguments = BuildFfmpegArguments(streamUrl, outputPath, extension, segmentMode, room.SegmentMinutes, maxBytes, room.Platform);

            var info = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = arguments,
                WorkingDirectory = Path.GetDirectoryName(ffmpegPath),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            var process = new Process { StartInfo = info, EnableRaisingEvents = true };
            var session = new RecordingSession(process, streamUrl, outputPath, Math.Max(1, segmentIndex), maxBytes);
            process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs args) { session.AppendError(args.Data); };
            if (!process.Start())
            {
                process.Dispose();
                throw new InvalidOperationException("无法启动 ffmpeg.exe。");
            }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return session;
        }

        private async Task<RoomPageResult> FetchRoomPageAsync(string roomId, string quality, CancellationToken cancellationToken)
        {
            await EnsureDouyinSessionAsync(cancellationToken);
            var query = "aid=6383&app_name=douyin_web&live_id=1&device_platform=web&language=zh-CN&browser_language=zh-CN&browser_platform=Win32&browser_name=Chrome&browser_version=116.0.0.0&web_rid=" + Uri.EscapeDataString(roomId.Trim()) + "&msToken=";
            var api = "https://live.douyin.com/webcast/room/web/enter/?" + query + "&a_bogus=" + Uri.EscapeDataString(DouyinSignature.Sign(query, DouyinUserAgent));
            var request = CreateDouyinRequest(api, "application/json, text/plain, */*");
            request.Referer = "https://live.douyin.com/" + roomId;
            using (var response = (HttpWebResponse)await request.GetResponseAsync())
            using (var reader = new StreamReader(response.GetResponseStream()))
            {
                var json = await reader.ReadToEndAsync();
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(json))
                    throw new InvalidOperationException("抖音拒绝了直播状态请求，请稍后重试。");
                return ParseDouyinRoom(json, quality);
            }
        }

        private async Task EnsureDouyinSessionAsync(CancellationToken cancellationToken)
        {
            if (_douyinCookies.GetCookies(new Uri("https://live.douyin.com/"))["ttwid"] != null)
                return;
            await _douyinSessionLock.WaitAsync(cancellationToken);
            try
            {
                if (_douyinCookies.GetCookies(new Uri("https://live.douyin.com/"))["ttwid"] != null)
                    return;
                var request = CreateDouyinRequest("https://ttwid.bytedance.com/ttwid/union/register/", "application/json");
                request.Method = "POST";
                request.ContentType = "application/json";
                var payload = Encoding.UTF8.GetBytes("{\"region\":\"cn\",\"aid\":1768,\"needFid\":false,\"service\":\"www.douyin.com\",\"migrate_info\":{\"ticket\":\"\",\"source\":\"node\"},\"cbUrlProtocol\":\"https\",\"union\":true}");
                request.ContentLength = payload.Length;
                using (var stream = await request.GetRequestStreamAsync())
                    await stream.WriteAsync(payload, 0, payload.Length, cancellationToken);

                string registration;
                using (var response = (HttpWebResponse)await request.GetResponseAsync())
                using (var reader = new StreamReader(response.GetResponseStream()))
                    registration = await reader.ReadToEndAsync();
                cancellationToken.ThrowIfCancellationRequested();
                var serializer = new JavaScriptSerializer();
                var data = serializer.DeserializeObject(registration) as Dictionary<string, object>;
                var callback = ReadString(data, "redirect_url");
                Uri callbackUri;
                if (!Uri.TryCreate(callback, UriKind.Absolute, out callbackUri) || !callbackUri.Host.EndsWith("douyin.com", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("无法建立抖音直播检测会话。");
                using (var response = (HttpWebResponse)await CreateDouyinRequest(callbackUri.AbsoluteUri, "text/html,*/*").GetResponseAsync())
                {
                }
                if (_douyinCookies.GetCookies(new Uri("https://live.douyin.com/"))["ttwid"] == null)
                    throw new InvalidOperationException("无法建立抖音直播检测会话。");
            }
            finally
            {
                _douyinSessionLock.Release();
            }
        }

        private HttpWebRequest CreateDouyinRequest(string url, string accept)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = DouyinUserAgent;
            request.Accept = accept;
            request.Headers[HttpRequestHeader.AcceptLanguage] = "zh-CN,zh;q=0.9";
            request.CookieContainer = _douyinCookies;
            request.CachePolicy = new System.Net.Cache.RequestCachePolicy(System.Net.Cache.RequestCacheLevel.NoCacheNoStore);
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            request.Timeout = 20000;
            request.ReadWriteTimeout = 20000;
            return request;
        }

        private RoomPageResult ParseDouyinRoom(string json, string quality)
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 200 };
            var root = serializer.DeserializeObject(json) as Dictionary<string, object>;
            var data = ReadDictionary(root, "data");
            var rooms = data == null || !data.ContainsKey("data") ? null : data["data"] as object[];
            if (rooms == null || rooms.Length == 0)
                return new RoomPageResult();
            var room = rooms[0] as Dictionary<string, object>;
            if (room == null)
                throw new InvalidOperationException("抖音返回了无法识别的直播状态。");

            var user = ReadDictionary(data, "user") ?? ReadDictionary(room, "owner");
            var result = new RoomPageResult
            {
                DisplayName = CleanAnchorName(ReadString(user, "nickname")),
                RoomTitle = CleanRoomTitle(ReadString(room, "title"))
            };
            int status;
            if (!int.TryParse(ReadString(room, "status"), out status) || status != 2)
                return result;

            var stream = ReadDictionary(room, "stream_url");
            result.StreamUrl = ChooseDouyinStream(stream, quality);
            if (string.IsNullOrWhiteSpace(result.StreamUrl))
                throw new InvalidOperationException("直播正在进行，但抖音没有返回可录制的视频流。");
            return result;
        }

        private string ChooseDouyinStream(Dictionary<string, object> stream, string quality)
        {
            if (stream == null)
                return null;
            var urls = ReadDictionary(stream, "flv_pull_url");
            if (urls == null || urls.Count == 0)
                urls = ReadDictionary(stream, "hls_pull_url_map");
            if (urls == null || urls.Count == 0)
                return null;
            if (string.IsNullOrWhiteSpace(quality) || quality == "自动")
            {
                object preferred;
                var defaultResolution = ReadString(stream, "default_resolution");
                if (!string.IsNullOrWhiteSpace(defaultResolution) && urls.TryGetValue(defaultResolution, out preferred))
                    return preferred as string;
            }
            return ChooseQuality(urls.Values.OfType<string>().ToList(), quality);
        }

        private static Dictionary<string, object> ReadDictionary(Dictionary<string, object> source, string key)
        {
            object value;
            return source != null && source.TryGetValue(key, out value) ? value as Dictionary<string, object> : null;
        }

        private static string ReadString(Dictionary<string, object> source, string key)
        {
            object value;
            return source != null && source.TryGetValue(key, out value) && value != null ? Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) : null;
        }

        private string CleanRoomTitle(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;
            value = WebUtility.HtmlDecode(value).Trim();
            value = Regex.Replace(value, @"\\u([0-9a-fA-F]{4})", delegate(Match match)
            {
                return ((char)Convert.ToInt32(match.Groups[1].Value, 16)).ToString();
            });
            value = value.Replace("\\/", "/").Replace("\\\"", "\"").Trim();
            if (value.Length == 0 || value.Length > 240 ||
                value.Equals("null", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("undefined", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("false", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("广告投放", StringComparison.OrdinalIgnoreCase))
                return null;
            return value;
        }

        private string CleanAnchorName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;
            value = WebUtility.HtmlDecode(value).Trim();
            if (value.IndexOf("undefined", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.Equals("null", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("false", StringComparison.OrdinalIgnoreCase))
                return null;
            value = Regex.Replace(value, @"\s*[-_|].*抖音直播.*$", string.Empty, RegexOptions.IgnoreCase).Trim();
            value = Regex.Replace(value, @"的直播间$", string.Empty).Trim();
            if (value.Length == 0 || value.Length > 80 || value == "直播间" || value == "抖音直播")
                return null;
            return value;
        }

        private string ChooseQuality(List<string> candidates, string quality)
        {
            if (candidates == null || candidates.Count == 0)
                return null;
            var tokens = new string[0];
            switch (quality ?? "自动")
            {
                case "原画":
                    tokens = new[] { "uhd", "or4", "origin" };
                    break;
                case "蓝光":
                case "超清":
                    tokens = new[] { "uhd", "hd", "or4" };
                    break;
                case "高清":
                    tokens = new[] { "hd", "sd" };
                    break;
                case "标清":
                case "流畅":
                    tokens = new[] { "sd", "ld" };
                    break;
            }
            foreach (var token in tokens)
            {
                var match = candidates.FirstOrDefault(candidate => candidate.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0);
                if (match != null)
                    return match;
            }
            return candidates[0];
        }

        private string BuildFfmpegArguments(string streamUrl, string outputPath, string extension, string segmentMode, int segmentMinutes, long maxBytes, string platform)
        {
            var isBilibili = string.Equals(platform, "Bilibili", StringComparison.OrdinalIgnoreCase);
            var referer = isBilibili
                ? "https://live.bilibili.com/"
                : "https://live.douyin.com/";
            var origin = isBilibili ? "https://live.bilibili.com" : "https://live.douyin.com";
            var headers = "Referer: " + referer + "\r\n" +
                          "Origin: " + origin + "\r\n" +
                          "User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0.0.0 Safari/537.36\r\n";
            var args = "-y -hide_banner -loglevel warning -rw_timeout 20000000 -reconnect 1 -reconnect_streamed 1 -reconnect_delay_max 5 -fflags +genpts+discardcorrupt -headers \"" + headers + "\" -i \"" + streamUrl + "\" -c copy";
            if (segmentMode == "时间")
            {
                var minutes = Math.Max(1, segmentMinutes <= 0 ? 60 : segmentMinutes);
                args += " -f segment -segment_time " + (minutes * 60) + " -segment_format " + (extension == "ts" ? "mpegts" : extension);
                if (extension == "mp4")
                    args += " -segment_format_options movflags=+frag_keyframe+empty_moov+default_base_moof";
            }
            else if (segmentMode == "大小")
            {
                if (extension == "flv")
                    args += " -f flv";
                else if (extension == "ts")
                    args += " -f mpegts";
                else
                    args += " -movflags +frag_keyframe+empty_moov+default_base_moof";
            }
            else if (extension == "flv")
            {
                args += " -f flv";
            }
            else if (extension == "ts")
            {
                args += " -f mpegts";
            }
            else
            {
                args += " -movflags +frag_keyframe+empty_moov+default_base_moof";
            }
            return args + " \"" + outputPath + "\"";
        }

        private string GetExtension(string format)
        {
            if (string.Equals(format, "FLV", StringComparison.OrdinalIgnoreCase))
                return "flv";
            if (string.Equals(format, "TS", StringComparison.OrdinalIgnoreCase))
                return "ts";
            return "mp4";
        }

        private string SanitizeFileName(string value)
        {
            foreach (var invalid in Path.GetInvalidFileNameChars())
                value = value.Replace(invalid, '_');
            return value;
        }
    }
}
