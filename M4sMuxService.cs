using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LiveBoard
{
    public sealed class M4sMuxResult
    {
        public bool Success { get; set; }
        public bool Cancelled { get; set; }
        public string OutputPath { get; set; }
        public string ErrorText { get; set; }
    }

    public sealed class M4sMuxService
    {
        public async Task<M4sMuxResult> MuxAsync(string ffmpegPath, string videoPath, string audioPath, string outputPath, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath))
                throw new FileNotFoundException("内置 FFmpeg 不可用。", ffmpegPath);
            if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
                throw new FileNotFoundException("视频 M4S 文件不存在。", videoPath);
            if (string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath))
                throw new FileNotFoundException("音频 M4S 文件不存在。", audioPath);
            if (string.IsNullOrWhiteSpace(outputPath))
                throw new ArgumentException("输出文件路径为空。", "outputPath");

            var outputDirectory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(outputDirectory))
                Directory.CreateDirectory(outputDirectory);

            var errors = new StringBuilder();
            var errorLock = new object();
            var completion = new TaskCompletionSource<int>();
            var arguments = "-y -hide_banner -loglevel warning -i \"" + videoPath + "\" -i \"" + audioPath +
                            "\" -map 0:v:0 -map 1:a:0 -c:v copy -c:a copy -map_metadata 0 -movflags +faststart \"" + outputPath + "\"";
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = arguments,
                    WorkingDirectory = Path.GetDirectoryName(ffmpegPath),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                },
                EnableRaisingEvents = true
            };
            process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
            {
                if (string.IsNullOrWhiteSpace(e.Data))
                    return;
                lock (errorLock)
                {
                    errors.AppendLine(e.Data);
                    if (errors.Length > 6000)
                        errors.Remove(0, errors.Length - 6000);
                }
            };
            process.Exited += delegate
            {
                try { completion.TrySetResult(process.ExitCode); }
                catch { completion.TrySetResult(-1); }
            };

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!process.Start())
                    throw new InvalidOperationException("无法启动 FFmpeg。" );
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                if (process.HasExited)
                    completion.TrySetResult(process.ExitCode);

                using (cancellationToken.Register(delegate
                {
                    ThreadPool.QueueUserWorkItem(delegate { StopProcess(process); });
                }))
                {
                    var exitCode = await completion.Task;
                    process.WaitForExit();
                    var cancelled = cancellationToken.IsCancellationRequested;
                    var errorText = GetErrorText(errors, errorLock);
                    if (cancelled)
                    {
                        DeleteIncompleteOutput(outputPath);
                        return new M4sMuxResult { Cancelled = true, OutputPath = outputPath, ErrorText = errorText };
                    }
                    if (exitCode != 0 || !File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
                    {
                        DeleteIncompleteOutput(outputPath);
                        return new M4sMuxResult
                        {
                            OutputPath = outputPath,
                            ErrorText = string.IsNullOrWhiteSpace(errorText) ? "FFmpeg 无法识别输入的 M4S 音视频轨道。" : errorText
                        };
                    }
                    return new M4sMuxResult { Success = true, OutputPath = outputPath };
                }
            }
            finally
            {
                process.Dispose();
            }
        }

        private static void StopProcess(Process process)
        {
            try
            {
                if (process == null || process.HasExited)
                    return;
                try
                {
                    process.StandardInput.WriteLine("q");
                    process.StandardInput.Flush();
                }
                catch
                {
                }
                if (!process.WaitForExit(2000))
                    process.Kill();
            }
            catch
            {
            }
        }

        private static string GetErrorText(StringBuilder errors, object errorLock)
        {
            lock (errorLock)
            {
                var value = errors.ToString().Replace("\r", " ").Replace("\n", " ").Trim();
                return value.Length > 900 ? value.Substring(value.Length - 900) : value;
            }
        }

        private static void DeleteIncompleteOutput(string outputPath)
        {
            try
            {
                if (File.Exists(outputPath))
                    File.Delete(outputPath);
            }
            catch
            {
            }
        }
    }
}
