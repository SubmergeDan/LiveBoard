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
        public bool PartialSuccess { get; set; }
        public bool Cancelled { get; set; }
        public string ErrorText { get; set; }
        public string LogText { get; set; }
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
        private const int MaxPageMediaAssets = 100;
        private static readonly object ToolLock = new object();
        private static readonly Regex UrlRegex = new Regex(@"https?://[^\s\]\)>]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex KuaishouUrlRegex = new Regex(@"https?(?::|\\u003[aA])(?:(?:\\/)|(?:\\u002[fF])){2}(?:[^\s\""'<>\\]|\\.)+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex GenericMediaUrlRegex = new Regex(@"(?<url>(?:https?:)?//[^\""'\s<>\\]+?\.(?:mp4|m3u8|mpd|flv|webm|mov)(?:\?[^\""'\s<>\\]*)?)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex VideoSourceRegex = new Regex(@"<(?:video|source)\b[^>]*\b(?:src|data-src)\s*=\s*(?:\""(?<url>[^\""\r\n]+)\""|'(?<url>[^'\r\n]+)'|(?<url>[^\s>]+))", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private readonly JavaScriptSerializer _serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        public async Task<MediaAnalysisResult> AnalyzeAsync(string input, string cookieBrowser, string proxy, string bilibiliCookies, CancellationToken cancellationToken, Action<string> progress)
        {
            var url = ExtractUrl(input);
            if (string.IsNullOrWhiteSpace(url))
                return Failure(null, "没有识别到有效的网址。");
            var effectiveProxy = ResolveProxy(proxy, url);

            var platform = DetectPlatform(url) ?? "网页";

            if (string.Equals(platform, "抖音", StringComparison.OrdinalIgnoreCase))
                url = await NormalizeDouyinUrlAsync(url, effectiveProxy, cancellationToken, progress);

            string cookiePath = null;
            try
            {
                cookiePath = CreateCookieFile(bilibiliCookies, platform == "Bilibili");
                if (platform == "快手")
                    return await AnalyzeKuaishouAsync(url, effectiveProxy, cancellationToken, progress);
                if (platform == "X" || platform == "Instagram")
                {
                    ReportProgress(progress, "正在读取帖子媒体");
                    var gallery = await AnalyzeGalleryAsync(url, platform, cookieBrowser, effectiveProxy, cancellationToken, progress);
                    if (gallery.Success && gallery.AssetCount > 0)
                    {
                        if (gallery.AssetCount == 1 && IsVideo(gallery.Assets[0]))
                        {
                            var video = await AnalyzeYtDlpAsync(url, platform, cookieBrowser, effectiveProxy, cookiePath, cancellationToken, progress);
                            if (video.Success)
                                return video;
                        }
                        return gallery;
                    }

                    var fallback = await AnalyzeYtDlpAsync(url, platform, cookieBrowser, effectiveProxy, cookiePath, cancellationToken, progress);
                    return fallback.Success ? fallback : Failure(url, CombineErrors(gallery.ErrorText, fallback.ErrorText));
                }

                if (string.Equals(platform, "网页", StringComparison.OrdinalIgnoreCase))
                {
                    var page = await AnalyzeGenericWebPageAsync(url, effectiveProxy, cancellationToken, progress);
                    if (page.Success && page.AssetCount > 0)
                        return page;

                    var ytDlp = await AnalyzeYtDlpAsync(url, platform, cookieBrowser, effectiveProxy, cookiePath, cancellationToken, progress);
                    if (ytDlp.Success)
                        return ytDlp;
                    return page.Success ? page : Failure(url, CombineErrors(ytDlp.ErrorText, page.ErrorText));
                }

                return await AnalyzeYtDlpAsync(url, platform, cookieBrowser, effectiveProxy, cookiePath, cancellationToken, progress);
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
                var effectiveProxy = ResolveProxy(proxy, GetDownloadUrl(analysis));
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
                    AddProxyArgument(arguments, effectiveProxy);
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
                        "--ignore-config", "--no-warnings", "--playlist-end", MaxPageMediaAssets.ToString(CultureInfo.InvariantCulture), "--no-colors", "--newline",
                        "--encoding", "utf-8", "--socket-timeout", "30", "--ffmpeg-location", Path.GetDirectoryName(ffmpegPath),
                        "--windows-filenames", "--trim-filenames", "120",
                        "--no-mtime", "--no-overwrites", "--merge-output-format", "mp4",
                        "--progress", "--progress-delta", "0.2",
                        "--progress-template", "download:__RH_PROGRESS__%(progress.downloaded_bytes)s|%(progress.total_bytes)s|%(progress.total_bytes_estimate)s|%(progress._percent_str)s|%(progress._speed_str)s|%(progress._eta_str)s",
                        "--print", "after_move:__RH_OUTPUT__%(filepath)s", "-P", outputDirectory,
                        "-o", "%(autonumber)03d_%(title).80B_%(id).32B.%(ext)s"
                    };
                    // Direct page assets are already resolved to media URLs. Reading a
                    // browser cookie database here can fail while the browser is open and
                    // is unnecessary for public direct streams.
                    if (!string.Equals(analysis.Engine, "direct", StringComparison.OrdinalIgnoreCase))
                        AddCookieArguments(arguments, cookieBrowser, cookiePath);
                    if (string.Equals(analysis.Engine, "direct", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(analysis.Url))
                    {
                        arguments.Add("--ignore-errors");
                        arguments.Add("--continue");
                        arguments.Add("--retries");
                        arguments.Add("20");
                        arguments.Add("--fragment-retries");
                        arguments.Add("20");
                        arguments.Add("--retry-sleep");
                        arguments.Add("1");
                        arguments.Add("--http-chunk-size");
                        arguments.Add("10M");
                        arguments.Add("--referer");
                        arguments.Add(analysis.Url);
                    }
                    AddProxyArgument(arguments, effectiveProxy);
                    if (!string.Equals(analysis.Engine, "direct", StringComparison.OrdinalIgnoreCase))
                    {
                        var selector = format == null || string.IsNullOrWhiteSpace(format.Selector) ? "bestvideo+bestaudio/best" : format.Selector;
                        arguments.Add("-f");
                        arguments.Add(selector);
                    }
                    foreach (var downloadUrl in GetDownloadUrls(analysis))
                        arguments.Add(downloadUrl);
                    ReportProgress(progress, "正在下载视频");
                    run = await RunToolAsync(ytPath, arguments, cancellationToken, progress);
                }

                if (run.Cancelled || cancellationToken.IsCancellationRequested)
                    return new MediaExportResult { Cancelled = true, OutputDirectory = outputDirectory };
                var after = SafeFileCount(outputDirectory);
                var count = Math.Max(Math.Max(0, after - before), CountCompletedOutputs(run.StandardOutput));
                if (count > 0 && (run.ExitCode != 0 || HasToolErrors(run.StandardError)))
                {
                    return new MediaExportResult
                    {
                        Success = true,
                        PartialSuccess = true,
                        ErrorText = MapError(run.StandardError, analysis.Platform),
                        LogText = BuildToolLog(run),
                        OutputDirectory = outputDirectory,
                        DownloadedCount = count
                    };
                }
                if (run.ExitCode != 0)
                {
                    return new MediaExportResult
                    {
                        ErrorText = MapError(run.StandardError, analysis.Platform),
                        LogText = BuildToolLog(run),
                        OutputDirectory = outputDirectory
                    };
                }
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
                return new MediaExportResult
                {
                    ErrorText = MapError(ex.Message, analysis.Platform),
                    LogText = ex.ToString(),
                    OutputDirectory = outputDirectory
                };
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
                    "--ignore-config", "--dump-single-json", "--skip-download", "--no-warnings", "--playlist-end", MaxPageMediaAssets.ToString(CultureInfo.InvariantCulture), "--no-colors",
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
                var entries = AsEnumerable(GetValue(root, "entries"))
                    .Select(value => value as Dictionary<string, object>)
                    .Where(value => value != null)
                    .Take(MaxPageMediaAssets)
                    .ToList();
                var primary = entries.FirstOrDefault() ?? root;
                var result = new MediaAnalysisResult
                {
                    Success = true,
                    Platform = platform,
                    Url = url,
                    Engine = "yt-dlp",
                    Title = FirstString(root, "title", "fulltitle") ?? FirstString(primary, "title", "fulltitle", "id")
                };
                if (entries.Count == 0)
                {
                    result.Assets.Add(CreateYtDlpAsset(root, 1));
                    BuildFormatOptions(root, result.Formats);
                    if (result.Formats.Count == 0)
                        result.Formats.Add(new MediaFormatOption { FormatId = "best", Selector = "bestvideo+bestaudio/best", Label = "最佳可用画质" });
                }
                else
                {
                    for (var index = 0; index < entries.Count; index++)
                        result.Assets.Add(CreateYtDlpAsset(entries[index], index + 1));
                }
                result.AssetCount = result.Assets.Count;
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

        private static MediaAssetInfo CreateYtDlpAsset(Dictionary<string, object> values, int index)
        {
            return new MediaAssetInfo
            {
                Index = index,
                Type = "视频",
                Extension = FirstString(values, "ext") ?? "mp4",
                Url = FirstString(values, "webpage_url", "original_url"),
                Width = GetInt(values, "width"),
                Height = GetInt(values, "height")
            };
        }

        private async Task<MediaAnalysisResult> AnalyzeGenericWebPageAsync(string url, string proxy, CancellationToken cancellationToken, Action<string> progress)
        {
            ReportProgress(progress, "正在扫描网页中的媒体资源");
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
                        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
                        using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                        {
                            response.EnsureSuccessStatusCode();
                            var html = await ReadPageTextAsync(response.Content, cancellationToken);
                            var baseUri = response.RequestMessage == null ? new Uri(url) : response.RequestMessage.RequestUri;
                            var mediaUrls = ExtractGenericMediaUrls(html, baseUri);
                            if (mediaUrls.Count == 0)
                                return Failure(url, "网页未包含可直接导出的媒体流；动态加载、登录限制或 DRM 加密的视频无法通过网页源码提取。");

                            var title = ExtractOpenGraphValue(html, "og:title");
                            if (string.IsNullOrWhiteSpace(title))
                            {
                                var titleMatch = Regex.Match(html, @"<title[^>]*>(?<value>[\s\S]{1,500}?)</title>", RegexOptions.IgnoreCase);
                                title = titleMatch.Success ? WebUtility.HtmlDecode(titleMatch.Groups["value"].Value).Trim() : null;
                            }
                            if (string.IsNullOrWhiteSpace(title))
                                title = baseUri.Host + " 网页媒体";

                            var result = new MediaAnalysisResult
                            {
                                Success = true,
                                Platform = "网页",
                                Url = url,
                                Engine = "direct",
                                Title = title.Replace("\r", " ").Replace("\n", " ").Trim()
                            };
                            for (var index = 0; index < mediaUrls.Count; index++)
                            {
                                var extension = ExtensionFromUrl(mediaUrls[index]);
                                if (string.Equals(extension, "m3u8", StringComparison.OrdinalIgnoreCase) || string.Equals(extension, "mpd", StringComparison.OrdinalIgnoreCase))
                                    extension = "mp4";
                                result.Assets.Add(new MediaAssetInfo
                                {
                                    Index = index + 1,
                                    Type = "视频",
                                    Extension = string.IsNullOrWhiteSpace(extension) ? "媒体流" : extension.ToLowerInvariant(),
                                    Url = mediaUrls[index]
                                });
                            }
                            result.AssetCount = result.Assets.Count;
                            return result;
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
                return Failure(url, MapError(ex.Message, "网页"));
            }
        }

        private static async Task<string> ReadPageTextAsync(HttpContent content, CancellationToken cancellationToken)
        {
            const int maximumBytes = 8 * 1024 * 1024;
            if (content == null)
                return string.Empty;
            if (content.Headers.ContentLength.HasValue && content.Headers.ContentLength.Value > maximumBytes)
                throw new InvalidDataException("网页内容过大，无法安全扫描媒体地址。");

            using (var input = await content.ReadAsStreamAsync())
            using (var output = new MemoryStream())
            {
                var buffer = new byte[81920];
                while (true)
                {
                    var read = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                    if (read == 0)
                        break;
                    if (output.Length + read > maximumBytes)
                        throw new InvalidDataException("网页内容过大，无法安全扫描媒体地址。");
                    output.Write(buffer, 0, read);
                }
                var charset = content.Headers.ContentType == null ? null : content.Headers.ContentType.CharSet;
                Encoding encoding;
                try { encoding = string.IsNullOrWhiteSpace(charset) ? Encoding.UTF8 : Encoding.GetEncoding(charset.Trim('\"')); }
                catch { encoding = Encoding.UTF8; }
                return encoding.GetString(output.ToArray());
            }
        }

        private static List<string> ExtractGenericMediaUrls(string html, Uri baseUri)
        {
            var urls = new List<string>();
            var knownUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Action<string, bool> add = delegate(string candidate, bool allowUnqualified)
            {
                var value = DecodeGenericMediaUrl(candidate);
                if (string.IsNullOrWhiteSpace(value))
                    return;
                Uri mediaUri;
                if (!Uri.TryCreate(baseUri, value, out mediaUri) ||
                    (mediaUri.Scheme != Uri.UriSchemeHttp && mediaUri.Scheme != Uri.UriSchemeHttps) ||
                    (!allowUnqualified && !IsGenericVideoUrl(mediaUri)))
                    return;
                var normalized = mediaUri.AbsoluteUri;
                if (knownUrls.Add(normalized))
                    urls.Add(normalized);
            };

            foreach (var property in new[] { "og:video", "og:video:url", "og:video:secure_url", "twitter:player:stream" })
                add(ExtractOpenGraphValue(html, property), true);
            foreach (Match match in VideoSourceRegex.Matches(html ?? string.Empty))
                add(match.Groups["url"].Value, true);
            var decoded = DecodeGenericMediaUrl(html);
            foreach (Match match in GenericMediaUrlRegex.Matches(decoded ?? string.Empty))
                add(match.Groups["url"].Value, false);
            return urls.Take(MaxPageMediaAssets).ToList();
        }

        private static string DecodeGenericMediaUrl(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;
            value = WebUtility.HtmlDecode(value).Replace("\\/", "/");
            value = Regex.Replace(value, @"\\u(?<code>[0-9a-fA-F]{4})", delegate(Match match)
            {
                return ((char)Convert.ToInt32(match.Groups["code"].Value, 16)).ToString();
            });
            return value.Trim().Trim('\"', '\'', '\\');
        }

        private static bool IsGenericVideoUrl(Uri uri)
        {
            var extension = ExtensionFromUrl(uri.AbsolutePath);
            return string.Equals(extension, "mp4", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(extension, "m3u8", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(extension, "mpd", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(extension, "flv", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(extension, "webm", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(extension, "mov", StringComparison.OrdinalIgnoreCase);
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

        private static string ResolveProxy(string configuredProxy, string targetUrl)
        {
            if (!string.IsNullOrWhiteSpace(configuredProxy))
                return configuredProxy.Trim();
            Uri target;
            if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out target))
                return null;
            try
            {
                var systemProxy = WebRequest.GetSystemWebProxy();
                var proxy = systemProxy == null ? null : systemProxy.GetProxy(target);
                if (proxy == null || Uri.Compare(proxy, target, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0)
                    return null;
                return proxy.AbsoluteUri;
            }
            catch
            {
                return null;
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
            if (value.IndexOf("IncompleteRead", StringComparison.OrdinalIgnoreCase) >= 0 || value.IndexOf("more expected", StringComparison.OrdinalIgnoreCase) >= 0)
                return "媒体服务器提前中断了数据传输；LiveBoard 已自动续传重试，仍未完成的媒体可稍后再次导出。";
            if (value.Length == 0) return "平台没有返回可下载的媒体。";
            return value.Length > 220 ? value.Substring(0, 220) + "…" : value;
        }

        private static string BuildToolLog(MediaToolResult run)
        {
            if (run == null)
                return string.Empty;
            var builder = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(run.StandardError))
            {
                builder.AppendLine("[stderr]");
                builder.AppendLine(run.StandardError.Trim());
            }
            if (!string.IsNullOrWhiteSpace(run.StandardOutput))
            {
                if (builder.Length > 0)
                    builder.AppendLine();
                builder.AppendLine("[stdout]");
                builder.AppendLine(run.StandardOutput.Trim());
            }
            builder.AppendLine();
            builder.AppendLine("退出码: " + run.ExitCode.ToString(CultureInfo.InvariantCulture));
            return builder.ToString().Trim();
        }

        private static int CountCompletedOutputs(string output)
        {
            return Regex.Matches(output ?? string.Empty, @"(?m)^__RH_OUTPUT__").Count;
        }

        private static bool HasToolErrors(string error)
        {
            return Regex.IsMatch(error ?? string.Empty, @"(?m)^ERROR:");
        }

        private static string GetDownloadUrl(MediaAnalysisResult analysis)
        {
            if (analysis != null && string.Equals(analysis.Engine, "direct", StringComparison.OrdinalIgnoreCase) && analysis.Assets.Count > 0 && !string.IsNullOrWhiteSpace(analysis.Assets[0].Url))
                return analysis.Assets[0].Url;
            return analysis == null ? null : analysis.Url;
        }

        private static IEnumerable<string> GetDownloadUrls(MediaAnalysisResult analysis)
        {
            if (analysis == null)
                return Enumerable.Empty<string>();
            if (string.Equals(analysis.Engine, "direct", StringComparison.OrdinalIgnoreCase))
            {
                return analysis.Assets
                    .Where(asset => asset != null && !string.IsNullOrWhiteSpace(asset.Url))
                    .Select(asset => asset.Url)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(MaxPageMediaAssets)
                    .ToList();
            }
            return string.IsNullOrWhiteSpace(analysis.Url) ? Enumerable.Empty<string>() : new[] { analysis.Url };
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
            return string.Equals(extension, "mp4", StringComparison.OrdinalIgnoreCase) || string.Equals(extension, "webm", StringComparison.OrdinalIgnoreCase) || string.Equals(extension, "mov", StringComparison.OrdinalIgnoreCase) || string.Equals(extension, "m3u8", StringComparison.OrdinalIgnoreCase) || string.Equals(extension, "mpd", StringComparison.OrdinalIgnoreCase) || string.Equals(extension, "flv", StringComparison.OrdinalIgnoreCase);
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
