using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace LiveBoard
{
    /// <summary>
    /// 启动耗时诊断：把关键节点的耗时写入 %LocalAppData%\LiveBoard\startup.log。
    /// 只做追加写，所有异常都被吞掉，绝不影响主流程；每次启动覆盖上一份日志。
    /// </summary>
    internal static class StartupTrace
    {
        private static readonly Stopwatch Watch = Stopwatch.StartNew();
        private static readonly object Gate = new object();
        private static readonly string LogPath = Path.Combine(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiveBoard"),
            "startup.log");

        private static bool _started;
        private static int _roomCardRefreshCount;
        private static double _roomCardRefreshMs;

        public static void Mark(string label)
        {
            Write(string.Format("[{0,8:N0} ms] {1}", Watch.Elapsed.TotalMilliseconds, label));
        }

        public static void CountRoomCardRefresh(double milliseconds)
        {
            lock (Gate)
            {
                _roomCardRefreshCount++;
                _roomCardRefreshMs += milliseconds;
            }
        }

        public static void Summary(string label)
        {
            int count;
            double total;
            lock (Gate)
            {
                count = _roomCardRefreshCount;
                total = _roomCardRefreshMs;
            }
            Write(string.Format("[{0,8:N0} ms] {1}：RefreshRoomCards 共 {2} 次，累计 {3:N0} ms",
                Watch.Elapsed.TotalMilliseconds, label, count, total));
        }

        private static void Write(string line)
        {
            try
            {
                lock (Gate)
                {
                    var directory = Path.GetDirectoryName(LogPath);
                    if (!string.IsNullOrWhiteSpace(directory))
                        Directory.CreateDirectory(directory);
                    if (!_started)
                    {
                        _started = true;
                        File.WriteAllText(LogPath, "LiveBoard 启动耗时诊断" + Environment.NewLine, new UTF8Encoding(true));
                    }
                    File.AppendAllText(LogPath,
                        DateTime.Now.ToString("HH:mm:ss.fff") + "  " + line + Environment.NewLine,
                        new UTF8Encoding(true));
                }
            }
            catch
            {
            }
        }
    }
}
