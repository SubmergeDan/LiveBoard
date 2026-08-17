using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LiveBoard
{
    public partial class App : Application
    {
        private Mutex _singleInstanceMutex;
        private bool _ownsSingleInstanceMutex;

        private void ComboPopupScrollViewer_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            var scrollViewer = sender as ScrollViewer;
            if (scrollViewer == null || e.Delta == 0)
                return;

            var distance = Math.Max(12.0, Math.Abs(e.Delta) * 0.45);
            var nextOffset = scrollViewer.VerticalOffset + (e.Delta > 0 ? -distance : distance);
            scrollViewer.ScrollToVerticalOffset(Math.Max(0, Math.Min(scrollViewer.ScrollableHeight, nextOffset)));
            e.Handled = true;
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            bool ownsMutex;
            _singleInstanceMutex = new Mutex(true, "Local\\LiveBoard.SingleInstance", out ownsMutex);
            if (!ownsMutex)
            {
                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;
                MessageBox.Show("LiveBoard 已在运行。请关闭当前窗口后再启动新版本。", "LiveBoard", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }
            _ownsSingleInstanceMutex = true;
            AppDomain.CurrentDomain.AssemblyResolve += ResolveEmbeddedAssembly;
            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (_singleInstanceMutex != null && _ownsSingleInstanceMutex)
            {
                _singleInstanceMutex.ReleaseMutex();
                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;
                _ownsSingleInstanceMutex = false;
            }
            base.OnExit(e);
        }

        private static Assembly ResolveEmbeddedAssembly(object sender, ResolveEventArgs args)
        {
            var requested = new AssemblyName(args.Name);
            if (!string.Equals(requested.Name, "QRCoder", StringComparison.OrdinalIgnoreCase))
                return null;

            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("LiveBoard.Resources.QRCoder.dll"))
            {
                if (stream == null)
                    return null;
                using (var memory = new MemoryStream())
                {
                    stream.CopyTo(memory);
                    return Assembly.Load(memory.ToArray());
                }
            }
        }
    }
}
