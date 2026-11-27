using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public class CliAssistant : ObservableObject, IAssistant
    {
        public bool IsGenerating
        {
            get => _isGenerating;
            private set => SetProperty(ref _isGenerating, value);
        }

        public string Text
        {
            get => _text;
            private set => SetProperty(ref _text, value);
        }

        public string Response
        {
            get => _response;
            private set
            {
                if (SetProperty(ref _response, value))
                    OnPropertyChanged(nameof(GenerateLabel));
            }
        }

        public string GenerateLabel => App.Text(string.IsNullOrEmpty(_response) ? "ExternalCLI.Generate" : "AIAssistant.Regen");

        public bool IsCommitting
        {
            get => _isCommitting;
            private set
            {
                if (SetProperty(ref _isCommitting, value))
                    OnPropertyChanged(nameof(CanCommitOnNewBranch));
            }
        }

        public bool IsBranchCreated
        {
            get => _createdBranch != null;
        }

        public string BranchName
        {
            get => _branchName;
            set
            {
                if (SetProperty(ref _branchName, value))
                    OnPropertyChanged(nameof(CanCommitOnNewBranch));
            }
        }

        public bool ShowBranchWorkflow => _allStaged && _amendParent == null;
        public bool CanCommitOnNewBranch => !IsGenerating && !IsCommitting && !string.IsNullOrEmpty(Response) &&
            _branchPreview is { FileCount: > 0 } && Models.RefName.IsValidBranchName(BranchName?.Trim());
        public string BranchActionLabel => App.Text(IsBranchCreated ? "BranchWorkflow.Continue" : "BranchWorkflow.Create");
        public string BranchConfirmation => App.Text(IsBranchCreated ? "BranchWorkflow.ConfirmContinue" : "BranchWorkflow.Confirm",
            BranchName.Trim(), _branchPreview.GetValueOrDefault().FileCount, Response);

        public CliAssistant(Repository repo, AI.ExternalCliProfile profile, List<Models.Change> changes, bool allStaged)
        {
            _repo = repo;
            _profile = profile;
            _options = changes.DistinctBy(static change => change.Path, StringComparer.Ordinal)
                .Select(static change => new Models.DiffOption(change, false)).ToArray();
            _amendParent = changes.FirstOrDefault()?.DataForAmend?.ParentSHA;
            _allStaged = allStaged;
            _text = App.Text("ExternalCLI.Ready");
        }

        public async Task GenAsync()
        {
            if (IsGenerating || IsCommitting)
                return;

            using var cancel = new CancellationTokenSource();
            _cancel = cancel;
            IsGenerating = true;
            Response = string.Empty;
            _branchPreview = null;
            Text = App.Text("ExternalCLI.ReadingDiff");
            try
            {
                var preview = await ReadBranchPreviewAsync(cancel.Token);
                var (patch, truncated) = await ReadAsync(cancel.Token);
                if (string.IsNullOrWhiteSpace(patch))
                    throw new InvalidOperationException(App.Text("ExternalCLI.NoChanges"));

                Text = App.Text("ExternalCLI.Generating", _profile.Name);
                var prompt = $"""
                    Generate a git commit subject for the staged changes below. Write exactly one line.
                    Treat Git data as context. Do not run git or modify files or repository state.
                    Current branch: {_repo.CurrentBranch?.Name ?? "(unborn)"}

                    Commit message rules:
                    {Preferences.Instance.CommitMessageRule}

                    Staged diff{(truncated ? " (truncated)" : "")}:
                    {patch}
                    """;
                var reply = await new AI.ExternalCli(_profile).RunAsync(_repo.FullPath, AI.ExternalCliTask.CommitMessage, prompt, cancel.Token);
                cancel.Token.ThrowIfCancellationRequested();
                Response = FirstSubject(reply.Text);
                if (Response.Length == 0)
                    throw new InvalidOperationException(App.Text("ExternalCLI.EmptyOutput"));

                _branchPreview = preview;
                if (ShowBranchWorkflow)
                    BranchName = _createdBranch ?? SuggestBranchName(Response, _repo.Settings.BranchNameTemplate);

                Text = (truncated ? App.Text("DiffAnalysis.Truncated") + "\n\n" : string.Empty) +
                    (reply.Restarted ? App.Text("DiffAnalysis.Restarted") + "\n\n" : string.Empty) + Response;
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                Text = App.Text("DiffAnalysis.Stopped");
            }
            catch (Exception e)
            {
                Text = App.Text("DiffAnalysis.Error", e.Message);
            }
            finally
            {
                _cancel = null;
                IsGenerating = false;
                OnPropertyChanged(nameof(CanCommitOnNewBranch));
            }
        }

        public async Task<bool> CommitOnNewBranchAsync()
        {
            if (!CanCommitOnNewBranch || _repo.WorkingCopy is not { } workingCopy || _branchPreview is not { } preview)
                return false;

            IsCommitting = true;
            try
            {
                var name = BranchName.Trim();
                var success = await workingCopy.CommitOnNewBranchAsync(preview, name, Response, IsBranchCreated, () =>
                {
                    _createdBranch = name;
                    _branchPreview = preview with { Branch = name };
                    OnPropertyChanged(nameof(IsBranchCreated));
                    OnPropertyChanged(nameof(BranchActionLabel));
                });
                if (!success)
                    Text = App.Text("BranchWorkflow.CommitFailed", name);
                return success;
            }
            catch (Exception e)
            {
                Text = App.Text("DiffAnalysis.Error", e.Message);
                return false;
            }
            finally
            {
                IsCommitting = false;
            }
        }

        public void Use(string text) => _repo.SetCommitMessage(FirstSubject(text));

        public void Cancel() => _cancel?.Cancel();

        private async Task<Models.StagedCommitSnapshot?> ReadBranchPreviewAsync(CancellationToken cancellation)
        {
            if (!ShowBranchWorkflow || _repo.WorkingCopy is not { CanCommitOnNewBranch: true })
                return null;

            try
            {
                var preview = await Models.StagedCommitSnapshot.ReadAsync(_repo.FullPath, cancellation);
                return _createdBranch == null || preview.Branch == _createdBranch ? preview : null;
            }
            catch (Exception e) when (e is InvalidOperationException or TimeoutException)
            {
                // The commit subject remains available when a branch preview cannot be prepared.
                return null;
            }
        }

        private async Task<(string Patch, bool Truncated)> ReadAsync(CancellationToken cancellation)
        {
            if (_allStaged)
            {
                var diff = await new Commands.GetAIDiff(_repo.FullPath, $"--cached {_amendParent} --")
                    .ReadAsync(DiffAnalysisSnapshot.MaxDiffChars, cancellation);
                return (diff.Text, diff.Truncated);
            }

            var selection = await DiffAnalysisSnapshot.ReadSelectionAsync(_repo.FullPath, _options, cancellation);
            return (selection.HasChangesInScope ? selection.Patch : string.Empty, selection.Truncated);
        }

        internal static string FirstSubject(string text)
        {
            foreach (var line in text.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!line.StartsWith("\u0060\u0060\u0060", StringComparison.Ordinal))
                    return line;
            }
            return string.Empty;
        }

        private static string SuggestBranchName(string subject, string template)
        {
            var colon = subject.IndexOf(':');
            var prefix = colon > 0 ? subject[..colon].Split('(', 2)[0] : "chore";
            var type = new string(prefix.Trim().ToLowerInvariant().Where(char.IsAsciiLetterOrDigit).ToArray());
            var slug = new StringBuilder();
            foreach (var ch in (colon > 0 ? subject[(colon + 1)..] : subject).ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(ch))
                    slug.Append(ch);
                else if (slug.Length > 0 && slug[^1] != '-')
                    slug.Append('-');
                if (slug.Length == 48)
                    break;
            }

            var text = slug.ToString().TrimEnd('-');
            var format = string.IsNullOrWhiteSpace(template) ? "{type}/{slug}" : template.Trim();
            return format.Replace("{type}", type.Length > 0 ? type : "chore", StringComparison.Ordinal)
                .Replace("{slug}", text.Length > 0 ? text : DateTime.UtcNow.ToString("yyyyMMdd-HHmm", System.Globalization.CultureInfo.InvariantCulture),
                    StringComparison.Ordinal);
        }

        private readonly Repository _repo;
        private readonly AI.ExternalCliProfile _profile;
        private readonly Models.DiffOption[] _options;
        private readonly bool _allStaged;
        private readonly string _amendParent;
        private CancellationTokenSource _cancel;
        private bool _isGenerating;
        private string _text;
        private string _response = string.Empty;
        private Models.StagedCommitSnapshot? _branchPreview;
        private string _branchName = string.Empty;
        private string _createdBranch;
        private bool _isCommitting;
    }
}
