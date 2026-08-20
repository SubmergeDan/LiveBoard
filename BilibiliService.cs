using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace LiveBoard
{
    public sealed class BilibiliProbeResult
    {
        public bool IsLive { get; set; }
        public bool HasError { get; set; }
        public string Message { get; set; }
        public string StreamUrl { get; set; }
        public string DisplayName { get; set; }
        public string RoomTitle { get; set; }
        public string CanonicalRoomId { get; set; }
        public string[] AvailableQualities { get; set; }
    }

    public sealed class BilibiliQrSession
    {
        public string Url { get; set; }
        public string Key { get; set; }
    }

    public sealed class BilibiliLoginPollResult
    {
        public bool Success { get; set; }
        public bool Expired { get; set; }
        public bool Scanned { get; set; }
        public string Message { get; set; }
    }

    internal sealed class BilibiliStreamCandidate
    {
        public string Url { get; set; }
        public string Format { get; set; }
        public string Codec { get; set; }
    }

    internal sealed class BilibiliPlayInfo
    {
        public string StreamUrl { get; set; }
        public string[] AvailableQualities { get; set; }
    }

    public sealed class BilibiliService
    {
        private CookieContainer _cookies = new CookieContainer();

        public bool IsLoggedIn { get; private set; }
        public string UserName { get; private set; }

        public string[] GetQualityOptions()
        {
            if (IsLoggedIn)
                return new[] { "自动", "杜比", "4K", "2K", "原画", "蓝光", "超清", "高清", "流畅" };
            return new[] { "自动", "超清", "高清", "流畅" };
        }

        public void LoadProtectedCookies(string encrypted)
        {
            _cookies = new CookieContainer();
            IsLoggedIn = false;
            UserName = null;
            if (string.IsNullOrWhiteSpace(encrypted))
                return;
            try
            {
                var protectedBytes = Convert.FromBase64String(encrypted);
                var plainBytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
                var lines = Encoding.UTF8.GetString(plainBytes).Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    var parts = line.Split(new[] { '\t' }, 2);
                    if (parts.Length != 2)
                        continue;
                    var value = Encoding.UTF8.GetString(Convert.FromBase64String(parts[1]));
                    _cookies.Add(new Cookie(parts[0], value, "/", ".bilibili.com"));
                }
            }
            catch
            {
                _cookies = new CookieContainer();
            }
        }

        public string ExportProtectedCookies()
        {
            try
            {
                var cookies = _cookies.GetCookies(new Uri("https://www.bilibili.com/"));
                var lines = new List<string>();
                foreach (Cookie cookie in cookies)
                {
                    var value = Convert.ToBase64String(Encoding.UTF8.GetBytes(cookie.Value ?? string.Empty));
                    lines.Add(cookie.Name + "\t" + value);
                }
                if (lines.Count == 0)
                    return null;
                var plain = Encoding.UTF8.GetBytes(string.Join("\n", lines.ToArray()));
                return Convert.ToBase64String(ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser));
            }
            catch
            {
                return null;
            }
        }

        public string ExportNetscapeCookies()
        {
            try
            {
                var cookies = _cookies.GetCookies(new Uri("https://www.bilibili.com/"));
                var lines = new List<string>
                {
                    "# Netscape HTTP Cookie File"
                };
                foreach (Cookie cookie in cookies)
                {
                    lines.Add(string.Join("\t", new[]
                    {
                        ".bilibili.com",
                        "TRUE",
                        "/",
                        cookie.Secure ? "TRUE" : "FALSE",
                        "2147483647",
                        cookie.Name,
                        cookie.Value ?? string.Empty
                    }));
                }
                return lines.Count > 1 ? string.Join("\n", lines.ToArray()) : null;
            }
            catch
            {
                return null;
            }
        }

        public void Logout()
        {
            _cookies = new CookieContainer();
            IsLoggedIn = false;
            UserName = null;
        }

        public async Task<bool> ValidateLoginAsync(CancellationToken cancellationToken)
        {
            try
            {
                var root = await GetJsonAsync("https://api.bilibili.com/x/web-interface/nav", cancellationToken);
                var data = AsDictionary(Get(root, "data"));
                IsLoggedIn = GetBoolean(data, "isLogin");
                UserName = IsLoggedIn ? GetString(data, "uname") : null;
                return IsLoggedIn;
            }
            catch
            {
                IsLoggedIn = false;
                UserName = null;
                return false;
            }
        }

        public async Task<BilibiliQrSession> BeginQrLoginAsync(CancellationToken cancellationToken)
        {
            _cookies = new CookieContainer();
            IsLoggedIn = false;
            UserName = null;
            var root = await GetJsonAsync("https://passport.bilibili.com/x/passport-login/web/qrcode/generate", cancellationToken);
            EnsureSuccess(root);
            var data = AsDictionary(Get(root, "data"));
            var url = GetString(data, "url");
            var key = GetString(data, "qrcode_key");
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(key))
                throw new InvalidDataException("B站没有返回有效的登录二维码。");
            return new BilibiliQrSession { Url = url, Key = key };
        }

        public async Task<BilibiliLoginPollResult> PollQrLoginAsync(string key, CancellationToken cancellationToken)
        {
            var url = "https://passport.bilibili.com/x/passport-login/web/qrcode/poll?qrcode_key=" + Uri.EscapeDataString(key ?? string.Empty);
            var root = await GetJsonAsync(url, cancellationToken);
            EnsureSuccess(root);
            var data = AsDictionary(Get(root, "data"));
            var code = GetInt(data, "code");
            if (code == 0)
            {
                var loggedIn = await ValidateLoginAsync(cancellationToken);
                return new BilibiliLoginPollResult { Success = loggedIn, Message = loggedIn ? "登录成功" : "登录信息验证失败" };
            }
            if (code == 86038)
                return new BilibiliLoginPollResult { Expired = true, Message = "二维码已过期" };
            if (code == 86090)
                return new BilibiliLoginPollResult { Scanned = true, Message = "已扫码，请在手机确认" };
            return new BilibiliLoginPollResult { Message = "等待扫码" };
        }

        public async Task<BilibiliProbeResult> ProbeAsync(string roomId, string quality, CancellationToken cancellationToken)
        {
            try
            {
                var infoUrl = "https://api.live.bilibili.com/room/v1/Room/get_info?room_id=" + Uri.EscapeDataString(roomId ?? string.Empty);
                var root = await GetJsonAsync(infoUrl, cancellationToken);
                EnsureSuccess(root);
                var data = AsDictionary(Get(root, "data"));
                var canonicalRoomId = GetString(data, "room_id");
                var uid = GetString(data, "uid");
                var liveStatus = GetInt(data, "live_status");
                var roomTitle = GetString(data, "title");
                if (string.IsNullOrWhiteSpace(roomTitle))
                    roomTitle = GetString(data, "room_title");
                var displayName = await GetAnchorNameAsync(uid, cancellationToken);
                var result = new BilibiliProbeResult
                {
                    IsLive = liveStatus == 1,
                    Message = liveStatus == 1 ? "直播中" : "未开播",
                    DisplayName = displayName,
                    RoomTitle = CleanRoomTitle(roomTitle),
                    CanonicalRoomId = string.IsNullOrWhiteSpace(canonicalRoomId) ? roomId : canonicalRoomId
                };
                if (result.IsLive)
                {
                    var playInfo = await GetPlayInfoAsync(result.CanonicalRoomId, quality, cancellationToken);
                    result.StreamUrl = playInfo.StreamUrl;
                    result.AvailableQualities = playInfo.AvailableQualities;
                }
                if (result.IsLive && string.IsNullOrWhiteSpace(result.StreamUrl))
                {
                    result.HasError = true;
                    result.Message = "未获取到当前账号可用的B站直播流。";
                }
                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new BilibiliProbeResult { HasError = true, Message = ex.Message };
            }
        }

        private async Task<string> GetAnchorNameAsync(string uid, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(uid))
                return null;
            try
            {
                var url = "https://api.live.bilibili.com/live_user/v1/Master/info?uid=" + Uri.EscapeDataString(uid);
                var root = await GetJsonAsync(url, cancellationToken);
                var data = AsDictionary(Get(root, "data"));
                var info = AsDictionary(Get(data, "info"));
                return GetString(info, "uname");
            }
            catch
            {
                return null;
            }
        }

        private string CleanRoomTitle(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;
            value = value.Trim();
            return value.Length > 240 ? value.Substring(0, 240) : value;
        }

        private async Task<BilibiliPlayInfo> GetPlayInfoAsync(string roomId, string quality, CancellationToken cancellationToken)
        {
            var qn = GetQualityNumber(quality);
            var url = "https://api.live.bilibili.com/xlive/web-room/v2/index/getRoomPlayInfo?room_id=" + Uri.EscapeDataString(roomId) +
                      "&protocol=0,1&format=0,1,2&codec=0,1&qn=" + qn + "&platform=web&ptype=8&dolby=5&panorama=1";
            var root = await GetJsonAsync(url, cancellationToken);
            EnsureSuccess(root);
            var data = AsDictionary(Get(root, "data"));
            var playurlInfo = AsDictionary(Get(data, "playurl_info"));
            var playurl = AsDictionary(Get(playurlInfo, "playurl"));
            var candidates = new List<BilibiliStreamCandidate>();
            var acceptedQualityNumbers = new HashSet<int>();
            foreach (var streamObject in AsArray(Get(playurl, "stream")))
            {
                var stream = AsDictionary(streamObject);
                foreach (var formatObject in AsArray(Get(stream, "format")))
                {
                    var format = AsDictionary(formatObject);
                    var formatName = GetString(format, "format_name");
                    foreach (var codecObject in AsArray(Get(format, "codec")))
                    {
                        var codec = AsDictionary(codecObject);
                        var baseUrl = GetString(codec, "base_url");
                        var codecName = GetString(codec, "codec_name");
                        var currentQualityNumber = GetInt(codec, "current_qn");
                        if (currentQualityNumber > 0)
                            acceptedQualityNumbers.Add(currentQualityNumber);
                        foreach (var acceptedQuality in AsArray(Get(codec, "accept_qn")))
                        {
                            var acceptedQualityNumber = ToInt(acceptedQuality);
                            if (acceptedQualityNumber > 0)
                                acceptedQualityNumbers.Add(acceptedQualityNumber);
                        }
                        foreach (var infoObject in AsArray(Get(codec, "url_info")))
                        {
                            var info = AsDictionary(infoObject);
                            var host = GetString(info, "host");
                            var extra = GetString(info, "extra");
                            if (!string.IsNullOrWhiteSpace(host) && !string.IsNullOrWhiteSpace(baseUrl))
                                candidates.Add(new BilibiliStreamCandidate { Url = host + baseUrl + extra, Format = formatName, Codec = codecName });
                        }
                    }
                }
            }
            var selected = candidates
                .OrderBy(candidate => string.Equals(candidate.Format, "flv", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(candidate => string.Equals(candidate.Codec, "avc", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .FirstOrDefault();
            var availableQualities = BuildAvailableQualityLabels(playurl, acceptedQualityNumbers);
            if (selected != null)
                return new BilibiliPlayInfo { StreamUrl = selected.Url, AvailableQualities = availableQualities };

            var fallbackUrl = "https://api.live.bilibili.com/room/v1/Room/playUrl?cid=" + Uri.EscapeDataString(roomId) + "&quality=" + qn + "&platform=web";
            var fallbackRoot = await GetJsonAsync(fallbackUrl, cancellationToken);
            EnsureSuccess(fallbackRoot);
            var fallbackData = AsDictionary(Get(fallbackRoot, "data"));
            var durl = AsArray(Get(fallbackData, "durl"));
            if (durl.Length > 0)
                return new BilibiliPlayInfo { StreamUrl = GetString(AsDictionary(durl[0]), "url"), AvailableQualities = availableQualities };
            return new BilibiliPlayInfo { AvailableQualities = availableQualities };
        }

        private string[] BuildAvailableQualityLabels(IDictionary<string, object> playurl, HashSet<int> acceptedQualityNumbers)
        {
            var descriptions = new Dictionary<int, string>();
            foreach (var descriptionObject in AsArray(Get(playurl, "g_qn_desc")))
            {
                var description = AsDictionary(descriptionObject);
                var number = GetInt(description, "qn");
                var label = GetString(description, "desc");
                if (number > 0 && !string.IsNullOrWhiteSpace(label))
                    descriptions[number] = label.Trim();
            }

            var labels = new List<string> { "自动" };
            var knownNumbers = new[] { 30000, 20000, 15000, 10000, 400, 250, 150, 80 };
            foreach (var number in knownNumbers)
            {
                if (!acceptedQualityNumbers.Contains(number) || (!IsLoggedIn && number > 250))
                    continue;
                var label = GetKnownQualityLabel(number);
                if (!labels.Contains(label))
                    labels.Add(label);
            }

            foreach (var number in acceptedQualityNumbers.OrderByDescending(value => value))
            {
                if ((!IsLoggedIn && number > 250) || knownNumbers.Contains(number))
                    continue;
                string label;
                if (descriptions.TryGetValue(number, out label) && !labels.Contains(label))
                    labels.Add(label);
            }
            return labels.ToArray();
        }

        private static string GetKnownQualityLabel(int qualityNumber)
        {
            switch (qualityNumber)
            {
                case 30000: return "杜比";
                case 20000: return "4K";
                case 15000: return "2K";
                case 10000: return "原画";
                case 400: return "蓝光";
                case 250: return "超清";
                case 150: return "高清";
                case 80: return "流畅";
                default: return qualityNumber.ToString();
            }
        }

        private int GetQualityNumber(string quality)
        {
            switch (quality ?? "自动")
            {
                case "杜比": return 30000;
                case "4K": return 20000;
                case "2K": return 15000;
                case "原画": return 10000;
                case "蓝光": return 400;
                case "超清": return 250;
                case "高清": return 150;
                case "流畅": return 80;
                default: return IsLoggedIn ? 10000 : 250;
            }
        }

        private async Task<IDictionary<string, object>> GetJsonAsync(string url, CancellationToken cancellationToken)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0.0.0 Safari/537.36";
            request.Accept = "application/json, text/plain, */*";
            request.Referer = "https://live.bilibili.com/";
            request.CookieContainer = _cookies;
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            request.Timeout = 20000;
            request.ReadWriteTimeout = 20000;
            try
            {
                using (cancellationToken.Register(delegate { request.Abort(); }))
                using (var response = (HttpWebResponse)await request.GetResponseAsync())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    var text = await reader.ReadToEndAsync();
                    cancellationToken.ThrowIfCancellationRequested();
                    return AsDictionary(new JavaScriptSerializer().DeserializeObject(text));
                }
            }
            catch (WebException)
            {
                if (cancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException(cancellationToken);
                throw;
            }
        }

        private void EnsureSuccess(IDictionary<string, object> root)
        {
            var code = GetInt(root, "code");
            if (code != 0)
                throw new InvalidDataException(GetString(root, "message") ?? ("B站接口返回错误 " + code));
        }

        private static object Get(IDictionary<string, object> dictionary, string key)
        {
            if (dictionary == null)
                return null;
            object value;
            return dictionary.TryGetValue(key, out value) ? value : null;
        }

        private static IDictionary<string, object> AsDictionary(object value)
        {
            return value as IDictionary<string, object> ?? new Dictionary<string, object>();
        }

        private static object[] AsArray(object value)
        {
            return value as object[] ?? new object[0];
        }

        private static string GetString(IDictionary<string, object> dictionary, string key)
        {
            var value = Get(dictionary, key);
            return value == null ? null : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
        }

        private static int GetInt(IDictionary<string, object> dictionary, string key)
        {
            return ToInt(Get(dictionary, key));
        }

        private static int ToInt(object value)
        {
            if (value == null)
                return 0;
            try { return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture); }
            catch { return 0; }
        }

        private static bool GetBoolean(IDictionary<string, object> dictionary, string key)
        {
            var value = Get(dictionary, key);
            if (value == null)
                return false;
            try { return Convert.ToBoolean(value, System.Globalization.CultureInfo.InvariantCulture); }
            catch { return false; }
        }
    }
}
