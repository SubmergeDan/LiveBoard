using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace LiveBoard
{
    public enum BrowserCookieState
    {
        NotSelected,
        Ready,
        Missing,
        Locked,
        Encrypted
    }

    public sealed class BrowserCookieReport
    {
        public BrowserCookieState State { get; set; }
        public string BrowserLabel { get; set; }
        public string DatabasePath { get; set; }
        public string Detail { get; set; }
        public bool HasAppBoundCookies { get; set; }

        public bool IsUsable
        {
            get { return State == BrowserCookieState.Ready; }
        }
    }

    /// <summary>
    /// 探测浏览器 Cookie 数据库是否真的能被 yt-dlp / gallery-dl 读取。
    /// yt-dlp 的 --cookies-from-browser 依赖“复制 Cookies 文件 + 自行解密”，
    /// 在 Chromium 系浏览器上有两个必失败场景：
    ///   1. 浏览器正在运行并独占锁定 Cookies 数据库（复制直接抛 PermissionError）；
    ///   2. 数据库里的 Cookie 已使用 App-Bound Encryption（v20）加密，yt-dlp 至今只支持 v10/DPAPI。
    /// </summary>
    public static class BrowserCookieService
    {
        public const string NoBrowserSource = "不使用浏览器登录";
        public const string CookieFileSource = "cookies.txt 文件";

        private const string EdgeLabel = "Microsoft Edge";
        private const string ChromeLabel = "Google Chrome";
        private const string FirefoxLabel = "Mozilla Firefox";

        // Chromium 在启用 App-Bound Encryption 后会写入该偏好项。
        private const string AppBoundKeyName = "app_bound_encrypted_key";

        public static string[] BrowserLabels
        {
            get { return new[] { EdgeLabel, ChromeLabel, FirefoxLabel }; }
        }

        public static string BrowserValue(string browserLabel)
        {
            if (string.Equals(browserLabel, EdgeLabel, StringComparison.OrdinalIgnoreCase))
                return "edge";
            if (string.Equals(browserLabel, ChromeLabel, StringComparison.OrdinalIgnoreCase))
                return "chrome";
            if (string.Equals(browserLabel, FirefoxLabel, StringComparison.OrdinalIgnoreCase))
                return "firefox";
            return null;
        }

        public static bool IsBrowserLabel(string browserLabel)
        {
            return BrowserValue(browserLabel) != null;
        }

        public static bool IsExplicitSource(string source)
        {
            return string.Equals(source, CookieFileSource, StringComparison.OrdinalIgnoreCase);
        }

        public static string BrowserExecutable(string browserLabel)
        {
            var value = BrowserValue(browserLabel);
            if (value == "edge")
                return FirstExisting(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe"));
            if (value == "chrome")
                return FirstExisting(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe"));
            if (value == "firefox")
                return FirstExisting(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Mozilla Firefox", "firefox.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Mozilla Firefox", "firefox.exe"));
            return null;
        }

        public static BrowserCookieReport Inspect(string browserLabel)
        {
            var report = new BrowserCookieReport
            {
                BrowserLabel = string.IsNullOrWhiteSpace(browserLabel) ? NoBrowserSource : browserLabel
            };

            if (string.IsNullOrWhiteSpace(browserLabel) ||
                string.Equals(browserLabel, NoBrowserSource, StringComparison.OrdinalIgnoreCase))
            {
                report.State = BrowserCookieState.NotSelected;
                report.Detail = "未使用浏览器登录状态。";
                return report;
            }

            if (string.Equals(browserLabel, CookieFileSource, StringComparison.OrdinalIgnoreCase))
            {
                report.State = BrowserCookieState.NotSelected;
                report.Detail = "使用导入的 cookies.txt 文件。";
                return report;
            }

            var value = BrowserValue(browserLabel);
            if (value == null)
            {
                report.State = BrowserCookieState.Missing;
                report.Detail = "无法识别的登录来源。";
                return report;
            }

            return string.Equals(value, "firefox", StringComparison.OrdinalIgnoreCase)
                ? InspectFirefox(report)
                : InspectChromium(report, value);
        }

        private static BrowserCookieReport InspectChromium(BrowserCookieReport report, string value)
        {
            var root = value == "edge" ? ChromiumUserDataDirectory("Microsoft\\Edge") : ChromiumUserDataDirectory("Google\\Chrome");
            var executable = BrowserExecutable(report.BrowserLabel);
            if (executable == null && (root == null || !Directory.Exists(root)))
            {
                report.State = BrowserCookieState.Missing;
                report.Detail = "未检测到 " + report.BrowserLabel + "，请先安装或改用其他登录来源。";
                return report;
            }

            var database = root == null ? null : FindNewestCookieDatabase(root);
            if (string.IsNullOrWhiteSpace(database))
            {
                report.State = BrowserCookieState.Missing;
                report.Detail = "没有找到 " + report.BrowserLabel + " 的 Cookie 数据库，请先在该浏览器中登录一次。";
                return report;
            }

            report.DatabasePath = database;
            if (!CanOpenShared(database))
            {
                report.State = BrowserCookieState.Locked;
                report.Detail = report.BrowserLabel + " 正在运行并锁定了 Cookie 数据库，任何工具都无法复制该文件。" +
                                "请先完全退出 " + report.BrowserLabel + " 再解析；" +
                                "若退出后仍无法读取（新版 Chromium 的 App-Bound 加密），请改用「" + CookieFileSource + "」。";
                return report;
            }

            if (ContainsAppBoundCookies(database))
            {
                report.HasAppBoundCookies = true;
                report.State = BrowserCookieState.Encrypted;
                report.Detail = report.BrowserLabel + " 的 Cookie 使用 App-Bound 加密（v20）：只有浏览器自己能解密，" +
                                "yt-dlp 与任何第三方工具都读不到登录态。请用浏览器扩展导出「" + CookieFileSource + "」后导入，" +
                                "或改用 Mozilla Firefox（Cookie 未加密，可直接读取）。";
                return report;
            }

            report.State = BrowserCookieState.Ready;
            report.Detail = report.BrowserLabel + " 的 Cookie 数据库可读取，将直接使用浏览器登录状态。";
            return report;
        }

        private static BrowserCookieReport InspectFirefox(BrowserCookieReport report)
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mozilla", "Firefox");
            if (!Directory.Exists(root))
            {
                report.State = BrowserCookieState.Missing;
                report.Detail = "未检测到 Mozilla Firefox。";
                return report;
            }

            var database = FindFirefoxCookieDatabase(root);
            if (string.IsNullOrWhiteSpace(database))
            {
                report.State = BrowserCookieState.Missing;
                report.Detail = "没有找到 Firefox 的 cookies.sqlite，请先在该浏览器中登录一次。";
                return report;
            }

            report.DatabasePath = database;
            if (!CanOpenShared(database))
            {
                report.State = BrowserCookieState.Locked;
                report.Detail = "Firefox 的 Cookie 数据库当前不可读，请先退出 Firefox 再解析。";
                return report;
            }

            report.State = BrowserCookieState.Ready;
            report.Detail = "Firefox 的 Cookie 数据库可读取，将直接使用浏览器登录状态；若 Firefox 里没有登录目标站点，登录态仍然会缺失。";
            return report;
        }

        public static string ChromiumVendorPath(string browserLabel)
        {
            if (string.Equals(browserLabel, EdgeLabel, StringComparison.OrdinalIgnoreCase))
                return "Microsoft\\Edge";
            if (string.Equals(browserLabel, ChromeLabel, StringComparison.OrdinalIgnoreCase))
                return "Google\\Chrome";
            return null;
        }

        public static string ChromiumUserDataDirectory(string vendorPath)
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(local))
                return null;
            return Path.Combine(local, vendorPath.Split('\\')[0], vendorPath.Split('\\')[1], "User Data");
        }

        public static string FindNewestCookieDatabase(string root)
        {
            var candidates = new List<string>();
            CollectCookieCandidates(root, candidates);
            try
            {
                foreach (var directory in Directory.GetDirectories(root))
                    CollectCookieCandidates(directory, candidates);
            }
            catch
            {
            }

            string best = null;
            var bestTime = DateTime.MinValue;
            foreach (var candidate in candidates)
            {
                try
                {
                    var time = File.GetLastWriteTimeUtc(candidate);
                    if (best == null || time > bestTime)
                    {
                        best = candidate;
                        bestTime = time;
                    }
                }
                catch
                {
                }
            }
            return best;
        }

        private static void CollectCookieCandidates(string directory, IList<string> candidates)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                return;
            var direct = Path.Combine(directory, "Cookies");
            if (File.Exists(direct))
                candidates.Add(direct);
            var network = Path.Combine(Path.Combine(directory, "Network"), "Cookies");
            if (File.Exists(network))
                candidates.Add(network);
        }

        private static string FindFirefoxCookieDatabase(string root)
        {
            var preferred = ReadFirefoxDefaultProfile(root);
            if (!string.IsNullOrWhiteSpace(preferred))
            {
                var database = Path.Combine(preferred, "cookies.sqlite");
                if (File.Exists(database))
                    return database;
            }

            var profilesRoot = Path.Combine(root, "Profiles");
            if (!Directory.Exists(profilesRoot))
                return null;

            string best = null;
            var bestTime = DateTime.MinValue;
            try
            {
                foreach (var directory in Directory.GetDirectories(profilesRoot))
                {
                    var database = Path.Combine(directory, "cookies.sqlite");
                    if (!File.Exists(database))
                        continue;
                    var time = File.GetLastWriteTimeUtc(database);
                    if (best == null || time > bestTime)
                    {
                        best = database;
                        bestTime = time;
                    }
                }
            }
            catch
            {
            }
            return best;
        }

        private static string ReadFirefoxDefaultProfile(string root)
        {
            var iniPath = Path.Combine(root, "profiles.ini");
            if (!File.Exists(iniPath))
                return null;
            try
            {
                string[] lines;
                try
                {
                    lines = File.ReadAllLines(iniPath, Encoding.UTF8);
                }
                catch
                {
                    lines = File.ReadAllLines(iniPath);
                }

                string section = null;
                string path = null;
                var isRelative = true;
                var isDefault = false;
                foreach (var rawLine in lines)
                {
                    var line = (rawLine ?? string.Empty).Trim();
                    if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal) || line.StartsWith(";", StringComparison.Ordinal))
                        continue;
                    if (line.StartsWith("[", StringComparison.Ordinal))
                    {
                        if (section != null && path != null && isDefault)
                            break;
                        section = line.Trim('[', ']');
                        path = null;
                        isRelative = true;
                        isDefault = false;
                        continue;
                    }

                    var separator = line.IndexOf('=');
                    if (separator <= 0)
                        continue;
                    var key = line.Substring(0, separator).Trim();
                    var value = line.Substring(separator + 1).Trim();
                    if (string.Equals(key, "Path", StringComparison.OrdinalIgnoreCase))
                        path = value;
                    else if (string.Equals(key, "IsRelative", StringComparison.OrdinalIgnoreCase))
                        isRelative = !string.Equals(value, "0", StringComparison.Ordinal);
                    else if (string.Equals(key, "Default", StringComparison.OrdinalIgnoreCase))
                        isDefault = string.Equals(value, "1", StringComparison.Ordinal);
                }

                if (path == null || !isDefault)
                {
                    // 兼容 [Install...] 段落里的 Default=<相对路径> 写法。
                    var installDefault = ReadFirefoxInstallDefault(lines);
                    if (!string.IsNullOrWhiteSpace(installDefault))
                        return Path.Combine(root, installDefault.Replace('/', Path.DirectorySeparatorChar));
                    return null;
                }

                return isRelative
                    ? Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar))
                    : path;
            }
            catch
            {
                return null;
            }
        }

        private static string ReadFirefoxInstallDefault(IEnumerable<string> lines)
        {
            foreach (var rawLine in lines)
            {
                var line = (rawLine ?? string.Empty).Trim();
                if (line.StartsWith("Default=", StringComparison.OrdinalIgnoreCase) && line.Length > 8)
                    return line.Substring(8);
            }
            return null;
        }

        public static bool CanOpenShared(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return false;
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    stream.ReadByte();
                }
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>
        /// SQLite 记录里 blob 的开头就是加密版本前缀（v10/v20），
        /// 直接扫描原始字节即可判断数据库里是否存在 App-Bound 加密的 Cookie。
        /// </summary>
        public static bool ContainsAppBoundCookies(string path)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    var buffer = new byte[64 * 1024];
                    var previous = (byte)0;
                    var previousPrevious = (byte)0;
                    int read;
                    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        for (var index = 0; index < read; index++)
                        {
                            var current = buffer[index];
                            if (current == (byte)'0' && previous == (byte)'2' && previousPrevious == (byte)'v')
                                return true;
                            previousPrevious = previous;
                            previous = current;
                        }
                    }
                }
            }
            catch
            {
            }
            return false;
        }

        public static bool HasAppBoundEncryptionKey(string browserLabel)
        {
            var value = BrowserValue(browserLabel);
            if (value == null || value == "firefox")
                return false;
            var root = value == "edge" ? ChromiumUserDataDirectory("Microsoft\\Edge") : ChromiumUserDataDirectory("Google\\Chrome");
            if (string.IsNullOrWhiteSpace(root))
                return false;
            var localState = Path.Combine(root, "Local State");
            if (!File.Exists(localState))
                return false;
            try
            {
                var text = File.ReadAllText(localState, Encoding.UTF8);
                return text.IndexOf(AppBoundKeyName, StringComparison.Ordinal) >= 0;
            }
            catch
            {
                return false;
            }
        }

        public static bool TryDescribeCookieFile(string path, out string summary, out string error)
        {
            summary = null;
            error = null;
            if (string.IsNullOrWhiteSpace(path))
            {
                error = "尚未选择 cookies.txt 文件。";
                return false;
            }
            if (!File.Exists(path))
            {
                error = "找不到 cookies.txt 文件：" + path;
                return false;
            }

            string[] lines;
            try
            {
                lines = File.ReadAllLines(path);
            }
            catch (Exception ex)
            {
                error = "无法读取 cookies.txt：" + ex.Message;
                return false;
            }

            var count = lines.Count(line => IsCookieLine(line));
            if (count == 0)
            {
                error = "该文件里没有 Cookie 记录，请确认导出的是 Netscape 格式的 cookies.txt。";
                return false;
            }

            summary = "已读取 " + count.ToString(CultureInfo.InvariantCulture) + " 条 Cookie";
            return true;
        }

        public static bool IsCookieLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#", StringComparison.Ordinal))
                return false;
            return line.Split('\t').Length >= 7;
        }

        public static string BuildCookieFileText(IEnumerable<CookieRecord> cookies)
        {
            var builder = new StringBuilder();
            builder.Append("# Netscape HTTP Cookie File\r\n");
            builder.Append("# 由 LiveBoard 内置登录同步生成\r\n");
            foreach (var cookie in cookies ?? Enumerable.Empty<CookieRecord>())
            {
                if (cookie == null || string.IsNullOrWhiteSpace(cookie.Name))
                    continue;
                var domain = string.IsNullOrWhiteSpace(cookie.Domain) ? cookie.Host : cookie.Domain;
                builder.Append(domain).Append('\t')
                    .Append(domain.StartsWith(".", StringComparison.Ordinal) ? "TRUE" : "FALSE").Append('\t')
                    .Append(string.IsNullOrWhiteSpace(cookie.Path) ? "/" : cookie.Path).Append('\t')
                    .Append(cookie.Secure ? "TRUE" : "FALSE").Append('\t')
                    .Append(cookie.Expires.ToString(CultureInfo.InvariantCulture)).Append('\t')
                    .Append(cookie.Name).Append('\t')
                    .Append(cookie.Value ?? string.Empty).Append("\r\n");
            }
            return builder.ToString();
        }

        private static string FirstExisting(params string[] paths)
        {
            foreach (var path in paths)
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    return path;
            }
            return null;
        }
    }

    public sealed class CookieRecord
    {
        public string Host { get; set; }
        public string Domain { get; set; }
        public string Path { get; set; }
        public string Name { get; set; }
        public string Value { get; set; }
        public bool Secure { get; set; }
        public long Expires { get; set; }
    }
}
