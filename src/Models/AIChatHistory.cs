using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SourceGit.Models
{
    public enum AIChatContextSource
    {
        Staged,
        Unstaged,
        Revision,
        Commit,
    }

    public sealed record AIChatAttachment(string Title, AIChatContextSource Origin, ViewModels.DiffAnalysisSnapshot Snapshot)
    {
        public string Label => string.IsNullOrEmpty(Snapshot.Scope) ? Title : $"{Title} · {Snapshot.Scope}";
        public string SourceLabel => App.Text("AIChat.Source." + Origin);
        public string Description => ViewModels.DiffAnalysisSnapshot.DisplayPath(Snapshot.Path, Snapshot.OriginalPath);
    }

    public sealed record AIChatTurn(string Question, string Answer, AIChatAttachment[] Context);

    public sealed class AIChatThread
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid ProfileId { get; set; }
        public string Model { get; set; } = string.Empty;
        public string Effort { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public DateTimeOffset LastOpened { get; set; }
        public string Draft { get; set; } = string.Empty;
        public List<AIChatAttachment> Pending { get; set; } = [];
        public List<AIChatTurn> Turns { get; set; } = [];
        public string DisplayTitle => string.IsNullOrEmpty(Title) ? App.Text("AIChat.New") : Title;
        public string DisplayDate => LastOpened.LocalDateTime.ToString("g");
    }

    internal sealed class AIChatHistory(string worktree)
    {
        public static string NormalizePath(string worktree)
        {
            var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(worktree));
            return OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
        }

        public List<AIChatThread> Load(out bool hasErrors)
        {
            var chats = new List<AIChatThread>();
            hasErrors = false;
            try
            {
                if (!Directory.Exists(_folder))
                    return chats;

                foreach (var file in Directory.EnumerateFiles(_folder, "*.json"))
                {
                    try
                    {
                        using var stream = File.OpenRead(file);
                        var chat = JsonSerializer.Deserialize(stream, JsonCodeGen.Default.AIChatThread);
                        if (chat != null && chat.Id != Guid.Empty && chat.Draft != null && chat.Turns != null && chat.Pending != null &&
                            chat.Pending.TrueForAll(IsValidAttachment) && chat.Turns.TrueForAll(IsValidTurn) &&
                            Path.GetFileNameWithoutExtension(file) == chat.Id.ToString("N"))
                            chats.Add(chat);
                        else
                            hasErrors = true;
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
                    {
                        hasErrors = true;
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                hasErrors = true;
            }
            return chats;
        }

        public bool Save(AIChatThread chat)
        {
            var file = Path.Combine(_folder, chat.Id.ToString("N") + ".json");
            var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                FileStreamOptions options = new() { Access = FileAccess.Write, Mode = FileMode.CreateNew };
                if (OperatingSystem.IsWindows())
                {
                    Directory.CreateDirectory(_folder);
                }
                else
                {
                    Directory.CreateDirectory(_folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                }

                using (var stream = new FileStream(temp, options))
                    JsonSerializer.Serialize(stream, chat, JsonCodeGen.Default.AIChatThread);
                File.Move(temp, file, true);
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
                return false;
            }
            finally
            {
                try
                {
                    File.Delete(temp);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // A leftover temporary file is ignored by the history reader.
                }
            }
        }

        private static bool IsValidTurn(AIChatTurn turn) => turn is { Question: not null, Answer: not null, Context: not null } &&
            Array.TrueForAll(turn.Context, IsValidAttachment);

        private static bool IsValidAttachment(AIChatAttachment item) => item is { Title: not null, Snapshot: { Path: not null, Patch: not null } };

        private readonly string _folder = Path.Combine(Native.OS.BasicDirectories.ConfigDir, "ai-chat",
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(NormalizePath(worktree)))));
    }
}
