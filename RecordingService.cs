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
        public bool IsLive { get; set; }
        public bool HasError { get; set; }
        public string Message { get; set; }
        public string StreamUrl { get; set; }
        public string DisplayName { get; set; }
        public string RoomTitle { get; set; }
        public string[] AvailableQualities { get; set; }
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
                    IsLive = page.IsLive,
                    HasError = page.HasError,
                    Message = page.Message ?? (page.IsLive ? "直播中" : "未开播"),
                    StreamUrl = page.StreamUrl,
                    DisplayName = page.DisplayName,
                    RoomTitle = page.RoomTitle,
                    AvailableQualities = page.AvailableQualities
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
                if (string.IsNullOrWhiteSpace(streamUrl) && !string.IsNullOrWhiteSpace(page.Message))
                    throw new InvalidOperationException(page.Message);
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
            result.IsLive = true;
            result.AvailableQualities = BuildDouyinQualityLabels(stream);
            result.StreamUrl = ChooseDouyinStream(stream, quality);
            var requestedQuality = string.IsNullOrWhiteSpace(quality) ? "自动" : quality.Trim();
            result.HasError = string.IsNullOrWhiteSpace(result.StreamUrl);
            result.Message = result.HasError
                ? (requestedQuality == "自动"
                    ? "直播中，但抖音没有返回可录制的视频流。"
                    : "直播中，但没有返回所选画质：" + requestedQuality + "。")
                : "直播中 · " + GetDouyinStreamQuality(stream, result.StreamUrl);
            return result;
        }

        private string ChooseDouyinStream(Dictionary<string, object> stream, string quality)
        {
            if (stream == null)
                return null;
            var urls = GetDouyinStreamUrls(stream);
            if (urls.Count == 0)
                return null;
            return ChooseQuality(urls, quality);
        }

        private static Dictionary<string, object> GetDouyinStreamUrls(Dictionary<string, object> stream)
        {
            var urls = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (stream == null)
                return urls;
            AddDouyinUrls(urls, ReadDictionary(stream, "flv_pull_url"));
            AddDouyinUrls(urls, ReadDictionary(stream, "hls_pull_url_map"));

            var liveCore = ReadDictionary(stream, "live_core_sdk_data");
            AddDouyinOriginUrl(urls, ReadDictionary(liveCore, "pull_data"));

            var pullDatas = ReadDictionary(stream, "pull_datas");
            if (pullDatas != null)
            {
                foreach (var pair in pullDatas)
                    AddDouyinOriginUrl(urls, pair.Value as Dictionary<string, object>);
            }
            return urls;
        }

        private static void AddDouyinOriginUrl(Dictionary<string, object> target, Dictionary<string, object> pullData)
        {
            if (target == null || pullData == null)
                return;
            object rawStreamData;
            if (!pullData.TryGetValue("stream_data", out rawStreamData) || rawStreamData == null)
                return;

            var streamData = rawStreamData as Dictionary<string, object>;
            if (streamData == null && rawStreamData is string)
            {
                try
                {
                    var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 200 };
                    streamData = serializer.DeserializeObject(rawStreamData as string) as Dictionary<string, object>;
                }
                catch
                {
                    return;
                }
            }
            var data = ReadDictionary(streamData, "data");
            var origin = ReadDictionary(data, "origin");
            var main = ReadDictionary(origin, "main");
            if (main == null)
                return;

            var codec = ReadDouyinOriginCodec(main);
            AddDouyinOriginVariant(target, main, "flv", codec);
            AddDouyinOriginVariant(target, main, "hls", codec);
        }

        private static string ReadDouyinOriginCodec(Dictionary<string, object> main)
        {
            object rawSdkParams;
            if (main == null || !main.TryGetValue("sdk_params", out rawSdkParams) || rawSdkParams == null)
                return null;
            var parsed = rawSdkParams as Dictionary<string, object>;
            if (parsed != null)
                return ReadString(parsed, "VCodec") ?? ReadString(parsed, "vcodec");
            var sdkParams = rawSdkParams as string;
            if (string.IsNullOrWhiteSpace(sdkParams))
                return null;
            try
            {
                var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 50 };
                parsed = serializer.DeserializeObject(sdkParams) as Dictionary<string, object>;
                return ReadString(parsed, "VCodec") ?? ReadString(parsed, "vcodec");
            }
            catch
            {
                return null;
            }
        }

        private static void AddDouyinOriginVariant(Dictionary<string, object> target, Dictionary<string, object> main, string key, string codec)
        {
            var url = ReadString(main, key);
            if (string.IsNullOrWhiteSpace(url) || target.Any(pair =>
                GetDouyinQualityLevel(pair.Key, pair.Value as string) == "origin"))
                return;
            if (!string.IsNullOrWhiteSpace(codec) && url.IndexOf("codec=", StringComparison.OrdinalIgnoreCase) < 0)
                url += (url.IndexOf("?", StringComparison.Ordinal) >= 0 ? "&" : "?") + "codec=" + Uri.EscapeDataString(codec);
            target.Add("ORIGIN", url);
        }

        private static void AddDouyinUrls(Dictionary<string, object> target, Dictionary<string, object> source)
        {
            if (target == null || source == null)
                return;
            foreach (var pair in source)
            {
                if (pair.Value is string && !string.IsNullOrWhiteSpace(pair.Value as string) && !target.ContainsKey(pair.Key))
                    target.Add(pair.Key, pair.Value);
            }
        }

        private string[] BuildDouyinQualityLabels(Dictionary<string, object> stream)
        {
            if (stream == null)
                return new[] { "自动" };
            var urls = GetDouyinStreamUrls(stream);
            var labels = new List<string> { "自动" };
            foreach (var level in new[] { "origin", "blu_ray", "uhd", "hd", "sd", "ld" })
            {
                if (urls.Any(pair => !string.IsNullOrWhiteSpace(pair.Value as string) && GetDouyinQualityLevel(pair.Key, pair.Value as string) == level))
                    labels.Add(GetDouyinQualityLabel(level));
            }
            return labels.ToArray();
        }

        private string GetDouyinStreamQuality(Dictionary<string, object> stream, string selectedUrl)
        {
            if (stream == null || string.IsNullOrWhiteSpace(selectedUrl))
                return "未知画质";
            var urls = GetDouyinStreamUrls(stream);
            foreach (var pair in urls)
            {
                if (string.Equals(pair.Value as string, selectedUrl, StringComparison.Ordinal))
                    return GetDouyinQualityLabel(GetDouyinQualityLevel(pair.Key, selectedUrl));
            }
            return "未知画质";
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

        private string ChooseQuality(Dictionary<string, object> candidates, string quality)
        {
            if (candidates == null || candidates.Count == 0)
                return null;
            var requestedQuality = string.IsNullOrWhiteSpace(quality) ? "自动" : quality.Trim();
            var levels = new List<string>();
            switch (requestedQuality)
            {
                case "原画":
                    levels.Add("origin");
                    break;
                case "蓝光":
                    levels.Add("blu_ray");
                    break;
                case "超清":
                    levels.Add("uhd");
                    break;
                case "高清":
                    levels.Add("hd");
                    break;
                case "标清":
                    levels.Add("sd");
                    break;
                case "流畅":
                    levels.Add("ld");
                    break;
            }
            if (requestedQuality == "自动")
                levels.AddRange(new[] { "origin", "blu_ray", "uhd", "hd", "sd", "ld" });

            foreach (var level in levels)
            {
                foreach (var candidate in candidates)
                {
                    var url = candidate.Value as string;
                    if (!string.IsNullOrWhiteSpace(url) && GetDouyinQualityLevel(candidate.Key, url) == level)
                        return url;
                }
            }

            return null;
        }

        private static string GetDouyinQualityLabel(string level)
        {
            switch (level)
            {
                case "origin": return "原画";
                case "blu_ray": return "蓝光";
                case "uhd": return "超清";
                case "hd": return "高清";
                case "sd": return "标清";
                case "ld": return "流畅";
                default: return "未知画质";
            }
        }

        private static string GetDouyinQualityLevel(string key, string streamUrl)
        {
            var normalizedKey = (key ?? string.Empty).Trim().ToLowerInvariant().Replace('-', '_');
            var normalizedUrl = (streamUrl ?? string.Empty).ToLowerInvariant();

            // Douyin reuses FULL_HD1/SD1/SD2 for different templates. The URL
            // template is authoritative when present (or4, hd, sd, ld).
            if (Regex.IsMatch(normalizedUrl, @"(?:^|[_/])(?:origin|or4)(?:[._?&/-]|$)"))
                return "origin";
            if (Regex.IsMatch(normalizedUrl, @"(?:^|[_/])blue_?ray(?:[._?&/-]|$)"))
                return "blu_ray";
            if (Regex.IsMatch(normalizedUrl, @"(?:^|[_-])(?:ls)?uhd5?(?:[._?&/-]|$)"))
                return "blu_ray";
            if (Regex.IsMatch(normalizedUrl, @"(?:^|[_-])(?:ls)?hd5?(?:[._?&/-]|$)"))
                return "uhd";
            if (Regex.IsMatch(normalizedUrl, @"(?:^|[_-])(?:ls)?sd5?(?:[._?&/-]|$)"))
                return "hd";
            if (Regex.IsMatch(normalizedUrl, @"(?:^|[_-])(?:ls)?ld5?(?:[._?&/-]|$)"))
                return "sd";

            switch (normalizedKey)
            {
                case "origin":
                case "origion":
                case "or4":
                    return "origin";
                case "full_hd1":
                case "fullhd":
                case "full_hd":
                    return "blu_ray";
                case "blu_ray":
                case "blue_ray":
                case "blueray":
                case "blue-ray":
                    return "blu_ray";
                case "uhd":
                    return "uhd";
                case "hd1":
                    return "uhd";
                case "hd":
                    return "hd";
                case "sd1":
                    return "sd";
                case "sd":
                    return "sd";
                case "sd2":
                    return "hd";
                case "ld":
                    return "ld";
            }

            var source = normalizedKey + " " + (streamUrl ?? string.Empty).ToLowerInvariant();
            if (Regex.IsMatch(source, @"(?:^|[^a-z0-9])(origin|origion|or4)(?:[^a-z0-9]|$)"))
                return "origin";
            if (Regex.IsMatch(source, @"(?:^|[^a-z0-9])full_?hd1?(?:[^a-z0-9]|$)"))
                return "blu_ray";
            if (Regex.IsMatch(source, @"(?:^|[^a-z0-9])hd1(?:[^a-z0-9]|$)"))
                return "uhd";
            if (Regex.IsMatch(source, @"(?:^|[^a-z0-9])uhd(?:[^a-z0-9]|$)"))
                return "uhd";
            if (Regex.IsMatch(source, @"(?:^|[^a-z0-9])hd(?:[^a-z0-9]|$)"))
                return "hd";
            if (Regex.IsMatch(source, @"(?:^|[^a-z0-9])sd1(?:[^a-z0-9]|$)"))
                return "sd";
            if (Regex.IsMatch(source, @"(?:^|[^a-z0-9])sd2(?:[^a-z0-9]|$)"))
                return "hd";
            if (Regex.IsMatch(source, @"(?:^|[^a-z0-9])ld(?:[^a-z0-9]|$)"))
                return "ld";
            return normalizedKey;
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
