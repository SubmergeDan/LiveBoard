using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

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
        public string[] AvailableQualities { get; set; }
    }

    internal sealed class RoomPageResult
    {
        public string StreamUrl { get; set; }
        public string DisplayName { get; set; }
    }

    public sealed class RecordingService
    {
        private const string BundledFfmpegResource = "LiveBoard.Resources.ffmpeg.exe";
        private static readonly object FfmpegExtractionLock = new object();
        private static readonly Regex StreamRegex = new Regex(
            @"https?://[^""'\s<>\\]+(?:\.m3u8|\.flv)[^""'\s<>\\]*",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private readonly BilibiliService _bilibili;

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
                    DisplayName = page.DisplayName
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
            var request = (HttpWebRequest)WebRequest.Create("https://live.douyin.com/" + roomId + "?_recording_helper=" + DateTime.UtcNow.Ticks);
            request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0.0.0 Safari/537.36";
            request.Accept = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8";
            request.Referer = "https://live.douyin.com/";
            request.CookieContainer = new CookieContainer();
            request.CachePolicy = new System.Net.Cache.RequestCachePolicy(System.Net.Cache.RequestCacheLevel.NoCacheNoStore);
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            request.Timeout = 20000;
            request.ReadWriteTimeout = 20000;

            using (var response = (HttpWebResponse)await request.GetResponseAsync())
            using (var reader = new StreamReader(response.GetResponseStream()))
            {
                var html = await reader.ReadToEndAsync();
                cancellationToken.ThrowIfCancellationRequested();
                html = DecodePageText(WebUtility.HtmlDecode(html).Replace("\\u0026", "&").Replace("\\/", "/"));
                var candidates = StreamRegex.Matches(html).Cast<Match>().Select(match => match.Value).Distinct().ToList();
                return new RoomPageResult
                {
                    StreamUrl = ChooseQuality(candidates, quality),
                    DisplayName = ExtractAnchorName(html)
                };
            }
        }

        private string DecodePageText(string value)
        {
            value = Regex.Replace(value, @"\\u([0-9a-fA-F]{4})", delegate(Match match)
            {
                return ((char)Convert.ToInt32(match.Groups[1].Value, 16)).ToString();
            });
            return value.Replace("\\\"", "\"");
        }

        private string ExtractAnchorName(string html)
        {
            var ownerIndex = html.IndexOf("\"owner\"", StringComparison.OrdinalIgnoreCase);
            if (ownerIndex >= 0)
            {
                var ownerSection = html.Substring(ownerIndex, Math.Min(12000, html.Length - ownerIndex));
                var ownerName = FirstValidNickname(ownerSection);
                if (!string.IsNullOrWhiteSpace(ownerName))
                    return ownerName;
            }

            var nickname = FirstValidNickname(html);
            if (!string.IsNullOrWhiteSpace(nickname))
                return nickname;

            var title = Regex.Match(html, @"<title[^>]*>(?<name>[^<]{1,160})</title>", RegexOptions.IgnoreCase);
            return title.Success ? CleanAnchorName(title.Groups["name"].Value) : null;
        }

        private string FirstValidNickname(string html)
        {
            var matches = Regex.Matches(html, @"""nickname""\s*:\s*""(?<name>[^""]{1,80})""", RegexOptions.IgnoreCase);
            foreach (Match match in matches)
            {
                var cleaned = CleanAnchorName(match.Groups["name"].Value);
                if (!string.IsNullOrWhiteSpace(cleaned))
                    return cleaned;
            }
            return null;
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
            var args = "-y -hide_banner -loglevel warning -rw_timeout 20000000 -reconnect 1 -reconnect_streamed 1 -reconnect_delay_max 5 -headers \"" + headers + "\" -i \"" + streamUrl + "\" -c copy";
            if (segmentMode == "时间")
            {
                var minutes = Math.Max(1, segmentMinutes <= 0 ? 60 : segmentMinutes);
                args += " -f segment -segment_time " + (minutes * 60) + " -reset_timestamps 1 -segment_format " + (extension == "ts" ? "mpegts" : extension);
            }
            else if (segmentMode == "大小")
            {
                args += " -fs " + Math.Max(1L, maxBytes);
                if (extension == "flv")
                    args += " -f flv";
                else if (extension == "ts")
                    args += " -f mpegts";
            }
            else if (extension == "flv")
            {
                args += " -f flv";
            }
            else if (extension == "ts")
            {
                args += " -f mpegts";
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
