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
using System.Security.Cryptography;
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
        public long FileSize { get; set; }
        public bool FileSizeIsEstimated { get; set; }
        public double DurationSeconds { get; set; }
        public string ThumbnailUrl { get; set; }
        public string Codec { get; set; }
        public bool IsSelected { get; set; }
        public List<MediaFormatOption> Formats { get; private set; }
        public MediaFormatOption SelectedFormat { get; set; }
        public bool ShowInlineQuality { get; set; }

        public MediaAssetInfo()
        {
            IsSelected = true;
            Formats = new List<MediaFormatOption>();
        }

        public bool HasFormats { get { return ShowInlineQuality && Formats != null && Formats.Count > 0; } }

        public string DisplayType
        {
            get
            {
                var format = string.Equals(Extension, "m3u8", StringComparison.OrdinalIgnoreCase) ? "HLS" :
                             string.Equals(Extension, "mpd", StringComparison.OrdinalIgnoreCase) ? "DASH" :
                             string.IsNullOrWhiteSpace(Extension) ? null : Extension.ToUpperInvariant();
                return string.Join(" · ", new[] { Type ?? "媒体", format, Codec }
                    .Where(value => !string.IsNullOrWhiteSpace(value)).ToArray());
            }
        }

        public string Summary
        {
            get
            {
                var details = new List<string>();
                if (Width > 0 && Height > 0)
                    details.Add(Width + "×" + Height);
                if (FileSize > 0)
                    details.Add((FileSizeIsEstimated ? "约 " : string.Empty) + FormatFileSize(FileSize));
                if (string.Equals(Type, "视频", StringComparison.OrdinalIgnoreCase) || IsVideoExtension(Extension))
                {
                    if (DurationSeconds > 0)
                        details.Add(FormatDuration(DurationSeconds));
                }
                return string.Join(" · ", details.ToArray());
            }
        }

        private static string FormatFileSize(long bytes)
        {
            var value = (double)bytes;
            var units = new[] { "B", "KB", "MB", "GB", "TB" };
            var unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }
            return value.ToString(value >= 100 || unit == 0 ? "0" : "0.#", CultureInfo.InvariantCulture) + " " + units[unit];
        }

        private static string FormatDuration(double seconds)
        {
            var duration = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return duration.TotalHours >= 1 ? duration.ToString(@"h\:mm\:ss") : duration.ToString(@"m\:ss");
        }

        private static bool IsVideoExtension(string extension)
        {
            return new[] { "mp4", "webm", "mov", "m3u8", "mpd", "flv", "mkv" }
                .Contains(extension ?? string.Empty, StringComparer.OrdinalIgnoreCase);
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
        private const string DouyinUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/136.0 Safari/537.36";
        private const string DouyinSpiderUserAgent = "Mozilla/5.0 (compatible; Baiduspider/2.0; +http://www.baidu.com/search/spider.html)";
        private const string KuaishouUserAgent = "Mozilla/5.0 (Linux; Android 15; Pixel 9 Pro) AppleWebKit/537.36 Chrome/136.0 Mobile Safari/537.36";
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
            {
                url = await NormalizeDouyinUrlAsync(url, effectiveProxy, cancellationToken, progress);
                var modalId = GetDouyinModalId(url);
                if (!string.IsNullOrWhiteSpace(modalId))
                {
                    var noteUrl = "https://www.douyin.com/note/" + modalId;
                    MediaAnalysisResult note = null;
                    try
                    {
                        note = await AnalyzeDouyinNoteApiAsync(noteUrl, effectiveProxy, cancellationToken, progress);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                    }
                    if (note != null && note.Success)
                        return note;
                    url = "https://www.douyin.com/video/" + modalId;
                }
                if (IsDouyinNoteUrl(url))
                    return await AnalyzeDouyinNoteAsync(url, effectiveProxy, cancellationToken, progress);
            }

            string cookiePath = null;
            try
            {
                cookiePath = string.Equals(platform, "抖音", StringComparison.OrdinalIgnoreCase)
                    ? await CreateDouyinCookieFileAsync(effectiveProxy, cancellationToken, progress)
                    : CreateCookieFile(bilibiliCookies, platform == "Bilibili");
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
                    var ytDlp = await AnalyzeYtDlpAsync(url, platform, cookieBrowser, effectiveProxy, cookiePath, cancellationToken, progress);
                    if (ytDlp.Success)
                        return ytDlp;
                    var page = await AnalyzeGenericWebPageAsync(url, effectiveProxy, cancellationToken, progress);
                    if (page.Success && page.AssetCount > 0)
                        return page;
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

        public async Task<MediaExportResult> ExportAsync(MediaAnalysisResult analysis, IList<MediaAssetInfo> selectedAssets, MediaFormatOption format, string outputDirectory, string cookieBrowser, string proxy, string bilibiliCookies, CancellationToken cancellationToken, Action<string> progress)
        {
            if (analysis == null || !analysis.Success)
                return new MediaExportResult { ErrorText = "还没有可导出的媒体。" };
            selectedAssets = (selectedAssets ?? new List<MediaAssetInfo>()).Where(asset => asset != null).ToList();
            if (selectedAssets.Count == 0)
                return new MediaExportResult { ErrorText = "请至少选择一个媒体。" };
            if (string.IsNullOrWhiteSpace(outputDirectory))
                outputDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            Directory.CreateDirectory(outputDirectory);

            string cookiePath = null;
            try
            {
                var effectiveProxy = ResolveProxy(proxy, GetDownloadUrl(analysis, selectedAssets));
                if (string.Equals(analysis.Engine, "direct-image", StringComparison.OrdinalIgnoreCase))
                    return await ExportDirectImagesAsync(analysis, selectedAssets, outputDirectory, effectiveProxy, cancellationToken, progress);
                cookiePath = string.Equals(analysis.Platform, "抖音", StringComparison.OrdinalIgnoreCase)
                    ? await CreateDouyinCookieFileAsync(effectiveProxy, cancellationToken, progress)
                    : CreateCookieFile(bilibiliCookies, string.Equals(analysis.Platform, "Bilibili", StringComparison.OrdinalIgnoreCase));
                var before = SafeFileCount(outputDirectory);
                MediaToolResult run;
                if (string.Equals(analysis.Engine, "gallery-dl", StringComparison.OrdinalIgnoreCase))
                {
                    var galleryPath = EnsureGalleryDlp();
                    var arguments = new List<string>
                    {
                        "--config-ignore", "--no-input", "--no-colors", "--windows-filenames", "--no-mtime",
                        "--range", BuildItemSelection(selectedAssets), "--Print", "after:__RH_OUTPUT__", "--directory", outputDirectory
                    };
                    AddCookieArguments(arguments, cookieBrowser, cookiePath);
                    AddProxyArgument(arguments, effectiveProxy);
                    arguments.Add(analysis.Url);
                    ReportProgress(progress, "正在下载帖子媒体");
                    run = await RunToolAsync(galleryPath, arguments, cancellationToken, progress);
                }
                else
                {
                    var ytPath = EnsureYtDlp();
                    run = await DownloadYtDlpAssetsAsync(analysis, selectedAssets, format, ytPath, outputDirectory, cookieBrowser, cookiePath, effectiveProxy, cancellationToken, progress);
                }

                if (run.Cancelled || cancellationToken.IsCancellationRequested)
                    return new MediaExportResult { Cancelled = true, OutputDirectory = outputDirectory };
                var after = SafeFileCount(outputDirectory);
                var completedOutputs = CountCompletedOutputs(run.StandardOutput);
                var count = Math.Max(Math.Max(0, after - before), completedOutputs);
                count = Math.Min(count, selectedAssets.Count);
                if (count == selectedAssets.Count)
                {
                    return new MediaExportResult
                    {
                        Success = true,
                        OutputDirectory = outputDirectory,
                        DownloadedCount = count
                    };
                }
                if (count > 0)
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
                    DownloadedCount = selectedAssets.Count
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

        private async Task<MediaToolResult> DownloadYtDlpAssetsAsync(MediaAnalysisResult analysis, IList<MediaAssetInfo> selectedAssets, MediaFormatOption fallbackFormat, string ytPath, string outputDirectory, string cookieBrowser, string cookiePath, string proxy, CancellationToken cancellationToken, Action<string> progress)
        {
            var groups = string.Equals(analysis.Engine, "direct", StringComparison.OrdinalIgnoreCase)
                ? new List<IList<MediaAssetInfo>> { selectedAssets }
                : selectedAssets.GroupBy(asset => ResolveAssetFormat(asset, fallbackFormat).Selector ?? "bestvideo+bestaudio/best", StringComparer.OrdinalIgnoreCase)
                    .Select(group => (IList<MediaAssetInfo>)group.ToList()).ToList();
            var outputs = new StringBuilder();
            var errors = new StringBuilder();
            var exitCode = 0;
            foreach (var group in groups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ffmpegPath = RecordingService.EnsureBundledFfmpeg();
                var arguments = new List<string>
                {
                    "--ignore-config", "--no-warnings", "--playlist-end", MaxPageMediaAssets.ToString(CultureInfo.InvariantCulture), "--no-colors", "--newline",
                    "--encoding", "utf-8", "--socket-timeout", "30", "--ffmpeg-location", Path.GetDirectoryName(ffmpegPath),
                    "--windows-filenames", "--trim-filenames", "120", "--no-mtime", "--no-overwrites", "--merge-output-format", "mp4",
                    "--progress", "--progress-delta", "0.2",
                    "--progress-template", "download:__RH_PROGRESS__%(progress.downloaded_bytes)s|%(progress.total_bytes)s|%(progress.total_bytes_estimate)s|%(progress._percent_str)s|%(progress._speed_str)s|%(progress._eta_str)s",
                    "--print", "after_move:__RH_OUTPUT__%(filepath)s", "-P", outputDirectory,
                    "-o", "%(autonumber)03d_%(title).80B_%(id).32B.%(ext)s"
                };
                if (!string.Equals(analysis.Engine, "direct", StringComparison.OrdinalIgnoreCase))
                    AddCookieArguments(arguments, cookieBrowser, cookiePath);
                AddPlatformRequestArguments(arguments, analysis.Platform, analysis.Url);
                if (string.Equals(analysis.Engine, "yt-dlp-impersonate", StringComparison.OrdinalIgnoreCase))
                {
                    arguments.Add("--impersonate");
                    arguments.Add("Chrome-136:Macos-15");
                }
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
                AddProxyArgument(arguments, proxy);
                if (!string.Equals(analysis.Engine, "direct", StringComparison.OrdinalIgnoreCase) && analysis.AssetCount > 1)
                {
                    arguments.Add("--playlist-items");
                    arguments.Add(BuildItemSelection(group));
                }
                if (!string.Equals(analysis.Engine, "direct", StringComparison.OrdinalIgnoreCase))
                {
                    arguments.Add("-f");
                    arguments.Add(ResolveAssetFormat(group[0], fallbackFormat).Selector ?? "bestvideo+bestaudio/best");
                }
                foreach (var downloadUrl in string.Equals(analysis.Engine, "direct", StringComparison.OrdinalIgnoreCase)
                    ? group.Where(asset => !string.IsNullOrWhiteSpace(asset.Url)).Select(asset => asset.Url).Distinct(StringComparer.OrdinalIgnoreCase)
                    : new[] { analysis.Url })
                    arguments.Add(downloadUrl);
                ReportProgress(progress, groups.Count > 1 ? "正在按各视频画质下载" : "正在下载视频");
                var run = await RunToolAsync(ytPath, arguments, cancellationToken, progress);
                if (run.ExitCode != 0 && string.Equals(analysis.Platform, "Bilibili", StringComparison.OrdinalIgnoreCase) && IsCertificateValidationError(run))
                {
                    arguments.Insert(arguments.Count - 1, "--no-check-certificates");
                    ReportProgress(progress, "B站证书校验失败，正在使用兼容模式重试");
                    run = await RunToolAsync(ytPath, arguments, cancellationToken, progress);
                }
                if (!string.IsNullOrWhiteSpace(run.StandardOutput))
                    outputs.AppendLine(run.StandardOutput.Trim());
                if (!string.IsNullOrWhiteSpace(run.StandardError))
                    errors.AppendLine(run.StandardError.Trim());
                if (run.ExitCode != 0)
                    exitCode = run.ExitCode;
                if (run.Cancelled)
                    return run;
            }
            return new MediaToolResult
            {
                ExitCode = exitCode,
                StandardOutput = outputs.ToString(),
                StandardError = errors.ToString()
            };
        }

        private static MediaFormatOption ResolveAssetFormat(MediaAssetInfo asset, MediaFormatOption fallback)
        {
            return asset != null && asset.SelectedFormat != null
                ? asset.SelectedFormat
                : fallback ?? new MediaFormatOption { Selector = "bestvideo+bestaudio/best" };
        }

        private async Task<MediaExportResult> ExportDirectImagesAsync(MediaAnalysisResult analysis, IList<MediaAssetInfo> selectedAssets, string outputDirectory, string proxy, CancellationToken cancellationToken, Action<string> progress)
        {
            var completed = 0;
            var errors = new StringBuilder();
            using (var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            })
            {
                if (!string.IsNullOrWhiteSpace(proxy))
                {
                    handler.Proxy = new WebProxy(proxy.Trim());
                    handler.UseProxy = true;
                }
                using (var client = new HttpClient(handler))
                {
                    client.Timeout = TimeSpan.FromMinutes(2);
                    client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", DouyinUserAgent);
                    client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");
                    foreach (var asset in selectedAssets.OrderBy(item => item.Index))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var temporaryPath = Path.Combine(outputDirectory, ".LiveBoard-" + Guid.NewGuid().ToString("N") + ".part");
                        try
                        {
                            ReportProgress(progress, "正在下载第 " + (completed + 1) + "/" + selectedAssets.Count + " 个媒体");
                            Exception downloadError = null;
                            foreach (var requestUrl in new[] { asset.Url, GetDouyinImageHttpFallbackUrl(asset.Url) }.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase))
                            {
                                DeleteQuietly(temporaryPath);
                                try
                                {
                                    using (var request = new HttpRequestMessage(HttpMethod.Get, requestUrl))
                                    {
                                        Uri referer;
                                        if (Uri.TryCreate(analysis.Url, UriKind.Absolute, out referer))
                                            request.Headers.Referrer = referer;
                                        using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                                        {
                                            response.EnsureSuccessStatusCode();
                                            var total = response.Content.Headers.ContentLength.GetValueOrDefault(asset.FileSize);
                                            using (var input = await response.Content.ReadAsStreamAsync())
                                            using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                                            {
                                                var buffer = new byte[81920];
                                                long downloaded = 0;
                                                while (true)
                                                {
                                                    var read = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                                                    if (read == 0)
                                                        break;
                                                    await output.WriteAsync(buffer, 0, read, cancellationToken);
                                                    downloaded += read;
                                                    ReportProgress(progress, "__RH_PROGRESS__" + downloaded.ToString(CultureInfo.InvariantCulture) + "|" + total.ToString(CultureInfo.InvariantCulture) + "|" + total.ToString(CultureInfo.InvariantCulture) + "|NA|NA|NA");
                                                }
                                                asset.FileSize = downloaded;
                                            }
                                        }
                                    }
                                    downloadError = null;
                                    break;
                                }
                                catch (OperationCanceledException)
                                {
                                    throw;
                                }
                                catch (Exception ex)
                                {
                                    downloadError = ex;
                                }
                            }
                            if (downloadError != null)
                                throw downloadError;

                            var extension = Regex.IsMatch(asset.Extension ?? string.Empty, @"^[a-zA-Z0-9]{1,8}$") ? asset.Extension.ToLowerInvariant() : (IsVideo(asset) ? "mp4" : "jpg");
                            var mediaId = GetDouyinMediaId(analysis.Url);
                            var stem = asset.Index.ToString("000", CultureInfo.InvariantCulture) + "_" + SafeFileNamePart(analysis.Title);
                            if (!string.IsNullOrWhiteSpace(mediaId))
                                stem += "_" + mediaId;
                            var outputPath = GetAvailableOutputPath(outputDirectory, stem, extension);
                            File.Move(temporaryPath, outputPath);
                            completed++;
                            ReportProgress(progress, "__RH_OUTPUT__" + outputPath);
                        }
                        catch (OperationCanceledException)
                        {
                            DeleteQuietly(temporaryPath);
                            throw;
                        }
                        catch (Exception ex)
                        {
                            DeleteQuietly(temporaryPath);
                            errors.AppendLine("第 " + asset.Index + " 个媒体：" + ex.Message);
                        }
                    }
                }
            }

            var failed = selectedAssets.Count - completed;
            return new MediaExportResult
            {
                Success = completed > 0,
                PartialSuccess = completed > 0 && failed > 0,
                ErrorText = failed == 0 ? null : (completed == 0 ? "媒体下载失败：" : "有 " + failed + " 个媒体下载失败：") + MapError(errors.ToString(), "抖音"),
                LogText = errors.ToString().Trim(),
                OutputDirectory = outputDirectory,
                DownloadedCount = completed
            };
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
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || !string.Equals(uri.Host, "v.douyin.com", StringComparison.OrdinalIgnoreCase))
                return url;

            try
            {
                ReportProgress(progress, "正在展开抖音分享链接");
                using (var handler = new HttpClientHandler { AllowAutoRedirect = true, MaxConnectionsPerServer = 8 })
                {
                    if (!string.IsNullOrWhiteSpace(proxy))
                    {
                        handler.Proxy = new WebProxy(proxy.Trim());
                        handler.UseProxy = true;
                    }
                    using (var client = new HttpClient(handler))
                    {
                        client.Timeout = TimeSpan.FromSeconds(20);
                        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", DouyinUserAgent);
                        using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                        {
                            var finalUri = response.RequestMessage == null ? null : response.RequestMessage.RequestUri;
                            if (finalUri == null)
                                return url;
                            var finalUrl = finalUri.ToString();
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

        private static bool IsDouyinNoteUrl(string url)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || !string.Equals(uri.Host, "www.douyin.com", StringComparison.OrdinalIgnoreCase))
                return false;

            var segments = uri.AbsolutePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            return segments.Length >= 2 && string.Equals(segments[0], "note", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(segments[1]);
        }

        private static bool IsDouyinMediaUrl(string url)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || !string.Equals(uri.Host, "www.douyin.com", StringComparison.OrdinalIgnoreCase))
                return false;
            var path = uri.AbsolutePath.Trim('/');
            return path.StartsWith("video/", StringComparison.OrdinalIgnoreCase) || path.StartsWith("note/", StringComparison.OrdinalIgnoreCase) || GetDouyinModalId(url) != null;
        }

        private async Task<MediaAnalysisResult> AnalyzeDouyinNoteAsync(string url, string proxy, CancellationToken cancellationToken, Action<string> progress)
        {
            ReportProgress(progress, "正在读取抖音图文");
            try
            {
                using (var handler = new HttpClientHandler
                {
                    AllowAutoRedirect = true,
                    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
                })
                {
                    if (!string.IsNullOrWhiteSpace(proxy))
                    {
                        handler.Proxy = new WebProxy(proxy.Trim());
                        handler.UseProxy = true;
                    }
                    using (var client = new HttpClient(handler))
                    {
                        client.Timeout = TimeSpan.FromSeconds(60);
                        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", DouyinSpiderUserAgent);
                        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
                        using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                        {
                            if (!response.IsSuccessStatusCode)
                                return await AnalyzeDouyinNoteApiAsync(url, proxy, cancellationToken, progress);
                            var html = await ReadPageTextAsync(response.Content, cancellationToken);
                            Dictionary<string, object> article = null;
                            foreach (Match match in Regex.Matches(html ?? string.Empty, @"<script\b[^>]*\btype\s*=\s*[\""']application/ld\+json[\""'][^>]*>(?<json>[\s\S]*?)</script>", RegexOptions.IgnoreCase))
                            {
                                try
                                {
                                    var candidate = _serializer.DeserializeObject(WebUtility.HtmlDecode(match.Groups["json"].Value.Trim())) as Dictionary<string, object>;
                                    if (string.Equals(FirstString(candidate, "@type"), "article", StringComparison.OrdinalIgnoreCase))
                                    {
                                        article = candidate;
                                        break;
                                    }
                                }
                                catch
                                {
                                }
                            }
                            if (article == null)
                                return await AnalyzeDouyinNoteApiAsync(url, proxy, cancellationToken, progress);

                            var imageValue = GetValue(article, "image");
                            var imageUrls = (imageValue is string ? new[] { imageValue } : AsEnumerable(imageValue))
                                .Select(Convert.ToString)
                                .Where(IsHttpUrl)
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .Take(MaxPageMediaAssets)
                                .ToList();
                            if (imageUrls.Count == 0)
                                return await AnalyzeDouyinNoteApiAsync(url, proxy, cancellationToken, progress);

                            var author = GetValue(article, "author") as Dictionary<string, object>;
                            var title = FirstString(article, "headline", "articleBody");
                            if (string.IsNullOrWhiteSpace(title))
                                title = FirstString(author, "name") ?? FirstString(article, "description") ?? "抖音图文";
                            var result = new MediaAnalysisResult
                            {
                                Success = true,
                                Platform = "抖音",
                                Url = url,
                                Engine = "direct-image",
                                Title = title.Replace("\r", " ").Replace("\n", " ").Trim()
                            };
                            for (var index = 0; index < imageUrls.Count; index++)
                            {
                                var extension = ExtensionFromUrl(imageUrls[index]);
                                result.Assets.Add(new MediaAssetInfo
                                {
                                    Index = index + 1,
                                    Type = "图片",
                                    Extension = string.IsNullOrWhiteSpace(extension) ? "webp" : extension.ToLowerInvariant(),
                                    Url = imageUrls[index],
                                    ThumbnailUrl = imageUrls[index]
                                });
                            }
                            result.AssetCount = result.Assets.Count;
                            foreach (var asset in result.Assets)
                            {
                                ReportProgress(progress, "正在读取第 " + asset.Index + "/" + result.Assets.Count + " 张图片信息");
                                await EnrichDouyinImageAsync(asset, client, url, cancellationToken);
                                ReportProgress(progress, "正在生成第 " + asset.Index + "/" + result.Assets.Count + " 张图片预览");
                                await ProbeVideoAssetAsync(asset, url, proxy, cancellationToken);
                            }
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
                return Failure(url, MapError(ex.GetBaseException().Message, "抖音"));
            }
        }

        private async Task<MediaAnalysisResult> AnalyzeDouyinNoteApiAsync(string url, string proxy, CancellationToken cancellationToken, Action<string> progress)
        {
            var mediaId = GetDouyinMediaId(url);
            if (string.IsNullOrWhiteSpace(mediaId))
                return Failure(url, "没有识别到抖音图文作品 ID。");

            ReportProgress(progress, "正在读取抖音作品详情");
            var cookies = new CookieContainer();
            using (var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                CookieContainer = cookies,
                UseCookies = true
            })
            {
                if (!string.IsNullOrWhiteSpace(proxy))
                {
                    handler.Proxy = new WebProxy(proxy.Trim());
                    handler.UseProxy = true;
                }
                using (var client = new HttpClient(handler))
                {
                    client.Timeout = TimeSpan.FromSeconds(60);
                    client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", DouyinUserAgent);
                    client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
                    await EstablishDouyinAnonymousSessionAsync(client, cookies, cancellationToken);

                    var query = "aid=6383&aweme_id=" + Uri.EscapeDataString(mediaId) + "&msToken=";
                    var api = "https://www.douyin.com/aweme/v1/web/aweme/detail/?" + query +
                              "&a_bogus=" + Uri.EscapeDataString(DouyinSignature.Sign(query, DouyinUserAgent));
                    using (var request = new HttpRequestMessage(HttpMethod.Get, api))
                    {
                        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
                        request.Headers.Referrer = new Uri(url);
                        using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                        {
                            response.EnsureSuccessStatusCode();
                            var root = _serializer.DeserializeObject(await response.Content.ReadAsStringAsync()) as Dictionary<string, object>;
                            var detail = GetValue(root, "aweme_detail") as Dictionary<string, object>;
                            var images = AsEnumerable(GetValue(detail, "images"))
                                .Select(value => value as Dictionary<string, object>)
                                .Where(value => value != null)
                                .Take(MaxPageMediaAssets)
                                .ToList();
                            if (GetInt(root, "status_code") != 0 || detail == null || images.Count == 0)
                                return Failure(url, "抖音没有返回该图文的媒体信息，作品可能已删除、转为私密或受到地区限制。");

                            var author = GetValue(detail, "author") as Dictionary<string, object>;
                            var title = FirstString(detail, "desc", "caption", "preview_title") ?? FirstString(author, "nickname") ?? "抖音图文";
                            var result = new MediaAnalysisResult
                            {
                                Success = true,
                                Platform = "抖音",
                                Url = url,
                                Engine = "direct-image",
                                Title = title.Replace("\r", " ").Replace("\n", " ").Trim()
                            };
                            foreach (var image in images)
                            {
                                var imageUrls = AsEnumerable(GetValue(image, "url_list"))
                                    .Select(Convert.ToString)
                                    .Where(IsHttpUrl)
                                    .ToList();
                                var imageUrl = imageUrls.FirstOrDefault(value =>
                                {
                                    var candidateExtension = ExtensionFromUrl(value);
                                    return string.Equals(candidateExtension, "jpg", StringComparison.OrdinalIgnoreCase) ||
                                           string.Equals(candidateExtension, "jpeg", StringComparison.OrdinalIgnoreCase);
                                }) ?? imageUrls.FirstOrDefault();
                                var video = GetValue(image, "video") as Dictionary<string, object>;
                                var playAddress = GetValue(video, "play_addr") as Dictionary<string, object>;
                                var videoUrl = AsEnumerable(GetValue(playAddress, "url_list"))
                                    .Select(Convert.ToString)
                                    .FirstOrDefault(IsHttpUrl);
                                var isMotionPhoto = !string.IsNullOrWhiteSpace(videoUrl);
                                if (!isMotionPhoto && string.IsNullOrWhiteSpace(imageUrl))
                                    continue;
                                var extension = isMotionPhoto ? "mp4" : ExtensionFromUrl(imageUrl);
                                result.Assets.Add(new MediaAssetInfo
                                {
                                    Index = result.Assets.Count + 1,
                                    Type = isMotionPhoto ? "动态照片" : "图片",
                                    Extension = string.IsNullOrWhiteSpace(extension) ? "jpg" : extension.ToLowerInvariant(),
                                    Url = isMotionPhoto ? videoUrl : imageUrl,
                                    ThumbnailUrl = imageUrl,
                                    Width = isMotionPhoto ? GetInt(playAddress, "width") : GetInt(image, "width"),
                                    Height = isMotionPhoto ? GetInt(playAddress, "height") : GetInt(image, "height"),
                                    FileSize = isMotionPhoto ? GetLong(playAddress, "data_size") : 0,
                                    DurationSeconds = isMotionPhoto ? GetDouble(video, "duration") / 1000d : 0,
                                    Codec = isMotionPhoto ? (GetInt(video, "is_h265") == 1 ? "H.265" : "H.264") : null
                                });
                            }
                            if (result.Assets.Count == 0)
                                return Failure(url, "该抖音图文没有返回可下载的图片。");

                            result.AssetCount = result.Assets.Count;
                            foreach (var asset in result.Assets)
                            {
                                ReportProgress(progress, "正在读取第 " + asset.Index + "/" + result.Assets.Count + " 个媒体信息");
                                if (IsVideo(asset))
                                {
                                    if (asset.FileSize <= 0)
                                        asset.FileSize = await GetRemoteContentLengthAsync(client, asset.Url, url, cancellationToken);
                                }
                                else
                                    await EnrichDouyinImageAsync(asset, client, url, cancellationToken);
                                ReportProgress(progress, "正在生成第 " + asset.Index + "/" + result.Assets.Count + " 个媒体预览");
                                await ProbeVideoAssetAsync(asset, url, proxy, cancellationToken);
                            }
                            return result;
                        }
                    }
                }
            }
        }

        private static async Task EnrichDouyinImageAsync(MediaAssetInfo asset, HttpClient client, string pageUrl, CancellationToken cancellationToken)
        {
            Exception lastError = null;
            foreach (var requestUrl in new[] { asset.Url, GetDouyinImageHttpFallbackUrl(asset.Url) }.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    using (var request = new HttpRequestMessage(HttpMethod.Get, requestUrl))
                    {
                        request.Headers.TryAddWithoutValidation("Accept", "image/avif,image/webp,image/apng,image/*,*/*;q=0.8");
                        Uri referer;
                        if (Uri.TryCreate(pageUrl, UriKind.Absolute, out referer))
                            request.Headers.Referrer = referer;
                        using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                        {
                            response.EnsureSuccessStatusCode();
                            asset.FileSize = response.Content.Headers.ContentLength.GetValueOrDefault();
                            IEnumerable<string> imageLengths;
                            long imageLength;
                            if (asset.FileSize <= 0 && response.Headers.TryGetValues("X-Length", out imageLengths) &&
                                long.TryParse(imageLengths.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out imageLength))
                                asset.FileSize = imageLength;

                            IEnumerable<string> imageDetails;
                            if (response.Headers.TryGetValues("X-Imagex-Extra", out imageDetails))
                            {
                                var details = imageDetails.FirstOrDefault() ?? string.Empty;
                                var width = Regex.Match(details, "\"w\"\\s*:\\s*(?<value>\\d+)");
                                var height = Regex.Match(details, "\"h\"\\s*:\\s*(?<value>\\d+)");
                                if (width.Success)
                                    asset.Width = GetInt(width.Groups["value"].Value);
                                if (height.Success)
                                    asset.Height = GetInt(height.Groups["value"].Value);
                            }

                            if (asset.FileSize <= 0)
                            {
                                using (var input = await response.Content.ReadAsStreamAsync())
                                {
                                    var buffer = new byte[81920];
                                    while (true)
                                    {
                                        var read = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                                        if (read == 0)
                                            break;
                                        asset.FileSize += read;
                                    }
                                }
                            }
                        }
                    }
                    return;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }
            }
            throw lastError ?? new HttpRequestException("抖音图片服务器没有返回媒体信息。");
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
                AddPlatformRequestArguments(arguments, platform, url);
                AddProxyArgument(arguments, proxy);
                arguments.Add(url);
                ReportProgress(progress, "正在识别视频与画质");
                var run = await RunToolAsync(path, arguments, cancellationToken, progress);
                if (run.Cancelled || cancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException(cancellationToken);
                if (run.ExitCode != 0 && string.Equals(platform, "Bilibili", StringComparison.OrdinalIgnoreCase) && IsCertificateValidationError(run))
                {
                    arguments.Insert(arguments.Count - 1, "--no-check-certificates");
                    ReportProgress(progress, "B站证书校验失败，正在使用兼容模式重试");
                    run = await RunToolAsync(path, arguments, cancellationToken, progress);
                }
                var impersonated = false;
                if (run.ExitCode != 0 && string.Equals(platform, "网页", StringComparison.OrdinalIgnoreCase) && NeedsBrowserImpersonation(run.StandardError))
                {
                    arguments.Insert(arguments.Count - 1, "--impersonate");
                    arguments.Insert(arguments.Count - 1, "Chrome-136:Macos-15");
                    ReportProgress(progress, "正在通过浏览器验证读取视频");
                    run = await RunToolAsync(path, arguments, cancellationToken, progress);
                    impersonated = true;
                }
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
                    Engine = impersonated ? "yt-dlp-impersonate" : "yt-dlp",
                    Title = FirstString(root, "title", "fulltitle") ?? FirstString(primary, "title", "fulltitle", "id")
                };
                if (entries.Count == 0)
                {
                    var asset = CreateYtDlpAsset(root, 1);
                    BuildFormatOptions(root, result.Formats, platform);
                    if (result.Formats.Count == 0)
                        result.Formats.Add(new MediaFormatOption { FormatId = "best", Selector = "bestvideo+bestaudio/best", Label = "最佳可用画质" });
                    asset.Formats.AddRange(result.Formats);
                    asset.SelectedFormat = asset.Formats[0];
                    result.Assets.Add(asset);
                }
                else
                {
                    for (var index = 0; index < entries.Count; index++)
                    {
                        var asset = CreateYtDlpAsset(entries[index], index + 1);
                        if (IsVideo(asset))
                        {
                            asset.ShowInlineQuality = true;
                            BuildFormatOptions(entries[index], asset.Formats, platform);
                            if (asset.Formats.Count == 0)
                                asset.Formats.Add(new MediaFormatOption { FormatId = "best", Selector = "bestvideo+bestaudio/best", Label = "最佳可用画质" });
                            asset.SelectedFormat = asset.Formats[0];
                        }
                        result.Assets.Add(asset);
                    }
                }
                if (entries.Count > 1 && string.Equals(platform, "Bilibili", StringComparison.OrdinalIgnoreCase))
                    await ApplyBilibiliPageThumbnailsAsync(url, result.Assets, proxy, cancellationToken, progress);
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

        private async Task ApplyBilibiliPageThumbnailsAsync(string url, IList<MediaAssetInfo> assets, string proxy, CancellationToken cancellationToken, Action<string> progress)
        {
            var match = Regex.Match(url ?? string.Empty, @"(?<![A-Za-z0-9])(BV[0-9A-Za-z]+)", RegexOptions.IgnoreCase);
            if (!match.Success || assets == null || assets.Count == 0)
                return;
            if (assets.Select(asset => asset == null ? null : asset.ThumbnailUrl).Distinct(StringComparer.OrdinalIgnoreCase).Count() == assets.Count)
                return;
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
                        client.Timeout = TimeSpan.FromSeconds(20);
                        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", DouyinUserAgent);
                        var api = "https://api.bilibili.com/x/web-interface/view?bvid=" + Uri.EscapeDataString(match.Groups[1].Value);
                        using (var response = await client.GetAsync(api, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                        {
                            if (!response.IsSuccessStatusCode)
                                return;
                            var root = _serializer.DeserializeObject(await response.Content.ReadAsStringAsync()) as Dictionary<string, object>;
                            var data = GetValue(root, "data") as Dictionary<string, object>;
                            var pages = AsEnumerable(GetValue(data, "pages"))
                                .Select(value => value as Dictionary<string, object>)
                                .Where(value => value != null)
                                .ToList();
                            if (pages.Count == 0)
                                return;
                            ReportProgress(progress, "正在读取各分P预览图");
                            for (var index = 0; index < Math.Min(assets.Count, pages.Count); index++)
                            {
                                var thumbnail = FirstString(pages[index], "first_frame");
                                if (thumbnail != null && thumbnail.StartsWith("//", StringComparison.Ordinal))
                                    thumbnail = "https:" + thumbnail;
                                if (IsHttpUrl(thumbnail))
                                    assets[index].ThumbnailUrl = thumbnail;
                            }
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
            }
        }

        private static MediaAssetInfo CreateYtDlpAsset(Dictionary<string, object> values, int index)
        {
            var extension = FirstString(values, "ext") ?? "mp4";
            var videoCodec = FirstString(values, "vcodec");
            return new MediaAssetInfo
            {
                Index = index,
                Type = IsVideoExtension(extension) || (!string.IsNullOrWhiteSpace(videoCodec) && !string.Equals(videoCodec, "none", StringComparison.OrdinalIgnoreCase)) ? "视频" : "图片",
                Extension = extension,
                Url = FirstString(values, "webpage_url", "original_url"),
                Width = GetInt(values, "width"),
                Height = GetInt(values, "height"),
                FileSize = GetLong(values, "filesize", "filesize_approx"),
                DurationSeconds = GetDouble(values, "duration"),
                ThumbnailUrl = FirstString(values, "thumbnail")
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
                        client.Timeout = TimeSpan.FromSeconds(60);
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
                            var thumbnail = ExtractOpenGraphValue(html, "og:image");
                            for (var index = 0; index < mediaUrls.Count; index++)
                            {
                                var extension = ExtensionFromUrl(mediaUrls[index]);
                                result.Assets.Add(new MediaAssetInfo
                                {
                                    Index = index + 1,
                                    Type = "视频",
                                    Extension = string.IsNullOrWhiteSpace(extension) ? "媒体流" : extension.ToLowerInvariant(),
                                    Url = mediaUrls[index],
                                    ThumbnailUrl = thumbnail
                                });
                            }
                            result.AssetCount = result.Assets.Count;
                            await EnrichDirectAssetsAsync(result.Assets, url, proxy, client, cancellationToken, progress);
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

        private async Task EnrichDirectAssetsAsync(IList<MediaAssetInfo> assets, string pageUrl, string proxy, HttpClient client, CancellationToken cancellationToken, Action<string> progress)
        {
            for (var offset = 0; offset < assets.Count; offset += 2)
            {
                var batch = assets.Skip(offset).Take(2).Select(async asset =>
                {
                    ReportProgress(progress, "正在读取第 " + asset.Index + "/" + assets.Count + " 个媒体信息");
                    try
                    {
                        if (string.Equals(ExtensionFromUrl(asset.Url), "m3u8", StringComparison.OrdinalIgnoreCase))
                            await ProbeHlsAssetAsync(asset, client, pageUrl, cancellationToken);
                        else
                            asset.FileSize = await GetRemoteContentLengthAsync(client, asset.Url, pageUrl, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        if (cancellationToken.IsCancellationRequested)
                            throw;
                    }
                    catch
                    {
                    }

                    await ProbeVideoAssetAsync(asset, pageUrl, proxy, cancellationToken);
                }).ToArray();
                await Task.WhenAll(batch);
            }
        }

        private static async Task ProbeHlsAssetAsync(MediaAssetInfo asset, HttpClient client, string pageUrl, CancellationToken cancellationToken)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, asset.Url))
            {
                Uri referer;
                if (Uri.TryCreate(pageUrl, UriKind.Absolute, out referer))
                    request.Headers.Referrer = referer;
                using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken))
                {
                    response.EnsureSuccessStatusCode();
                    var playlist = await response.Content.ReadAsStringAsync();
                    var lines = Regex.Split(playlist ?? string.Empty, @"\r?\n");
                    var segmentUrls = new List<string>();
                    var segmentDurations = new List<double>();
                    double pendingDuration = 0;
                    long byteRangeTotal = 0;
                    foreach (var rawLine in lines)
                    {
                        var line = (rawLine ?? string.Empty).Trim();
                        if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
                        {
                            var value = line.Substring(8).Split(',')[0];
                            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out pendingDuration);
                        }
                        else if (line.StartsWith("#EXT-X-BYTERANGE:", StringComparison.OrdinalIgnoreCase))
                        {
                            long length;
                            if (long.TryParse(line.Substring(17).Split('@')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out length))
                                byteRangeTotal += length;
                        }
                        else if (line.Length > 0 && line[0] != '#' && pendingDuration > 0)
                        {
                            Uri segmentUri;
                            if (Uri.TryCreate(new Uri(asset.Url), line, out segmentUri))
                            {
                                segmentUrls.Add(segmentUri.AbsoluteUri);
                                segmentDurations.Add(pendingDuration);
                            }
                            pendingDuration = 0;
                        }
                    }

                    var duration = segmentDurations.Sum();
                    if (duration > 0)
                        asset.DurationSeconds = duration;
                    if (byteRangeTotal > 0)
                    {
                        asset.FileSize = byteRangeTotal;
                        return;
                    }
                    if (segmentUrls.Count == 0)
                        return;

                    var sampleCount = Math.Min(8, segmentUrls.Count);
                    var sampleIndexes = Enumerable.Range(0, sampleCount)
                        .Select(index => sampleCount == 1 ? 0 : index * (segmentUrls.Count - 1) / (sampleCount - 1))
                        .Distinct()
                        .ToList();
                    var sizes = await Task.WhenAll(sampleIndexes
                        .Select(index => GetRemoteContentLengthAsync(client, segmentUrls[index], pageUrl, cancellationToken))
                        .ToArray());
                    long measuredBytes = 0;
                    double measuredDuration = 0;
                    for (var index = 0; index < sizes.Length; index++)
                    {
                        if (sizes[index] <= 0)
                            continue;
                        measuredBytes += sizes[index];
                        measuredDuration += segmentDurations[sampleIndexes[index]];
                    }
                    if (measuredBytes > 0 && measuredDuration > 0 && duration > 0)
                    {
                        asset.FileSize = (long)Math.Round(measuredBytes * duration / measuredDuration);
                        asset.FileSizeIsEstimated = sampleIndexes.Count < segmentUrls.Count;
                    }
                }
            }
        }

        private static async Task<long> GetRemoteContentLengthAsync(HttpClient client, string url, string pageUrl, CancellationToken cancellationToken)
        {
            foreach (var useRange in new[] { false, true })
            {
                using (var request = new HttpRequestMessage(useRange ? HttpMethod.Get : HttpMethod.Head, url))
                {
                    Uri referer;
                    if (Uri.TryCreate(pageUrl, UriKind.Absolute, out referer))
                        request.Headers.Referrer = referer;
                    if (useRange)
                        request.Headers.TryAddWithoutValidation("Range", "bytes=0-0");
                    try
                    {
                        using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                        {
                            if (!response.IsSuccessStatusCode)
                                continue;
                            IEnumerable<string> imageLengths;
                            long imageLength;
                            if (response.Headers.TryGetValues("X-Length", out imageLengths) &&
                                long.TryParse(imageLengths.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out imageLength) && imageLength > 0)
                                return imageLength;
                            if (response.Content.Headers.ContentRange != null && response.Content.Headers.ContentRange.HasLength)
                                return response.Content.Headers.ContentRange.Length.Value;
                            var length = response.Content.Headers.ContentLength.GetValueOrDefault();
                            if (length > 0 && (!useRange || response.StatusCode == HttpStatusCode.OK))
                                return length;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        if (cancellationToken.IsCancellationRequested)
                            throw;
                    }
                    catch
                    {
                    }
                }
            }
            return 0;
        }

        private async Task ProbeVideoAssetAsync(MediaAssetInfo asset, string pageUrl, string proxy, CancellationToken cancellationToken)
        {
            var previewPath = GetMediaPreviewPath(asset.Url);
            Directory.CreateDirectory(Path.GetDirectoryName(previewPath));
            var createPreview = !File.Exists(previewPath) || new FileInfo(previewPath).Length == 0;
            var isImage = string.Equals(asset.Type, "图片", StringComparison.OrdinalIgnoreCase);
            if (isImage && !createPreview)
            {
                asset.ThumbnailUrl = previewPath;
                return;
            }
            var arguments = new List<string> { "-hide_banner", "-y", "-loglevel", "info" };
            if (!string.IsNullOrWhiteSpace(proxy))
            {
                arguments.Add("-http_proxy");
                arguments.Add(proxy.Trim());
            }
            arguments.Add("-referer");
            arguments.Add(pageUrl);
            arguments.Add("-user_agent");
            arguments.Add(isImage ? DouyinSpiderUserAgent : DouyinUserAgent);
            if (!isImage)
            {
                arguments.Add("-ss");
                arguments.Add("1");
            }
            arguments.Add("-i");
            arguments.Add(isImage ? GetDouyinImageHttpFallbackUrl(asset.Url) ?? asset.Url : asset.Url);
            arguments.Add("-frames:v");
            arguments.Add("1");
            arguments.Add("-an");
            if (createPreview)
            {
                arguments.Add("-vf");
                arguments.Add("scale=320:-2");
                arguments.Add("-q:v");
                arguments.Add("4");
                arguments.Add(previewPath);
            }
            else
            {
                arguments.Add("-f");
                arguments.Add("null");
                arguments.Add("NUL");
            }

            try
            {
                var run = await RunToolAsync(RecordingService.EnsureBundledFfmpeg(), arguments, cancellationToken, null);
                if (run.Cancelled)
                    throw new OperationCanceledException(cancellationToken);
                var log = run.StandardError ?? string.Empty;
                var duration = Regex.Match(log, @"Duration:\s*(?<hours>\d{1,3}):(?<minutes>\d{2}):(?<seconds>\d{2}(?:\.\d+)?)", RegexOptions.IgnoreCase);
                if (asset.DurationSeconds <= 0 && duration.Success)
                {
                    asset.DurationSeconds = double.Parse(duration.Groups["hours"].Value, CultureInfo.InvariantCulture) * 3600 +
                                            double.Parse(duration.Groups["minutes"].Value, CultureInfo.InvariantCulture) * 60 +
                                            double.Parse(duration.Groups["seconds"].Value, CultureInfo.InvariantCulture);
                }
                var dimensions = Regex.Match(log, @"Video:[^\r\n]*?(?<width>\d{2,5})x(?<height>\d{2,5})(?:[,\s])", RegexOptions.IgnoreCase);
                if (dimensions.Success)
                {
                    asset.Width = GetInt(dimensions.Groups["width"].Value);
                    asset.Height = GetInt(dimensions.Groups["height"].Value);
                }
                var codec = Regex.Match(log, @"Video:\s*(?<codec>[^,\s(]+)", RegexOptions.IgnoreCase);
                if (codec.Success)
                    asset.Codec = FormatCodec(codec.Groups["codec"].Value);
                if (File.Exists(previewPath) && new FileInfo(previewPath).Length > 0)
                    asset.ThumbnailUrl = previewPath;
                else
                    DeleteQuietly(previewPath);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                DeleteQuietly(previewPath);
            }
        }

        private static string GetMediaPreviewPath(string url)
        {
            Uri uri;
            var key = Uri.TryCreate(url, UriKind.Absolute, out uri) ? uri.GetLeftPart(UriPartial.Path) : url;
            using (var sha = SHA256.Create())
            {
                var hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(key ?? string.Empty))).Replace("-", string.Empty);
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiveBoard", "media-previews", hash.Substring(0, 24) + ".jpg");
            }
        }

        private static string FormatCodec(string codec)
        {
            if (string.Equals(codec, "h264", StringComparison.OrdinalIgnoreCase) || string.Equals(codec, "avc", StringComparison.OrdinalIgnoreCase) || string.Equals(codec, "avc1", StringComparison.OrdinalIgnoreCase)) return "H.264";
            if (string.Equals(codec, "hevc", StringComparison.OrdinalIgnoreCase) || string.Equals(codec, "h265", StringComparison.OrdinalIgnoreCase)) return "H.265";
            if (string.Equals(codec, "av1", StringComparison.OrdinalIgnoreCase)) return "AV1";
            if (string.Equals(codec, "vp9", StringComparison.OrdinalIgnoreCase)) return "VP9";
            return string.IsNullOrWhiteSpace(codec) ? null : codec.ToUpperInvariant();
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
                var requestUrl = await NormalizeKuaishouUrlAsync(url, proxy, cancellationToken);
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
                        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", KuaishouUserAgent);
                        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
                        HttpResponseMessage response = null;
                        Exception requestError = null;
                        for (var attempt = 0; attempt < 3 && response == null; attempt++)
                        {
                            try
                            {
                                response = await client.GetAsync(requestUrl, cancellationToken);
                            }
                            catch (OperationCanceledException)
                            {
                                if (cancellationToken.IsCancellationRequested)
                                    throw;
                            }
                            catch (Exception ex)
                            {
                                requestError = ex;
                            }
                            if (response == null && attempt < 2)
                                await Task.Delay(500 * (attempt + 1), cancellationToken);
                        }
                        if (response == null)
                            throw requestError ?? new HttpRequestException("快手页面请求失败。");
                        using (response)
                        {
                            response.EnsureSuccessStatusCode();
                            var html = await response.Content.ReadAsStringAsync();
                            var analysis = ExtractKuaishouAnalysis(html, url);
                            if (analysis != null)
                            {
                                foreach (var asset in analysis.Assets)
                                {
                                    ReportProgress(progress, "正在读取第 " + asset.Index + "/" + analysis.Assets.Count + " 个媒体信息");
                                    if (IsVideo(asset) && asset.FileSize <= 0)
                                        asset.FileSize = await GetRemoteContentLengthAsync(client, asset.Url, url, cancellationToken);
                                    else
                                        await EnrichDouyinImageAsync(asset, client, url, cancellationToken);
                                    ReportProgress(progress, "正在生成第 " + asset.Index + "/" + analysis.Assets.Count + " 个媒体预览");
                                    await ProbeVideoAssetAsync(asset, url, proxy, cancellationToken);
                                }
                                return analysis;
                            }

                            var mediaUrl = ExtractKuaishouMediaUrl(html);
                            if (string.IsNullOrWhiteSpace(mediaUrl))
                                return Failure(url, "快手页面没有返回可下载的视频或图片，请使用公开作品分享链接。");

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
                                    new MediaAssetInfo
                                    {
                                        Index = 1,
                                        Type = "视频",
                                        Extension = extension,
                                        Url = mediaUrl,
                                        ThumbnailUrl = ExtractOpenGraphValue(html, "og:image")
                                    }
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

        private static async Task<string> NormalizeKuaishouUrlAsync(string url, string proxy, CancellationToken cancellationToken)
        {
            Uri source;
            if (!Uri.TryCreate(url, UriKind.Absolute, out source) || !string.Equals(source.Host, "v.kuaishou.com", StringComparison.OrdinalIgnoreCase))
                return url;
            try
            {
                using (var handler = new HttpClientHandler { AllowAutoRedirect = false })
                {
                    if (!string.IsNullOrWhiteSpace(proxy))
                    {
                        handler.Proxy = new WebProxy(proxy.Trim());
                        handler.UseProxy = true;
                    }
                    using (var client = new HttpClient(handler))
                    {
                        client.Timeout = TimeSpan.FromSeconds(20);
                        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", KuaishouUserAgent);
                        using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                        {
                            var location = response.Headers.Location;
                            if (location == null)
                                return url;
                            var target = location.IsAbsoluteUri ? location : new Uri(source, location);
                            if (target.Host.EndsWith("chenzhongtech.com", StringComparison.OrdinalIgnoreCase))
                                target = new UriBuilder(target) { Host = "c.kuaishou.com" }.Uri;
                            return target.AbsoluteUri;
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
                        if (IsVideoExtension(ext) || string.Equals(type, "video", StringComparison.OrdinalIgnoreCase))
                            type = "视频";
                        else
                            type = "图片";
                        result.Assets.Add(new MediaAssetInfo
                        {
                            Index = index++,
                            Type = type,
                            Extension = ext,
                            Url = values[1] as string,
                            Width = GetInt(meta, "width"),
                            Height = GetInt(meta, "height"),
                            FileSize = GetLong(meta, "filesize", "file_size", "size"),
                            DurationSeconds = GetDouble(meta, "duration"),
                            ThumbnailUrl = IsVideoExtension(ext)
                                ? FirstString(meta, "thumbnail", "preview", "image")
                                : values[1] as string
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

        private void BuildFormatOptions(Dictionary<string, object> root, List<MediaFormatOption> formats, string platform)
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
                if (height <= 0)
                    height = InferFormatHeight(format);
                if (string.IsNullOrWhiteSpace(id) || height <= 0 || string.Equals(vcodec, "none", StringComparison.OrdinalIgnoreCase))
                    continue;
                var shortSide = width > 0 ? Math.Min(width, height) : height;
                var note = FirstString(format, "format_note");
                if (string.Equals(platform, "抖音", StringComparison.OrdinalIgnoreCase) &&
                    (note ?? string.Empty).IndexOf("(API)", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;
                var quality = QualityLabel(note, shortSide);
                var codec = vcodec ?? string.Empty;
                var codecScore = codec.IndexOf("avc", StringComparison.OrdinalIgnoreCase) >= 0 || codec.IndexOf("h264", StringComparison.OrdinalIgnoreCase) >= 0 ? 300 :
                                 codec.IndexOf("hevc", StringComparison.OrdinalIgnoreCase) >= 0 || codec.IndexOf("h265", StringComparison.OrdinalIgnoreCase) >= 0 ? 200 : 100;
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
            foreach (var group in candidates.GroupBy(item => item.Label).OrderByDescending(item => item.Max(value => value.Width > 0 ? Math.Min(value.Width, value.Height) : value.Height)))
            {
                var selected = group
                    .OrderByDescending(item => item.Width > 0 ? Math.Min(item.Width, item.Height) : item.Height)
                    .ThenByDescending(item => item.Score)
                    .First();
                formats.Add(new MediaFormatOption
                {
                    FormatId = selected.Id,
                    Selector = selected.HasAudio ? selected.Id : selected.Id + "+bestaudio/best",
                    Label = selected.Label + (selected.Width > 0 ? " · " + selected.Width + "×" + selected.Height : string.Empty),
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

        private static int InferFormatHeight(Dictionary<string, object> format)
        {
            foreach (var value in new[] { FirstString(format, "format_note", "resolution", "format"), FirstString(format, "url") })
            {
                var match = Regex.Match(value ?? string.Empty, @"(?<!\d)(?<height>\d{3,4})p(?!\d)", RegexOptions.IgnoreCase);
                if (match.Success)
                    return GetInt(match.Groups["height"].Value);
            }
            return 0;
        }

        private static bool NeedsBrowserImpersonation(string error)
        {
            return (error ?? string.Empty).IndexOf("impersonate", StringComparison.OrdinalIgnoreCase) >= 0 &&
                   ((error ?? string.Empty).IndexOf("Cloudflare", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (error ?? string.Empty).IndexOf("anti-bot", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool IsCertificateValidationError(MediaToolResult run)
        {
            if (run == null)
                return false;
            var text = (run.StandardError ?? string.Empty) + "\n" + (run.StandardOutput ?? string.Empty);
            return text.IndexOf("CERTIFICATE_VERIFY_FAILED", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   text.IndexOf("certificate verify failed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   text.IndexOf("certificate has expired", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   text.IndexOf("SSL:", StringComparison.OrdinalIgnoreCase) >= 0;
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

        private async Task<string> CreateDouyinCookieFileAsync(string proxy, CancellationToken cancellationToken, Action<string> progress)
        {
            try
            {
                ReportProgress(progress, "正在建立抖音匿名会话");
                var cookies = new CookieContainer();
                using (var handler = new HttpClientHandler
                {
                    AllowAutoRedirect = true,
                    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                    CookieContainer = cookies,
                    UseCookies = true
                })
                {
                    if (!string.IsNullOrWhiteSpace(proxy))
                    {
                        handler.Proxy = new WebProxy(proxy.Trim());
                        handler.UseProxy = true;
                    }
                    using (var client = new HttpClient(handler))
                    {
                        client.Timeout = TimeSpan.FromSeconds(20);
                        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", DouyinUserAgent);
                        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
                        await EstablishDouyinAnonymousSessionAsync(client, cookies, cancellationToken);
                        var douyinUri = new Uri("https://www.douyin.com/");
                        var values = cookies.GetCookies(douyinUri);
                        return WriteCookieFile(values);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return null;
            }
        }

        private async Task EstablishDouyinAnonymousSessionAsync(HttpClient client, CookieContainer cookies, CancellationToken cancellationToken)
        {
            const string payload = "{\"region\":\"cn\",\"aid\":1768,\"needFid\":false,\"service\":\"www.douyin.com\",\"migrate_info\":{\"ticket\":\"\",\"source\":\"node\"},\"cbUrlProtocol\":\"https\",\"union\":true}";
            using (var content = new StringContent(payload, Encoding.UTF8, "application/json"))
            using (var response = await client.PostAsync("https://ttwid.bytedance.com/ttwid/union/register/", content, cancellationToken))
            {
                response.EnsureSuccessStatusCode();
                var registration = _serializer.DeserializeObject(await response.Content.ReadAsStringAsync()) as Dictionary<string, object>;
                var callback = FirstString(registration, "redirect_url");
                Uri callbackUri;
                if (!Uri.TryCreate(callback, UriKind.Absolute, out callbackUri) || !callbackUri.Host.EndsWith("douyin.com", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("无法建立抖音匿名会话。");
                using (var callbackResponse = await client.GetAsync(callbackUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                    callbackResponse.EnsureSuccessStatusCode();
            }

            var douyinUri = new Uri("https://www.douyin.com/");
            cookies.SetCookies(douyinUri, "s_v_web_id=verify_" + Guid.NewGuid().ToString("N") + "; Path=/; Secure");
            var values = cookies.GetCookies(douyinUri);
            if (values["ttwid"] == null || values["s_v_web_id"] == null)
                throw new InvalidOperationException("无法建立抖音匿名会话。");
        }

        private static string WriteCookieFile(CookieCollection cookies)
        {
            var builder = new StringBuilder("# Netscape HTTP Cookie File\r\n");
            foreach (Cookie cookie in cookies)
            {
                var domain = string.IsNullOrWhiteSpace(cookie.Domain) ? "www.douyin.com" : cookie.Domain;
                var expires = cookie.Expires == DateTime.MinValue
                    ? DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds()
                    : new DateTimeOffset(cookie.Expires.ToUniversalTime()).ToUnixTimeSeconds();
                builder.Append(domain).Append('\t')
                    .Append(domain.StartsWith(".", StringComparison.Ordinal) ? "TRUE" : "FALSE").Append('\t')
                    .Append(string.IsNullOrWhiteSpace(cookie.Path) ? "/" : cookie.Path).Append('\t')
                    .Append(cookie.Secure ? "TRUE" : "FALSE").Append('\t')
                    .Append(expires.ToString(CultureInfo.InvariantCulture)).Append('\t')
                    .Append(cookie.Name).Append('\t').Append(cookie.Value).Append("\r\n");
            }
            var path = Path.Combine(Path.GetTempPath(), "LiveBoard-media-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
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

        private static void AddPlatformRequestArguments(IList<string> arguments, string platform, string referer)
        {
            if (string.Equals(platform, "YouTube", StringComparison.OrdinalIgnoreCase))
            {
                arguments.Add("--js-runtimes");
                arguments.Add("node");
                arguments.Add("--extractor-args");
                arguments.Add("youtube:player_client=web_embedded");
                return;
            }
            if (!string.Equals(platform, "抖音", StringComparison.OrdinalIgnoreCase))
                return;
            arguments.Add("--user-agent");
            arguments.Add(DouyinUserAgent);
            if (!string.IsNullOrWhiteSpace(referer))
            {
                arguments.Add("--referer");
                arguments.Add(referer);
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
            if (host == "youtube.com" || host.EndsWith(".youtube.com") || host == "youtu.be") return "YouTube";
            if (host == "x.com" || host.EndsWith(".x.com") || host.Contains("twitter.com")) return "X";
            if (host.Contains("instagram.com")) return "Instagram";
            return null;
        }

        private static bool IsHttpUrl(string value)
        {
            Uri uri;
            return Uri.TryCreate(value, UriKind.Absolute, out uri) &&
                   (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }

        private static string GetDouyinImageHttpFallbackUrl(string value)
        {
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps ||
                !(string.Equals(uri.Host, "douyinpic.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".douyinpic.com", StringComparison.OrdinalIgnoreCase)))
                return null;
            var builder = new UriBuilder(uri) { Scheme = Uri.UriSchemeHttp, Port = -1 };
            return builder.Uri.AbsoluteUri;
        }

        private static string GetDouyinMediaId(string url)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri))
                return null;
            var segments = uri.AbsolutePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            return segments.Length >= 2 &&
                   (string.Equals(segments[0], "note", StringComparison.OrdinalIgnoreCase) || string.Equals(segments[0], "video", StringComparison.OrdinalIgnoreCase))
                ? segments[1]
                : null;
        }

        private static string GetDouyinModalId(string url)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || !string.Equals(uri.Host, "www.douyin.com", StringComparison.OrdinalIgnoreCase) ||
                !uri.AbsolutePath.StartsWith("/user/", StringComparison.OrdinalIgnoreCase))
                return null;
            var match = Regex.Match(uri.Query, @"(?:^|[?&])modal_id=(?<id>\d{10,25})(?:&|$)", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups["id"].Value : null;
        }

        private static string SafeFileNamePart(string value)
        {
            value = string.IsNullOrWhiteSpace(value) ? "抖音图文" : value.Trim();
            foreach (var character in Path.GetInvalidFileNameChars())
                value = value.Replace(character, '_');
            value = value.Trim(' ', '.');
            if (value.Length > 60)
                value = value.Substring(0, 60).TrimEnd(' ', '.');
            return string.IsNullOrWhiteSpace(value) ? "抖音图文" : value;
        }

        private static string GetAvailableOutputPath(string directory, string stem, string extension)
        {
            var path = Path.Combine(directory, stem + "." + extension);
            for (var suffix = 2; File.Exists(path); suffix++)
                path = Path.Combine(directory, stem + " (" + suffix.ToString(CultureInfo.InvariantCulture) + ")." + extension);
            return path;
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

        private static string GetDownloadUrl(MediaAnalysisResult analysis, IList<MediaAssetInfo> selectedAssets)
        {
            var first = selectedAssets == null ? null : selectedAssets.FirstOrDefault(asset => asset != null && !string.IsNullOrWhiteSpace(asset.Url));
            if (analysis != null && string.Equals(analysis.Engine, "direct", StringComparison.OrdinalIgnoreCase) && first != null)
                return first.Url;
            return analysis == null ? null : analysis.Url;
        }

        private static IEnumerable<string> GetDownloadUrls(MediaAnalysisResult analysis, IList<MediaAssetInfo> selectedAssets)
        {
            if (analysis == null)
                return Enumerable.Empty<string>();
            if (string.Equals(analysis.Engine, "direct", StringComparison.OrdinalIgnoreCase))
            {
                return (selectedAssets ?? new List<MediaAssetInfo>())
                    .Where(asset => asset != null && !string.IsNullOrWhiteSpace(asset.Url))
                    .Select(asset => asset.Url)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(MaxPageMediaAssets)
                    .ToList();
            }
            return string.IsNullOrWhiteSpace(analysis.Url) ? Enumerable.Empty<string>() : new[] { analysis.Url };
        }

        private static string BuildItemSelection(IEnumerable<MediaAssetInfo> assets)
        {
            return string.Join(",", assets
                .Where(asset => asset != null && asset.Index > 0)
                .Select(asset => asset.Index)
                .Distinct()
                .OrderBy(index => index)
                .Select(index => index.ToString(CultureInfo.InvariantCulture))
                .ToArray());
        }

        private MediaAnalysisResult ExtractKuaishouAnalysis(string html, string url)
        {
            var match = Regex.Match(html ?? string.Empty, @"window\.INIT_STATE\s*=\s*(?<json>\{[\s\S]*?\})\s*</script>", RegexOptions.IgnoreCase);
            if (!match.Success)
                return null;

            Dictionary<string, object> root;
            try
            {
                root = _serializer.DeserializeObject(match.Groups["json"].Value) as Dictionary<string, object>;
            }
            catch
            {
                return null;
            }

            var entry = root == null ? null : root.Values
                .OfType<Dictionary<string, object>>()
                .FirstOrDefault(value => GetValue(value, "photo") is Dictionary<string, object>);
            var photo = GetValue(entry, "photo") as Dictionary<string, object>;
            if (photo == null)
                return null;

            var coverUrl = KuaishouObjectUrls(GetValue(photo, "coverUrls")).FirstOrDefault();
            var result = new MediaAnalysisResult
            {
                Success = true,
                Platform = "快手",
                Url = url,
                Engine = "direct-image",
                Title = (FirstString(photo, "caption") ?? "快手作品").Replace("\r", " ").Replace("\n", " ").Trim()
            };

            var manifest = GetValue(photo, "manifest") as Dictionary<string, object>;
            var adaptation = AsEnumerable(GetValue(manifest, "adaptationSet")).OfType<Dictionary<string, object>>().FirstOrDefault();
            var representations = AsEnumerable(GetValue(adaptation, "representation")).OfType<Dictionary<string, object>>().ToList();
            var representation = representations.FirstOrDefault(value => string.Equals(FirstString(value, "videoCodec"), "avc", StringComparison.OrdinalIgnoreCase)) ?? representations.FirstOrDefault();
            var videoUrl = KuaishouObjectUrls(GetValue(photo, "mainMvUrls")).FirstOrDefault() ?? FirstString(representation, "url");
            if (!string.IsNullOrWhiteSpace(videoUrl))
            {
                result.Assets.Add(new MediaAssetInfo
                {
                    Index = 1,
                    Type = "视频",
                    Extension = string.IsNullOrWhiteSpace(ExtensionFromUrl(videoUrl)) ? "mp4" : ExtensionFromUrl(videoUrl),
                    Url = videoUrl,
                    ThumbnailUrl = coverUrl,
                    Width = GetInt(photo, "width"),
                    Height = GetInt(photo, "height"),
                    FileSize = GetLong(representation, "fileSize"),
                    DurationSeconds = GetDouble(photo, "duration") / 1000d,
                    Codec = FormatCodec(FirstString(representation, "videoCodec"))
                });
            }
            else
            {
                var atlas = GetValue(entry, "atlas") as Dictionary<string, object>;
                if (atlas == null)
                {
                    var parameters = GetValue(photo, "ext_params") as Dictionary<string, object>;
                    atlas = GetValue(parameters, "atlas") as Dictionary<string, object>;
                }

                var cdn = AsEnumerable(GetValue(atlas, "cdn")).Select(Convert.ToString).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
                if (string.IsNullOrWhiteSpace(cdn))
                {
                    cdn = AsEnumerable(GetValue(atlas, "cdnList"))
                        .OfType<Dictionary<string, object>>()
                        .Select(value => FirstString(value, "cdn"))
                        .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
                }
                var sizes = AsEnumerable(GetValue(atlas, "size")).OfType<Dictionary<string, object>>().ToList();
                foreach (var path in AsEnumerable(GetValue(atlas, "list")).Select(Convert.ToString).Where(value => !string.IsNullOrWhiteSpace(value)))
                {
                    var imageUrl = IsHttpUrl(path) ? path : (string.IsNullOrWhiteSpace(cdn) ? null : "https://" + cdn.TrimEnd('/') + "/" + path.TrimStart('/'));
                    if (string.IsNullOrWhiteSpace(imageUrl))
                        continue;
                    var size = result.Assets.Count < sizes.Count ? sizes[result.Assets.Count] : null;
                    var extension = ExtensionFromUrl(imageUrl);
                    result.Assets.Add(new MediaAssetInfo
                    {
                        Index = result.Assets.Count + 1,
                        Type = "图片",
                        Extension = string.IsNullOrWhiteSpace(extension) ? "jpg" : extension,
                        Url = imageUrl,
                        ThumbnailUrl = imageUrl,
                        Width = size == null ? GetInt(photo, "width") : GetInt(size, "w"),
                        Height = size == null ? GetInt(photo, "height") : GetInt(size, "h")
                    });
                }

                if (result.Assets.Count == 0 && !string.IsNullOrWhiteSpace(coverUrl))
                {
                    result.Assets.Add(new MediaAssetInfo
                    {
                        Index = 1,
                        Type = "图片",
                        Extension = string.IsNullOrWhiteSpace(ExtensionFromUrl(coverUrl)) ? "jpg" : ExtensionFromUrl(coverUrl),
                        Url = coverUrl,
                        ThumbnailUrl = coverUrl,
                        Width = GetInt(photo, "width"),
                        Height = GetInt(photo, "height")
                    });
                }
            }

            result.AssetCount = result.Assets.Count;
            return result.AssetCount == 0 ? null : result;
        }

        private static IEnumerable<string> KuaishouObjectUrls(object value)
        {
            return AsEnumerable(value)
                .OfType<Dictionary<string, object>>()
                .Select(item => FirstString(item, "url"))
                .Where(IsHttpUrl);
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

        private static long GetLong(Dictionary<string, object> dictionary, params string[] keys)
        {
            foreach (var key in keys)
            {
                var value = GetValue(dictionary, key);
                if (value == null)
                    continue;
                try { return Convert.ToInt64(value); }
                catch { long result; if (long.TryParse(Convert.ToString(value), out result)) return result; }
            }
            return 0;
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
