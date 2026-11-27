using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.AI
{
    internal enum ExternalCliTask
    {
        CommitMessage,
        DiffAnalysis,
        PullRequestDraft,
    }

    internal readonly record struct ExternalCliEvent(string Type, string Label, string Payload, string Thinking = null, bool ContinuesThinking = false);

    internal sealed class ExternalCli
    {
        internal readonly record struct Reply(string Text, bool Restarted);

        public ExternalCli(ExternalCliProfile profile, string chatModel = null, string chatEffort = null)
        {
            _executable = profile.Executable?.Trim();
            _arguments = [
                .. ExternalCliProfile.ReadArguments(profile.Arguments),
                .. ExternalCliProfile.ExpandChatArguments(profile.ChatModels, profile.ChatModelArguments, ExternalCliProfile.ChatModelToken, chatModel),
                .. ExternalCliProfile.ExpandChatArguments(profile.ChatEfforts, profile.ChatEffortArguments, ExternalCliProfile.ChatEffortToken, chatEffort),
            ];
            _resumeArguments = ExternalCliProfile.ReadArguments(profile.ResumeArguments);
            _usePromptFile = profile.UsePromptFile;
            _structured = profile.StructuredOutput;
            _resumeGeneration = profile.ResumeGeneration;
            _additionalPrompt = profile.AdditionalPrompt;
            _key = Hash(string.Join('\0',
                profile.Id.ToString(), _executable, _usePromptFile.ToString(), _structured.ToString(), _additionalPrompt,
                Hash(string.Join('\0', _arguments)), Hash(string.Join('\0', _resumeArguments))));
        }

        public async Task<Reply> RunAsync(string worktree, ExternalCliTask task, string prompt, CancellationToken cancellation,
            Action<ExternalCliEvent> emit = null, Guid conversationId = default)
        {
            var fileToken = _arguments.Any(static argument => argument.Contains(PromptFileToken, StringComparison.Ordinal));
            if (fileToken != _usePromptFile || _arguments.Any(static argument => argument.Contains(SessionToken, StringComparison.Ordinal)) ||
                _resumeArguments.Any(static argument => argument.Contains(PromptFileToken, StringComparison.Ordinal)) ||
                (_resumeArguments.Length > 0 && (!_structured ||
                 !_resumeArguments.Any(static argument => argument.Contains(SessionToken, StringComparison.Ordinal)))))
                throw new InvalidOperationException(App.Text("ExternalCLI.InvalidArguments"));

            if (!string.IsNullOrWhiteSpace(_additionalPrompt))
                prompt = $"User preferences for the answer:\n{_additionalPrompt}\n\n{prompt}";

            if (!_structured || _resumeArguments.Length == 0 || (task != ExternalCliTask.DiffAnalysis && !_resumeGeneration))
                return new Reply((await RunOnceAsync(worktree, prompt, null, cancellation, emit).ConfigureAwait(false)).Text, false);

            var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(worktree));
            if (OperatingSystem.IsWindows())
                directory = directory.ToUpperInvariant();

            var scope = $"{directory}\0{_key}\0{task}";
            if (conversationId != Guid.Empty)
                scope += $"\0{conversationId:N}";
            var path = Path.Combine(Native.OS.BasicDirectories.ConfigDir, "external-cli-sessions", Hash(scope) + ".id");
            using var sessionLock = await LockAsync(path, cancellation).ConfigureAwait(false);
            var id = sessionLock == null ? null : LoadSession(path);
            var restarted = false;
            Response reply;
            try
            {
                reply = await RunOnceAsync(worktree, prompt, id, cancellation, emit).ConfigureAwait(false);
            }
            catch (SessionExpiredException) when (id != null)
            {
                // Invalidate a rejected ID even if the fresh request fails.
                if (sessionLock != null)
                    SaveSession(path, null);
                restarted = true;
                id = null;
                reply = await RunOnceAsync(worktree, prompt, null, cancellation, emit).ConfigureAwait(false);
            }

            if (sessionLock != null)
                SaveSession(path, reply.Session ?? id);
            return new Reply(reply.Text, restarted);
        }

        private async Task<Response> RunOnceAsync(string worktree, string prompt, string session, CancellationToken cancellation,
            Action<ExternalCliEvent> emit)
        {
            var start = Commands.ExternalProcess.CreateStartInfo(_executable, worktree);
            DirectoryInfo temp = null;
            string file = null;
            try
            {
                if (_usePromptFile)
                {
                    temp = Directory.CreateTempSubdirectory("sourcegit-ai-");
                    file = Path.Combine(temp.FullName, "prompt.txt");
                    await File.WriteAllTextAsync(file, prompt, new UTF8Encoding(false), cancellation).ConfigureAwait(false);
                }

                foreach (var argument in _arguments)
                    start.ArgumentList.Add(file == null ? argument : argument.Replace(PromptFileToken, file, StringComparison.Ordinal));
                if (session != null)
                {
                    foreach (var argument in _resumeArguments)
                        start.ArgumentList.Add(argument.Replace(SessionToken, session, StringComparison.Ordinal));
                }

                var events = _structured ? new Events(emit) : null;
                var result = await Commands.ExternalProcess.RunAsync(start, _usePromptFile ? null : prompt, MaxOutputChars, timeoutSeconds: null,
                    cancellation, events == null ? null : events.Consume).ConfigureAwait(false);
                if (result.StdOut.Truncated)
                    throw new InvalidOperationException(App.Text("ExternalCLI.OutputLimit"));
                if (events?.ErrorCode == "session_expired" && session != null)
                    throw new SessionExpiredException();
                if (events?.Error != null)
                    throw new InvalidOperationException(events.Error);
                if (result.ExitCode != 0)
                    throw new InvalidOperationException(Commands.ExternalProcess.FormatError(result));

                var answer = (events == null ? result.StdOut.Text : events.Answer)?.Trim();
                if (string.IsNullOrEmpty(answer))
                    throw new InvalidOperationException(App.Text("ExternalCLI.EmptyOutput"));
                return new Response(answer, events?.Session);
            }
            finally
            {
                if (temp != null)
                {
                    try
                    {
                        File.Delete(file);
                        temp.Delete();
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        // The command may still have the temporary file open.
                    }
                }
            }
        }

        private sealed class Events(Action<ExternalCliEvent> emit)
        {
            public string Answer { get; private set; }
            public string Session { get; private set; }
            public string Error { get; private set; }
            public string ErrorCode { get; private set; }

            public void Consume(string line)
            {
                if (string.IsNullOrWhiteSpace(line) || Error != null)
                    return;

                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    var type = GetString(root, "type");
                    var text = GetString(root, "text");
                    if (type == null)
                        throw new JsonException();

                    var continuesThinking = _lastThinking;
                    _lastThinking = type == "thinking";
                    switch (type)
                    {
                        case "answer" when text != null && Answer == null:
                            Answer = text;
                            break;
                        case "thinking" when text != null:
                            emit?.Invoke(new("thinking", null, null, text, continuesThinking));
                            break;
                        case "tool_call" or "tool_result" when text != null:
                            emit?.Invoke(new(type, GetString(root, "label"), text));
                            break;
                        case "session" when IsValidSession(GetString(root, "id")):
                            Session = GetString(root, "id");
                            break;
                        case "error" when text != null:
                            ErrorCode = GetString(root, "code");
                            Error = text.Length == 0 ? App.Text("ExternalCLI.InvalidOutput") : text;
                            break;
                        case "answer" or "thinking" or "tool_call" or "tool_result" or "session" or "error":
                            throw new JsonException();
                    }
                }
                catch (JsonException)
                {
                    Error = App.Text("ExternalCLI.InvalidOutput");
                }
            }

            private static string GetString(JsonElement element, string name) =>
                element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
                value.ValueKind == JsonValueKind.String ? value.GetString() : null;

            private bool _lastThinking;
        }

        private static async Task<FileStream> LockAsync(string path, CancellationToken cancellation)
        {
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    return File.Open(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                }
                catch (IOException e) when ((e.HResult & 0xffff) is 11 or 32 or 33)
                {
                    await Task.Delay(100, cancellation).ConfigureAwait(false);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    return null;
                }
            }
        }

        private static string LoadSession(string path)
        {
            try
            {
                if (new FileInfo(path).Length > 2048)
                    return null;
                var id = File.ReadAllText(path, Encoding.UTF8);
                return IsValidSession(id) ? id : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static void SaveSession(string path, string session)
        {
            try
            {
                // The corresponding lock is held by the caller.
                if (session == null)
                {
                    File.Delete(path);
                    return;
                }

                var temporary = path + ".tmp";
                File.WriteAllText(temporary, session);
                File.Move(temporary, path, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Session persistence is optional.
            }
        }

        private static bool IsValidSession(string session) =>
            session is { Length: > 0 and <= 512 } && !session.Any(char.IsControl);

        private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

        private sealed class SessionExpiredException : Exception;
        private readonly record struct Response(string Text, string Session);

        private const string PromptFileToken = "{promptFile}";
        private const string SessionToken = "{sessionId}";
        private const int MaxOutputChars = 4 * 1024 * 1024;

        private readonly string _executable;
        private readonly string[] _arguments;
        private readonly string[] _resumeArguments;
        private readonly bool _usePromptFile;
        private readonly bool _structured;
        private readonly bool _resumeGeneration;
        private readonly string _additionalPrompt;
        private readonly string _key;
    }
}
