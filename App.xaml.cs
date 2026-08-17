using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LiveBoard
{
    public partial class App : Application
    {
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
            AppDomain.CurrentDomain.AssemblyResolve += ResolveEmbeddedAssembly;
            base.OnStartup(e);
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
