using System;
using System.IO;

namespace SourceGit
{
    /// <summary>
    ///     Diagnostic trace for the launcher window geometry/state, used to investigate why a
    ///     maximized window changes size around the preferences dialog. Writes one line per
    ///     event into "window-trace.log" inside the config directory; failures are swallowed so
    ///     tracing can never break the app. Remove this class once the issue is settled.
    /// </summary>
    public static class WindowTrace
    {
        private static readonly object _lock = new object();
        private static string _file = null;

        public static void Write(string message)
        {
            try
            {
                lock (_lock)
                {
                    if (_file == null)
                    {
                        var dir = Native.OS.BasicDirectories?.ConfigDir;
                        if (string.IsNullOrEmpty(dir))
                            dir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

                        if (string.IsNullOrEmpty(dir))
                            return;

                        _file = Path.Combine(dir, "window-trace.log");
                        File.AppendAllText(_file, $"==== session start {DateTime.Now:yyyy-MM-dd HH:mm:ss} pid={Environment.ProcessId} ===={Environment.NewLine}");
                    }

                    File.AppendAllText(_file, $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
                }
            }
            catch
            {
                // Diagnostics must never affect the app.
            }
        }
    }
}
