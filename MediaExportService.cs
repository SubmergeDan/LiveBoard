using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace LiveBoard
{
    public sealed class MediaFormatOption
    {
        public string FormatId { get; set; }
        public string Selector { get; set; }
        public string Label { get; set; }
        public bool HasAudio { get; set; }

        public override string ToString()
        {
            return Label ?? FormatId;
        }
    }

    public sealed class MediaAssetInfo
    {
        public int Index { get; set; }
        public string Type { get; set; }
        public string Extension { get; set; }
        public string Url { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public string Summary
        {
            get
            {
                var dimensions = Width > 0 && Height > 0 ? " · " + Width + "×" + Height : string.Empty;
                return (Type ?? "媒体") + dimensions;
            }
        }
    }

    public sealed class MediaAnalysisResult
    {
        public bool Success { get; set; }
        public string ErrorText { get; set; }
        public string Platform { get; set; }
        public string Title { get; set; }
        public string Url { get; set; }
        public string Engine { get; set; }
        public int AssetCount { get; set; }
        public List<MediaFormatOption> Formats { get; private set; }
        public List<MediaAssetInfo> Assets { get; private set; }

        public MediaAnalysisResult()
        {
            Formats = new List<MediaFormatOption>();
            Assets = new List<MediaAssetInfo>();
        }
    }

    public sealed class MediaExportResult
    {
        public bool Success { get; set; }
        public bool Cancelled { get; set; }
        public string ErrorText { get; set; }
        public string OutputDirectory { get; set; }
        public int DownloadedCount { get; set; }
    }

    internal sealed class MediaToolResult
    {
        public int ExitCode { get; set; }
        public bool Cancelled { get; set; }
        public string StandardOutput { get; set; }
        public string StandardError { get; set; }
    }

    public sealed class MediaExportService
    {
        private const string YtDlpResource = "LiveBoard.Resources.yt-dlp.exe";
        private const string GalleryDlpResource = "LiveBoard.Resources.gallery-dl.exe";
        private static readonly object ToolLock = new object();
        private static readonly Regex UrlRegex = new Regex(@"https?://[^\s\]\)>]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex KuaishouUrlRegex = new Regex(@"https?(?::|\\u003[aA])(?:(?:\\/)|(?:\\u002[fF])){2}(?:[^\s\""'<>\\]|\\.)+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private readonly JavaScriptSerializer _serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        public async Task<MediaAnalysisResult> AnalyzeAsync(string input, string cookieBrowser, string proxy, string bilibiliCookies, CancellationToken cancellationToken, Action<string> progress)
        {
            var url = ExtractUrl(input);
            if (string.IsNullOrWhiteSpace(url))
                return Failure(null, "没有识别到有效的网址。");

            var platform = DetectPlatform(url);
            if (platform == null)
                return Failure(url, "暂不支持这个网址，请使用抖音、快手、B站、X 或 Instagram 的公开分享地址。");

            if (string.Equals(platform, "抖音", StringComparison.OrdinalIgnoreCase))
                url = await NormalizeDouyinUrlAsync(url, proxy, cancellationToken, progress);

            string cookiePath = null;
            try
            {
                cookiePath = CreateCookieFile(bilibiliCookies, platform == "Bilibili");
                if (platform == "快手")
                    return await AnalyzeKuaishouAsync(url, proxy, cancellationToken, progress);
                if (platform == "X" || platform == "Instagram")
                {
                    ReportProgress(progress, "正在读取帖子媒体");
                    var gallery = await AnalyzeGalleryAsync(url, platform, cookieBrowser, proxy, cancellationToken, progress);
                    if (gallery.Success && gallery.AssetCount > 0)
                    {
                        if (gallery.AssetCount == 1 && IsVideo(gallery.Assets[0]))
                        {
                            var video = await AnalyzeYtDlpAsync(url, platform, cookieBrowser, proxy, cookiePath, cancellationToken, progress);
                            if (video.Success)
                                return video;
                        }
                        return gallery;
                    }

                    var fallback = await AnalyzeYtDlpAsync(url, platform, cookieBrowser, proxy, cookiePath, cancellationToken, progress);
                    return fallback.Success ? fallback : Failure(url, CombineErrors(gallery.ErrorText, fallback.ErrorText));
                }

                return await AnalyzeYtDlpAsync(url, platform, cookieBrowser, proxy, cookiePath, cancellationToken, progress);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Failure(url, MapError(ex.Message, platform));
            }
            finally
            {
                DeleteQuietly(cookiePath);
            }
        }

        public async Task<MediaExportResult> ExportAsync(MediaAnalysisResult analysis, MediaFormatOption format, string outputDirectory, string cookieBrowser, string proxy, string bilibiliCookies, CancellationToken cancellationToken, Action<string> progress)
        {
            if (analysis == null || !analysis.Success)
                return new MediaExportResult { ErrorText = "还没有可导出的媒体。" };
            if (string.IsNullOrWhiteSpace(outputDirectory))
                outputDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            Directory.CreateDirectory(outputDirectory);

            string cookiePath = null;
            try
            {
                cookiePath = CreateCookieFile(bilibiliCookies, string.Equals(analysis.Platform, "Bilibili", StringComparison.OrdinalIgnoreCase));
                var before = SafeFileCount(outputDirectory);
                MediaToolResult run;
                if (string.Equals(analysis.Engine, "gallery-dl", StringComparison.OrdinalIgnoreCase))
                {
                    var galleryPath = EnsureGalleryDlp();
                    var arguments = new List<string>
                    {
                        "--config-ignore", "--no-input", "--no-colors", "--windows-filenames", "--no-mtime",
                        "--range", "1-1000", "--directory", outputDirectory
                    };
                    AddCookieArguments(arguments, cookieBrowser, cookiePath);
                    AddProxyArgument(arguments, proxy);
                    arguments.Add(GetDownloadUrl(analysis));
                    ReportProgress(progress, "正在下载帖子媒体");
                    run = await RunToolAsync(galleryPath, arguments, cancellationToken, progress);
                }
                else
                {
                    var ytPath = EnsureYtDlp();
                    var ffmpegPath = RecordingService.EnsureBundledFfmpeg();
                    var arguments = new List<string>
                    {
                        "--ignore-config", "--no-warnings", "--no-playlist", "--no-colors", "--newline",
                        "--encoding", "utf-8", "--socket-timeout", "30", "--ffmpeg-location", Path.GetDirectoryName(ffmpegPath),
                        "--no-mtime", "--no-overwrites", "--merge-output-format", "mp4",
                        "--progress", "--progress-delta", "0.2",
                        "--progress-template", "download:__RH_PROGRESS__%(progress.downloaded_bytes)s|%(progress.total_bytes)s|%(progress.total_bytes_estimate)s|%(progress._percent_str)s|%(progress._speed_str)s|%(progress._eta_str)s",
                        "--print", "after_move:__RH_OUTPUT__%(filepath)s", "-P", outputDirectory,
                        "-o", "%(title).160B_%(id)s.%(ext)s"
                    };
                    AddCookieArguments(arguments, cookieBrowser, cookiePath);
                    AddProxyArgument(arguments, proxy);
                    var selector = format == null || string.IsNullOrWhiteSpace(format.Selector) ? "bestvideo+bestaudio/best" : format.Selector;
                    arguments.Add("-f");
                    arguments.Add(selector);
                    arguments.Add(GetDownloadUrl(analysis));
                    ReportProgress(progress, "正在下载视频");
                    run = await RunToolAsync(ytPath, arguments, cancellationToken, progress);
                }

                if (run.Cancelled || cancellationToken.IsCancellationRequested)
                    return new MediaExportResult { Cancelled = true, OutputDirectory = outputDirectory };
                if (run.ExitCode != 0)
                    return new MediaExportResult { ErrorText = MapError(run.StandardError, analysis.Platform), OutputDirectory = outputDirectory };

                var after = SafeFileCount(outputDirectory);
                var count = Math.Max(0, after - before);
                return new MediaExportResult
                {
                    Success = true,
                    OutputDirectory = outputDirectory,
                    DownloadedCount = count > 0 ? count : Math.Max(1, analysis.AssetCount)
                };
            }
            catch (OperationCanceledException)
            {
                return new MediaExportResult { Cancelled = true, OutputDirectory = outputDirectory };
            }
            catch (Exception ex)
            {
                return new MediaExportResult { ErrorText = MapError(ex.Message, analysis.Platform), OutputDirectory = outputDirectory };
            }
            finally
            {
                DeleteQuietly(cookiePath);
            }
        }

        public static string ExtractUrl(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return null;
            var match = UrlRegex.Match(input.Trim());
            if (!match.Success)
                return null;
            return match.Value.TrimEnd('.', ',', '，', '。', ';', '；', ')', ']', '》');
        }

        private async Task<string> NormalizeDouyinUrlAsync(string url, string proxy, CancellationToken cancellationToken, Action<string> progress)
        {
            var normalized = NormalizeDouyinNoteUrl(url);
            if (!string.Equals(normalized, url, StringComparison.OrdinalIgnoreCase))
                return normalized;

            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || !string.Equals(uri.Host, "v.douyin.com", StringComparison.OrdinalIgnoreCase))
                return url;

            try
            {
                ReportProgress(progress, "正在展开抖音分享链接");
                using (var handler = new HttpClientHandler { AllowAutoRedirect = true })
                {
                    if (!string.IsNullOrWhiteSpace(proxy))
                    {
                        handler.Proxy = new WebProxy(proxy.Trim());
                        handler.UseProxy = true;
                    }
                    using (var client = new HttpClient(handler))
                    {
                        client.Timeout = TimeSpan.FromSeconds(20);
                        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/136.0 Safari/537.36");
                        using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                        {
                            var finalUri = response.RequestMessage == null ? null : response.RequestMessage.RequestUri;
                            if (finalUri == null)
                                return url;
                            var finalUrl = NormalizeDouyinNoteUrl(finalUri.ToString());
                            return IsDouyinMediaUrl(finalUrl) ? finalUrl : url;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return url;
            }
        }

        private static string NormalizeDouyinNoteUrl(string url)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || !string.Equals(uri.Host, "www.douyin.com", StringComparison.OrdinalIgnoreCase))
                return url;

            var segments = uri.AbsolutePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2 || !string.Equals(segments[0], "note", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(segments[1]))
                return url;

            var builder = new UriBuilder(uri)
            {
                Path = "/video/" + segments[1],
                Query = string.Empty
            };
            return builder.Uri.ToString();
        }

        private static bool IsDouyinMediaUrl(string url)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || !string.Equals(uri.Host, "www.douyin.com", StringComparison.OrdinalIgnoreCase))
                return false;
            var path = uri.AbsolutePath.Trim('/');
            return path.StartsWith("video/", StringComparison.OrdinalIgnoreCase) || path.StartsWith("note/", StringComparison.OrdinalIgnoreCase);
        }

        private async Task<MediaAnalysisResult> AnalyzeYtDlpAsync(string url, string platform, string cookieBrowser, string proxy, string cookiePath, CancellationToken cancellationToken, Action<string> progress)
        {
            try
            {
                var path = EnsureYtDlp();
                var arguments = new List<string>
                {
                    "--ignore-config", "--dump-single-json", "--skip-download", "--no-warnings", "--no-playlist", "--no-colors",
                    "--encoding", "utf-8", "--socket-timeout", "30"
                };
                AddCookieArguments(arguments, cookieBrowser, cookiePath);
                AddProxyArgument(arguments, proxy);
                arguments.Add(url);
                ReportProgress(progress, "正在识别视频与画质");
                var run = await RunToolAsync(path, arguments, cancellationToken, progress);
                if (run.Cancelled || cancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException(cancellationToken);
                if (run.ExitCode != 0)
                    return Failure(url, MapError(run.StandardError, platform));

                var root = _serializer.DeserializeObject(run.StandardOutput) as Dictionary<string, object>;
                if (root == null)
                    return Failure(url, MapError(run.StandardError, platform));
                var result = new MediaAnalysisResult
                {
                    Success = true,
                    Platform = platform,
                    Url = url,
                    Engine = "yt-dlp",
                    Title = FirstString(root, "title", "fulltitle", "id")
                };
                var asset = new MediaAssetInfo
                {
                    Index = 1,
                    Type = "视频",
                    Extension = FirstString(root, "ext") ?? "mp4",
                    Width = GetInt(root, "width"),
                    Height = GetInt(root, "height")
                };
                result.Assets.Add(asset);
                result.AssetCount = 1;
                BuildFormatOptions(root, result.Formats);
                if (result.Formats.Count == 0)
                    result.Formats.Add(new MediaFormatOption { FormatId = "best", Selector = "bestvideo+bestaudio/best", Label = "最佳可用画质" });
                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Failure(url, MapError(ex.Message, platform));
            }
        }

        private async Task<MediaAnalysisResult> AnalyzeKuaishouAsync(string url, string proxy, CancellationToken cancellationToken, Action<string> progress)
        {
            ReportProgress(progress, "正在读取快手公开视频");
            try
            {
                using (var handler = new HttpClientHandler { AllowAutoRedirect = true })
                {
                    if (!string.IsNullOrWhiteSpace(proxy))
                    {
                        handler.Proxy = new WebProxy(proxy.Trim());
                        handler.UseProxy = true;
                    }
                    using (var client = new HttpClient(handler))
                    {
                        client.Timeout = TimeSpan.FromSeconds(30);
                        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/136.0 Safari/537.36");
                        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
                        using (var response = await client.GetAsync(url, cancellationToken))
                        {
                            response.EnsureSuccessStatusCode();
                            var html = await response.Content.ReadAsStringAsync();
                            var mediaUrl = ExtractKuaishouMediaUrl(html);
                            if (string.IsNullOrWhiteSpace(mediaUrl))
                                return Failure(url, "快手页面没有返回可下载的视频，请使用公开作品分享链接。");

                            var extension = ExtensionFromUrl(mediaUrl);
                            if (string.IsNullOrWhiteSpace(extension) || string.Equals(extension, "m3u8", StringComparison.OrdinalIgnoreCase))
                                extension = "mp4";
                            var title = ExtractOpenGraphValue(html, "og:title");
                            if (string.IsNullOrWhiteSpace(title))
                                title = "快手视频";
                            return new MediaAnalysisResult
                            {
                                Success = true,
                                Platform = "快手",
                                Url = url,
                                Engine = "direct",
                                Title = title.Trim(),
                                AssetCount = 1,
                                Assets =
                                {
                                    new MediaAssetInfo { Index = 1, Type = "视频", Extension = extension, Url = mediaUrl }
                                }
                            };
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Failure(url, MapError(ex.Message, "快手"));
            }
        }

        private async Task<MediaAnalysisResult> AnalyzeGalleryAsync(string url, string platform, string cookieBrowser, string proxy, CancellationToken cancellationToken, Action<string> progress)
        {
            try
            {
                var path = EnsureGalleryDlp();
                var arguments = new List<string>
                {
                    "--config-ignore", "--no-input", "--no-colors", "--range", "1-1000", "--dump-json"
                };
                AddCookieArguments(arguments, cookieBrowser, null);
                AddProxyArgument(arguments, proxy);
                arguments.Add(url);
                var run = await RunToolAsync(path, arguments, cancellationToken, progress);
                if (run.Cancelled || cancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException(cancellationToken);
                if (run.ExitCode != 0)
                    return Failure(url, MapError(run.StandardError, platform));

                var root = _serializer.DeserializeObject(run.StandardOutput) as object[];
                if (root == null)
                    return Failure(url, "没有读取到帖子媒体。");
                var result = new MediaAnalysisResult
                {
                    Success = true,
                    Platform = platform,
                    Url = url,
                    Engine = "gallery-dl"
                };
                var index = 1;
                foreach (var item in root)
                {
                    var values = item as object[];
                    if (values == null || values.Length < 2)
                        continue;
                    var code = GetInt(values[0]);
                    if (code == 2)
                    {
                        var meta = values[1] as Dictionary<string, object>;
                        result.Title = FirstString(meta, "content", "title", "description", "tweet_id");
                    }
                    else if (code == 3 && values.Length >= 3)
                    {
                        var meta = values[2] as Dictionary<string, object>;
                        var ext = FirstString(meta, "extension") ?? ExtensionFromUrl(values[1] as string);
                        var type = FirstString(meta, "type");
                        if (string.IsNullOrWhiteSpace(type))
                            type = IsVideoExtension(ext) ? "视频" : "图片";
                        result.Assets.Add(new MediaAssetInfo
                        {
                            Index = index++,
                            Type = type,
                            Extension = ext,
                            Url = values[1] as string,
                            Width = GetInt(meta, "width"),
                            Height = GetInt(meta, "height")
                        });
                    }
                }
                result.AssetCount = result.Assets.Count;
                if (string.IsNullOrWhiteSpace(result.Title))
                    result.Title = platform + " 帖子";
                if (result.AssetCount == 0)
                    return Failure(url, "没有读取到帖子媒体。");
                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Failure(url, MapError(ex.Message, platform));
            }
        }

        private void BuildFormatOptions(Dictionary<string, object> root, List<MediaFormatOption> formats)
        {
            var values = AsEnumerable(GetValue(root, "formats"));
            var candidates = new List<FormatCandidate>();
            foreach (var value in values)
            {
                var format = value as Dictionary<string, object>;
                if (format == null)
                    continue;
                var id = FirstString(format, "format_id");
                var vcodec = FirstString(format, "vcodec");
                var acodec = FirstString(format, "acodec");
                var width = GetInt(format, "width");
                var height = GetInt(format, "height");
                if (string.IsNullOrWhiteSpace(id) || width <= 0 || height <= 0 || string.Equals(vcodec, "none", StringComparison.OrdinalIgnoreCase))
                    continue;
                var shortSide = Math.Min(width, height);
                var note = FirstString(format, "format_note");
                var quality = QualityLabel(note, shortSide);
                var codecScore = vcodec.IndexOf("avc", StringComparison.OrdinalIgnoreCase) >= 0 || vcodec.IndexOf("h264", StringComparison.OrdinalIgnoreCase) >= 0 ? 300 :
                                 vcodec.IndexOf("hevc", StringComparison.OrdinalIgnoreCase) >= 0 || vcodec.IndexOf("h265", StringComparison.OrdinalIgnoreCase) >= 0 ? 200 : 100;
                var tbr = GetDouble(format, "tbr");
                candidates.Add(new FormatCandidate
                {
                    Id = id,
                    Label = quality,
                    Width = width,
                    Height = height,
                    HasAudio = !string.Equals(acodec, "none", StringComparison.OrdinalIgnoreCase),
                    Score = codecScore + (int)Math.Min(100, tbr / 100)
                });
            }

            formats.Add(new MediaFormatOption { FormatId = "best", Selector = "bestvideo+bestaudio/best", Label = "最佳可用画质" });
            foreach (var group in candidates.GroupBy(item => item.Label).OrderByDescending(item => item.Max(value => Math.Min(value.Width, value.Height))))
            {
                var selected = group.OrderByDescending(item => item.Score).First();
                formats.Add(new MediaFormatOption
                {
                    FormatId = selected.Id,
                    Selector = selected.HasAudio ? selected.Id : selected.Id + "+bestaudio/best",
                    Label = selected.Label + " · " + selected.Width + "×" + selected.Height,
                    HasAudio = selected.HasAudio
                });
            }
        }

        private sealed class FormatCandidate
        {
            public string Id;
            public string Label;
            public int Width;
            public int Height;
            public bool HasAudio;
            public int Score;
        }

        private async Task<MediaToolResult> RunToolAsync(string executable, IList<string> arguments, CancellationToken cancellationToken, Action<string> progress)
        {
            var info = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = string.Join(" ", arguments.Select(QuoteArgument).ToArray()),
                WorkingDirectory = Path.GetDirectoryName(executable),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            var output = new StringBuilder();
            var error = new StringBuilder();
            var completion = new TaskCompletionSource<int>();
            using (var process = new Process { StartInfo = info, EnableRaisingEvents = true })
            {
                process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs args)
                {
                    if (args.Data == null)
                        return;
                    lock (output) output.AppendLine(args.Data);
                    ReportProgress(progress, args.Data);
                };
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs args)
                {
                    if (args.Data == null)
                        return;
                    lock (error) error.AppendLine(args.Data);
                    ReportProgress(progress, args.Data);
                };
                process.Exited += delegate { completion.TrySetResult(process.ExitCode); };
                if (!process.Start())
                    throw new InvalidOperationException("无法启动媒体解析组件。");
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                using (cancellationToken.Register(delegate
                {
                    TerminateProcessTree(process);
                }))
                {
                    var exitCode = await completion.Task.ConfigureAwait(false);
                    process.WaitForExit();
                    return new MediaToolResult
                    {
                        ExitCode = exitCode,
                        Cancelled = cancellationToken.IsCancellationRequested,
                        StandardOutput = output.ToString(),
                        StandardError = error.ToString()
                    };
                }
            }
        }

        private static void TerminateProcessTree(Process process)
        {
            if (process == null)
                return;
            try
            {
                if (process.HasExited)
                    return;
                var taskKillPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "taskkill.exe");
                Process.Start(new ProcessStartInfo
                {
                    FileName = taskKillPath,
                    Arguments = "/PID " + process.Id.ToString(CultureInfo.InvariantCulture) + " /T /F",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
            }
            catch
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill();
                }
                catch
                {
                }
            }
        }

        private static string EnsureYtDlp()
        {
            return EnsureTool(YtDlpResource, "yt-dlp.exe", 5 * 1024 * 1024);
        }

        private static string EnsureGalleryDlp()
        {
            return EnsureTool(GalleryDlpResource, "gallery-dl.exe", 2 * 1024 * 1024);
        }

        private static string EnsureTool(string resourceName, string fileName, long minimumLength)
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiveBoard", "tools");
            Directory.CreateDirectory(directory);
            var target = Path.Combine(directory, fileName);
            lock (ToolLock)
            {
                var resource = typeof(MediaExportService).Assembly.GetManifestResourceStream(resourceName);
                if (resource == null)
                    throw new InvalidOperationException("内置媒体组件资源不存在。");
                using (resource)
                {
                    if (File.Exists(target) && new FileInfo(target).Length == resource.Length && new FileInfo(target).Length >= minimumLength)
                        return target;
                    var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        {
                            resource.CopyTo(output);
                            output.Flush();
                        }
                        if (File.Exists(target))
                        {
                            try { File.Replace(temporary, target, null, true); }
                            catch (PlatformNotSupportedException) { File.Delete(target); File.Move(temporary, target); }
                        }
                        else
                            File.Move(temporary, target);
                    }
                    finally
                    {
                        DeleteQuietly(temporary);
                    }
                }
            }
            return target;
        }

        private string CreateCookieFile(string cookieData, bool include)
        {
            if (!include || string.IsNullOrWhiteSpace(cookieData))
                return null;
            var path = Path.Combine(Path.GetTempPath(), "LiveBoard-media-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(path, cookieData, new UTF8Encoding(false));
            return path;
        }

        private static void AddCookieArguments(IList<string> arguments, string browser, string cookiePath)
        {
            if (!string.IsNullOrWhiteSpace(cookiePath))
            {
                arguments.Add("--cookies");
                arguments.Add(cookiePath);
            }
            else
            {
                var value = BrowserValue(browser);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    arguments.Add("--cookies-from-browser");
                    arguments.Add(value);
                }
            }
        }

        private static void AddProxyArgument(IList<string> arguments, string proxy)
        {
            if (!string.IsNullOrWhiteSpace(proxy))
            {
                arguments.Add("--proxy");
                arguments.Add(proxy.Trim());
            }
        }

        private static string BrowserValue(string browser)
        {
            if (string.Equals(browser, "Microsoft Edge", StringComparison.OrdinalIgnoreCase)) return "edge";
            if (string.Equals(browser, "Google Chrome", StringComparison.OrdinalIgnoreCase)) return "chrome";
            if (string.Equals(browser, "Mozilla Firefox", StringComparison.OrdinalIgnoreCase)) return "firefox";
            return null;
        }

        private static int SafeFileCount(string directory)
        {
            try { return Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly).Length; }
            catch { return 0; }
        }

        private static string DetectPlatform(string url)
        {
            var host = new Uri(url).Host.ToLowerInvariant();
            if (host.Contains("douyin.com")) return "抖音";
            if (host.Contains("kuaishou.com") || host.Contains("kuaishouapp.com")) return "快手";
            if (host.Contains("bilibili.com") || host == "b23.tv") return "Bilibili";
            if (host == "x.com" || host.EndsWith(".x.com") || host.Contains("twitter.com")) return "X";
            if (host.Contains("instagram.com")) return "Instagram";
            return null;
        }

        private static MediaAnalysisResult Failure(string url, string message)
        {
            return new MediaAnalysisResult { Success = false, Url = url, ErrorText = message };
        }

        private static string CombineErrors(string first, string second)
        {
            if (string.IsNullOrWhiteSpace(first)) return second;
            if (string.IsNullOrWhiteSpace(second)) return first;
            return first.IndexOf("登录", StringComparison.OrdinalIgnoreCase) >= 0 ? first : second;
        }

        private static string MapError(string text, string platform)
        {
            var value = (text ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            if (value.IndexOf("Fresh cookies", StringComparison.OrdinalIgnoreCase) >= 0 || value.IndexOf("cookies-from-browser", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (string.Equals(platform, "抖音", StringComparison.OrdinalIgnoreCase) || string.Equals(platform, "快手", StringComparison.OrdinalIgnoreCase) || string.Equals(platform, "X", StringComparison.OrdinalIgnoreCase))
                    return platform + " 暂时无法读取该媒体，可能受到平台风控或网络限制；登录来源不是必需项。";
                return platform + " 可能需要登录状态，请在登录来源中选择已登录的 Edge、Chrome 或 Firefox。";
            }
            if (value.IndexOf("empty media response", StringComparison.OrdinalIgnoreCase) >= 0 || value.IndexOf("redirect to login", StringComparison.OrdinalIgnoreCase) >= 0)
                return platform + " 当前需要登录或受到地区限制，请选择浏览器登录状态并保持代理可用。";
            if (value.Length == 0) return "平台没有返回可下载的媒体。";
            return value.Length > 220 ? value.Substring(0, 220) + "…" : value;
        }

        private static string GetDownloadUrl(MediaAnalysisResult analysis)
        {
            if (analysis != null && string.Equals(analysis.Engine, "direct", StringComparison.OrdinalIgnoreCase) && analysis.Assets.Count > 0 && !string.IsNullOrWhiteSpace(analysis.Assets[0].Url))
                return analysis.Assets[0].Url;
            return analysis == null ? null : analysis.Url;
        }

        private static string ExtractKuaishouMediaUrl(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
                return null;

            var candidates = new List<string>
            {
                ExtractOpenGraphValue(html, "og:video"),
                ExtractOpenGraphValue(html, "og:video:url"),
                ExtractOpenGraphValue(html, "og:video:secure_url")
            };
            candidates.AddRange(KuaishouUrlRegex.Matches(html).Cast<Match>().Select(match => match.Value));
            foreach (var candidate in candidates)
            {
                var value = DecodeKuaishouUrl(candidate);
                if (IsKuaishouMediaUrl(value))
                    return value;
            }
            return null;
        }

        private static string ExtractOpenGraphValue(string html, string property)
        {
            if (string.IsNullOrWhiteSpace(html))
                return null;
            var expression = @"<meta\b(?=[^>]*(?:property|name)\s*=\s*[\""']" + Regex.Escape(property) + @"[\""'])(?=[^>]*content\s*=\s*[\""'](?<value>[^\""']+)[\""'])[^>]*>";
            var match = Regex.Match(html, expression, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return match.Success ? WebUtility.HtmlDecode(match.Groups["value"].Value).Trim() : null;
        }

        private static string DecodeKuaishouUrl(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;
            value = WebUtility.HtmlDecode(value.Trim()).Replace("\\/", "/");
            value = Regex.Replace(value, @"\\u(?<code>[0-9a-fA-F]{4})", delegate(Match match)
            {
                return ((char)Convert.ToInt32(match.Groups["code"].Value, 16)).ToString();
            });
            return value.Trim('"', '\'', '\\');
        }

        private static bool IsKuaishouMediaUrl(string value)
        {
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return false;
            var path = uri.AbsolutePath.ToLowerInvariant();
            if (path.EndsWith(".jpg") || path.EndsWith(".jpeg") || path.EndsWith(".png") || path.EndsWith(".webp") || path.EndsWith(".gif"))
                return false;
            if (path.IndexOf(".mp4", StringComparison.Ordinal) >= 0 || path.IndexOf(".m3u8", StringComparison.Ordinal) >= 0)
                return true;
            var host = uri.Host.ToLowerInvariant();
            return (host.Contains("kwimgs") || host.Contains("kwaicdn") || host.Contains("yximgs")) &&
                   (path.IndexOf("video", StringComparison.Ordinal) >= 0 || path.IndexOf("play", StringComparison.Ordinal) >= 0 || path.IndexOf("mov", StringComparison.Ordinal) >= 0);
        }

        private static bool IsVideo(MediaAssetInfo asset)
        {
            return asset != null && (string.Equals(asset.Type, "video", StringComparison.OrdinalIgnoreCase) || string.Equals(asset.Type, "视频", StringComparison.OrdinalIgnoreCase) || IsVideoExtension(asset.Extension));
        }

        private static bool IsVideoExtension(string extension)
        {
            return string.Equals(extension, "mp4", StringComparison.OrdinalIgnoreCase) || string.Equals(extension, "webm", StringComparison.OrdinalIgnoreCase) || string.Equals(extension, "mov", StringComparison.OrdinalIgnoreCase) || string.Equals(extension, "m3u8", StringComparison.OrdinalIgnoreCase);
        }

        private static string ExtensionFromUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "";
            var clean = url.Split('?')[0];
            var ext = Path.GetExtension(clean);
            return ext == null ? "" : ext.TrimStart('.');
        }

        private static string QualityLabel(string note, int shortSide)
        {
            if (!string.IsNullOrWhiteSpace(note) && note.IndexOf("P", StringComparison.OrdinalIgnoreCase) >= 0)
                return note.Trim();
            if (shortSide >= 2000) return "4K";
            if (shortSide >= 1300) return "2K";
            if (shortSide >= 950) return "1080P";
            if (shortSide >= 650) return "720P";
            if (shortSide >= 430) return "480P";
            return shortSide + "P";
        }

        private static IEnumerable<object> AsEnumerable(object value)
        {
            var enumerable = value as IEnumerable;
            if (enumerable == null) return Enumerable.Empty<object>();
            return enumerable.Cast<object>();
        }

        private static object GetValue(Dictionary<string, object> dictionary, string key)
        {
            object value;
            return dictionary != null && dictionary.TryGetValue(key, out value) ? value : null;
        }

        private static int GetInt(Dictionary<string, object> dictionary, string key)
        {
            return GetInt(GetValue(dictionary, key));
        }

        private static int GetInt(object value)
        {
            if (value == null) return 0;
            try { return Convert.ToInt32(value); }
            catch { int result; return int.TryParse(Convert.ToString(value), out result) ? result : 0; }
        }

        private static double GetDouble(Dictionary<string, object> dictionary, string key)
        {
            var value = GetValue(dictionary, key);
            if (value == null) return 0;
            try { return Convert.ToDouble(value); }
            catch { double result; return double.TryParse(Convert.ToString(value), out result) ? result : 0; }
        }

        private static string FirstString(Dictionary<string, object> dictionary, params string[] keys)
        {
            if (dictionary == null) return null;
            foreach (var key in keys)
            {
                var value = GetValue(dictionary, key);
                if (value != null && !string.IsNullOrWhiteSpace(Convert.ToString(value)))
                    return Convert.ToString(value);
            }
            return null;
        }

        private static string QuoteArgument(string value)
        {
            if (value == null) return "\"\"";
            if (value.Length > 0 && value.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
                return value;
            var builder = new StringBuilder("\"");
            var slashes = 0;
            foreach (var character in value)
            {
                if (character == '\\') { slashes++; continue; }
                if (character == '"') builder.Append(new string('\\', slashes * 2 + 1)).Append('"');
                else { builder.Append(new string('\\', slashes)).Append(character); }
                slashes = 0;
            }
            builder.Append(new string('\\', slashes * 2)).Append('"');
            return builder.ToString();
        }

        private static void DeleteQuietly(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private static void ReportProgress(Action<string> progress, string value)
        {
            if (progress != null)
                progress(value);
        }
    }
}
