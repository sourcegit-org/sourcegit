using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    internal static class ExternalProcess
    {
        internal readonly record struct Output(string Text, bool Truncated);
        internal readonly record struct Result(int ExitCode, Output StdOut, string StdErr);

        public static ProcessStartInfo CreateStartInfo(string executable, string directory)
        {
            var path = ResolveExecutable(executable);
            if (path == null)
                throw new FileNotFoundException(App.Text("ExternalCLI.ExecutableNotFound", executable));

            return new ProcessStartInfo()
            {
                FileName = path,
                WorkingDirectory = directory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
        }

        public static async Task<Result> RunAsync(ProcessStartInfo start, string input, int maxOutputChars, int? timeoutSeconds,
            CancellationToken cancellation, Action<string> onLine = null)
        {
            using var process = new Process() { StartInfo = start };
            using var deadline = timeoutSeconds.HasValue ? CancellationTokenSource.CreateLinkedTokenSource(cancellation) : null;
            deadline?.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds.GetValueOrDefault()));
            var token = deadline?.Token ?? cancellation;
            token.ThrowIfCancellationRequested();
            try
            {
                process.Start();
            }
            catch (Win32Exception e)
            {
                throw new InvalidOperationException(App.Text("ExternalCLI.StartError", e.Message), e);
            }

            var stdout = ReadAsync(process.StandardOutput, maxOutputChars, token, onLine);
            var stderr = ReadAsync(process.StandardError, 16 * 1024, token);
            var stdin = start.RedirectStandardInput ? WriteInputAsync(process, input, token) : Task.CompletedTask;
            var io = Task.WhenAll(stdout, stderr, stdin, process.WaitForExitAsync(token));
            try
            {
                await io.WaitAsync(token).ConfigureAwait(false);
                return new Result(process.ExitCode, stdout.Result, stderr.Result.Text.Trim());
            }
            catch (OperationCanceledException) when (deadline?.IsCancellationRequested == true && !cancellation.IsCancellationRequested)
            {
                throw new TimeoutException(App.Text("ExternalCLI.TimeoutError", timeoutSeconds));
            }
            finally
            {
                if (!io.IsCompleted || !process.HasExited)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (Exception e) when (e is InvalidOperationException or Win32Exception or AggregateException)
                    {
                        // The process may already have exited.
                    }

                    // Drain pending reads after cancellation without waiting on inherited pipes indefinitely.
                    try
                    {
                        await io.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // Preserve the original result of the operation.
                    }
                }
            }
        }

        public static string FormatError(Result result)
        {
            var message = App.Text("ExternalCLI.ExitError", result.ExitCode);
            return result.StdErr.Length == 0 ? message : $"{message}\n{result.StdErr}";
        }

        private static string ResolveExecutable(string executable)
        {
            if (string.IsNullOrWhiteSpace(executable))
                return null;

            executable = executable.Trim();
            if (Path.IsPathFullyQualified(executable))
                return ResolveFile(executable);

            if (Path.GetFileName(executable) != executable)
                return null;

            var paths = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var candidate in paths.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var directory = candidate.Trim('"');
                if (Path.IsPathFullyQualified(directory) && ResolveFile(Path.Combine(directory, executable)) is { } found)
                    return found;
            }

            return null;
        }

        private static string ResolveFile(string path)
        {
            if (!OperatingSystem.IsWindows())
                return File.Exists(path) ? path : null;

            if (!Path.HasExtension(path))
                path += ".exe";

            return Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path) ? path : null;
        }

        private static async Task WriteInputAsync(Process process, string input, CancellationToken cancellation)
        {
            var stream = process.StandardInput.BaseStream;
            try
            {
                if (!string.IsNullOrEmpty(input))
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(input), cancellation).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // A command can report an error and exit without reading stdin.
            }
            finally
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }

        private static async Task<Output> ReadAsync(StreamReader reader, int limit, CancellationToken cancellation, Action<string> onLine = null)
        {
            var text = new StringBuilder();
            var lineStart = 0;
            var truncated = false;
            var buffer = new char[4096];
            while (await reader.ReadAsync(buffer.AsMemory(), cancellation).ConfigureAwait(false) is var count && count > 0)
            {
                var remaining = Math.Min(limit - text.Length, count);
                if (remaining < count)
                    truncated = true;

                var offset = text.Length;
                text.Append(buffer, 0, remaining);
                if (onLine != null)
                {
                    for (var i = 0; i < remaining; i++)
                    {
                        if (buffer[i] == '\n')
                        {
                            onLine(text.ToString(lineStart, offset + i - lineStart).TrimEnd('\r'));
                            lineStart = offset + i + 1;
                        }
                    }
                }
            }

            if (onLine != null && !truncated && lineStart < text.Length)
                onLine(text.ToString(lineStart, text.Length - lineStart).TrimEnd('\r'));
            if (truncated && text.Length > 0 && char.IsHighSurrogate(text[^1]))
                text.Length--;
            return new Output(text.ToString(), truncated);
        }
    }
}
