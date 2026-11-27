using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.VisualTree;

namespace SourceGit.Views
{
    public partial class DiffAnalysis : ChromelessWindow
    {
        public DiffAnalysis()
        {
            CloseOnESC = true;
            InitializeComponent();
            Conversation.HideRequested += Close;
        }

        internal static bool ActivateOpenWindow(ViewModels.AIChat chat)
        {
            if (!_windows.TryGetValue(chat, out var window))
                return false;
            window.Activate();
            window.Conversation.FocusDraft();
            return true;
        }

        internal static void ShowChatWindow(Window owner, ViewModels.AIChat chat)
        {
            if (ActivateOpenWindow(chat))
                return;

            var window = new DiffAnalysis() { DataContext = chat };
            var screen = owner.Screens.ScreenFromWindow(owner) ?? owner.Screens.Primary;
            if (screen != null)
            {
                window.Width = Math.Clamp(screen.WorkingArea.Width / screen.Scaling - 24, window.MinWidth, window.Width);
                window.Height = Math.Clamp(screen.WorkingArea.Height / screen.Scaling - 24, window.MinHeight, window.Height);
            }

            _windows.Add(chat, window);
            window.Show(owner);
            window.Activate();
            window.Conversation.FocusDraft();
        }

        protected override void OnClosed(EventArgs e)
        {
            if (DataContext is ViewModels.AIChat chat)
            {
                _windows.Remove(chat);
                chat.FlushDraft();
            }
            base.OnClosed(e);
        }

        internal static MenuItem CreateLineMenu(Control anchor, string path, Models.DiffOption option, ViewModels.DiffAnalysisSnapshot snapshot) =>
            CreateMenu(anchor, path, _ => Task.FromResult<Models.AIChatAttachment[]>([FromFile(option, snapshot)]));

        internal static MenuItem CreateRevisionTextMenu(Control anchor, string path, string revision, string file, string content, string scope) =>
            CreateMenu(anchor, path, _ => Task.FromResult<Models.AIChatAttachment[]>([FromRevision(revision, file, content, scope)]));

        internal static void AddRevisionFileMenu(ContextMenu menu, Control anchor, string path, string revision, Models.Object file)
        {
            AddAnalysisMenu(menu, CreateMenu(anchor, path, async cancellation =>
            {
                var blob = await new Commands.WorkflowGitRead(path, $"cat-file blob {file.SHA}")
                    .ReadAsync(ViewModels.DiffAnalysisSnapshot.MaxDiffChars, cancellation, allowTruncated: true);
                return [FromRevision(revision, file.Path, blob.Text, string.Empty, blob.Truncated)];
            }));
        }

        internal static void AddSelectionMenu(ContextMenu menu, Control anchor, string path,
            List<Models.Change> changes, Func<Models.Change, Models.DiffOption> createOption)
        {
            if (menu == null || changes.Count == 0)
                return;

            var options = changes.DistinctBy(static change => change.Path, StringComparer.Ordinal).Select(createOption).ToArray();
            AddAnalysisMenu(menu, CreateMenu(anchor, path, cancellation => CaptureFilesAsync(path, options, cancellation)));
        }

        internal static void AddCommitMenu(ContextMenu menu, Control anchor, string path, Models.Commit commit)
        {
            AddAnalysisMenu(menu, CreateMenu(anchor, path, async cancellation =>
            {
                var snapshot = await ViewModels.DiffAnalysisSnapshot.ReadCommitAsync(path, commit, cancellation);
                var id = commit.SHA[..Math.Min(10, commit.SHA.Length)];
                return snapshot == null ? [] : [new($"{id} · {commit.Subject}", Models.AIChatContextSource.Commit, snapshot)];
            }));
        }

        private static MenuItem CreateMenu(Control anchor, string path, Func<CancellationToken, Task<Models.AIChatAttachment[]>> capture)
        {
            if (TopLevel.GetTopLevel(anchor) is not Window owner)
                return null;

            MenuItem CreateAction(string label, bool analyze)
            {
                var action = new MenuItem() { Header = App.Text(label) };
                action.Click += async (_, e) =>
                {
                    e.Handled = true;
                    var host = anchor.FindAncestorOfType<Repository>();
                    var repo = host?.DataContext as ViewModels.Repository;
                    var matches = repo != null && Models.AIChatHistory.NormalizePath(repo.FullPath) == Models.AIChatHistory.NormalizePath(path);
                    var chat = matches ? repo.Chat : ViewModels.AIChat.ForWorktree(path);
                    if (matches)
                        host.ShowAIChat(chat);
                    else
                    {
                        chat.RefreshProfiles();
                        ShowChatWindow(owner, chat);
                    }

                    await chat.AttachAsync(capture, analyze);
                };
                return action;
            }

            var menu = anchor.CreateAIMenu(CreateAction("AIChat.Attach", false));
            menu.Items.Add(CreateAction("DiffAnalysis", true));
            return menu;
        }

        private static async Task<Models.AIChatAttachment[]> CaptureFilesAsync(string path, Models.DiffOption[] options, CancellationToken cancellation)
        {
            var attachments = new List<Models.AIChatAttachment>();
            var budget = Math.Max(2, ViewModels.DiffAnalysisSnapshot.MaxDiffChars / options.Length);
            foreach (var option in options)
            {
                cancellation.ThrowIfCancellationRequested();
                var snapshot = await ViewModels.DiffAnalysisSnapshot.ReadFileAsync(path, option, budget, cancellation).ConfigureAwait(false);
                if (snapshot.HasChangesInScope)
                    attachments.Add(FromFile(option, snapshot));
            }
            return [.. attachments];
        }

        private static Models.AIChatAttachment FromFile(Models.DiffOption option, ViewModels.DiffAnalysisSnapshot snapshot)
        {
            var origin = option.IsLocalChange
                ? option.IsUnstaged ? Models.AIChatContextSource.Unstaged : Models.AIChatContextSource.Staged
                : Models.AIChatContextSource.Revision;
            return new(Path.GetFileName(snapshot.Path), origin, snapshot);
        }

        private static Models.AIChatAttachment FromRevision(string revision, string file, string content, string scope, bool truncated = false)
        {
            var binary = content.Contains('\0');
            var length = Math.Min(content.Length, ViewModels.DiffAnalysisSnapshot.MaxDiffChars);
            if (length < content.Length && char.IsHighSurrogate(content[length - 1]))
                length--;
            var snapshot = new ViewModels.DiffAnalysisSnapshot(scope, file, string.Empty, $"File at {revision}",
                binary ? "[Binary file]" : content[..length], truncated || length < content.Length, binary);
            return new(Path.GetFileName(file), Models.AIChatContextSource.Revision, snapshot);
        }

        private static void AddAnalysisMenu(ContextMenu menu, MenuItem analysis)
        {
            if (menu == null || analysis == null)
                return;
            if (menu.Items.Count > 0)
                menu.Items.Insert(0, new MenuItem() { Header = "-" });
            menu.Items.Insert(0, analysis);
        }

        private static readonly Dictionary<ViewModels.AIChat, DiffAnalysis> _windows = [];
    }
}
