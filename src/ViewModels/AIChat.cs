using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Avalonia.Threading;

namespace SourceGit.ViewModels
{
    public sealed record AIChatOption(string Value, string Label);

    public sealed class AIChat : AnalysisTranscript
    {
        public AI.ExternalCliProfile[] Profiles { get; private set; } = [];
        public AIChatOption[] ModelOptions { get; private set; } = [];
        public AIChatOption[] EffortOptions { get; private set; } = [];
        public bool HasModelOptions => ModelOptions.Length > 1;
        public bool HasEffortOptions => EffortOptions.Length > 1;
        public bool HasChatOptions => HasModelOptions || HasEffortOptions;
        public bool HasProfiles => Profiles.Length > 0;
        public bool IsBusy => _request != null;
        public bool IsSending => IsBusy && _isSending;
        public bool CanSelectProfile => !IsBusy && (_current.Turns.Count == 0 ||
            !Profiles.Any(profile => profile.Id == _current.ProfileId));
        public bool CanSend => !IsBusy && SelectedProfile != null &&
            (HasPending || _current.Turns.Count > 0 && !string.IsNullOrWhiteSpace(Draft));
        public string Title => _current.DisplayTitle;
        public List<Models.AIChatThread> History => _threads.Where(chat => chat.DisplayTitle.Contains(Filter, StringComparison.CurrentCultureIgnoreCase))
            .OrderByDescending(static chat => chat.LastOpened).ToList();
        public Models.AIChatAttachment[] Pending => IsSending ? [] : [.. _current.Pending];
        public bool HasPending => !IsSending && _current.Pending.Count > 0;
        public string PendingLabel => App.Text("AIChat.Pending", _current.Pending.Count);
        public bool HasPreview => Preview != null;

        public AI.ExternalCliProfile SelectedProfile
        {
            get => _selectedProfile;
            set
            {
                if (CanSelectProfile && value != null && SetProperty(ref _selectedProfile, value))
                {
                    _current.ProfileId = value.Id;
                    _current.Model = string.Empty;
                    _current.Effort = string.Empty;
                    RefreshChatOptions();
                    ScheduleSave();
                    OnPropertyChanged(nameof(CanSend));
                }
            }
        }

        public AIChatOption SelectedModel
        {
            get => FindOption(ModelOptions, _current.Model);
            set
            {
                if (!IsBusy && value != null && ModelOptions.Contains(value) && _current.Model != value.Value)
                {
                    _current.Model = value.Value;
                    ScheduleSave();
                    OnPropertyChanged(nameof(SelectedModel));
                }
            }
        }

        public AIChatOption SelectedEffort
        {
            get => FindOption(EffortOptions, _current.Effort);
            set
            {
                if (!IsBusy && value != null && EffortOptions.Contains(value) && _current.Effort != value.Value)
                {
                    _current.Effort = value.Value;
                    ScheduleSave();
                    OnPropertyChanged(nameof(SelectedEffort));
                }
            }
        }

        public string Draft
        {
            get => IsSending ? string.Empty : _current.Draft;
            set
            {
                if (IsSending || _current.Draft == value)
                    return;
                _current.Draft = value ?? string.Empty;
                ScheduleSave();
                OnPropertyChanged(nameof(Draft));
                OnPropertyChanged(nameof(CanSend));
            }
        }

        public string Filter
        {
            get => _filter;
            set
            {
                if (SetProperty(ref _filter, value ?? string.Empty))
                    OnPropertyChanged(nameof(History));
            }
        }

        public string Status
        {
            get => _status;
            private set => SetProperty(ref _status, value);
        }

        public Models.AIChatAttachment Preview
        {
            get => _preview;
            private set
            {
                if (SetProperty(ref _preview, value))
                    OnPropertyChanged(nameof(HasPreview));
            }
        }

        internal static AIChat ForWorktree(string worktree, Guid preferred = default)
        {
            var key = Models.AIChatHistory.NormalizePath(worktree);
            if (_open.TryGetValue(key, out var reference) && reference.TryGetTarget(out var chat))
                return chat;

            foreach (var dead in _open.Where(static entry => !entry.Value.TryGetTarget(out _)).Select(static entry => entry.Key).ToArray())
                _open.Remove(dead);

            chat = new AIChat(worktree, preferred);
            _open[key] = new WeakReference<AIChat>(chat);
            return chat;
        }

        internal static void FlushOpenChats()
        {
            foreach (var reference in _open.Values)
                if (reference.TryGetTarget(out var chat))
                    chat.FlushDraft();
        }

        private AIChat(string worktree, Guid preferred)
        {
            _repo = worktree;
            _preferred = preferred;
            _store = new Models.AIChatHistory(worktree);
            _threads = _store.Load(out var errors);
            _saveTimer.Tick += (_, _) => FlushDraft();
            Activate(_threads.OrderByDescending(static chat => chat.LastOpened).FirstOrDefault() ?? new(), false);
            if (errors)
                Status = App.Text("AIChat.LoadFailed");
        }

        public void RefreshProfiles()
        {
            if (IsBusy)
                return;

            Profiles = AI.ExternalCliProfile.Available().Where(static profile => profile.CanAnalyze).ToArray();
            _selectedProfile = Array.Find(Profiles, profile => profile.Id == _current.ProfileId);
            if (_selectedProfile == null && _current.Turns.Count == 0)
                _selectedProfile = Array.Find(Profiles, profile => profile.Id == _preferred) ?? Profiles.FirstOrDefault();
            OnPropertyChanged(nameof(Profiles));
            OnPropertyChanged(nameof(HasProfiles));
            OnPropertyChanged(nameof(SelectedProfile));
            RefreshChatOptions();
            UpdateActions();
        }

        private void RefreshChatOptions()
        {
            var profile = SelectedProfile;
            ModelOptions = CreateChoices(profile?.ChatModels, profile?.ChatModelArguments, AI.ExternalCliProfile.ChatModelToken);
            EffortOptions = CreateChoices(profile?.ChatEfforts, profile?.ChatEffortArguments, AI.ExternalCliProfile.ChatEffortToken);
            OnPropertyChanged(nameof(ModelOptions));
            OnPropertyChanged(nameof(EffortOptions));
            OnPropertyChanged(nameof(HasModelOptions));
            OnPropertyChanged(nameof(HasEffortOptions));
            OnPropertyChanged(nameof(HasChatOptions));
            OnPropertyChanged(nameof(SelectedModel));
            OnPropertyChanged(nameof(SelectedEffort));
        }

        private static AIChatOption[] CreateChoices(string values, string arguments, string token) =>
            [new(string.Empty, App.Text("AIChat.ProviderDefault")),
                .. AI.ExternalCliProfile.ReadChatChoices(values, arguments, token).Select(static value => new AIChatOption(value, value))];

        private static AIChatOption FindOption(AIChatOption[] options, string value) =>
            Array.Find(options, option => option.Value == value) ?? options.FirstOrDefault();

        public void NewChat()
        {
            if (!IsBusy)
                Activate(new(), true);
        }

        public void OpenHistory(Models.AIChatThread thread)
        {
            if (!IsBusy && thread != null && _threads.Contains(thread))
                Activate(thread, true);
        }

        public void TogglePreview(Models.AIChatAttachment item) => Preview = Preview == item ? null : item;

        public void Remove(Models.AIChatAttachment item)
        {
            if (IsBusy || !_current.Pending.Remove(item))
                return;
            if (Preview == item)
                Preview = null;
            UpdatePending();
            SaveCurrent();
        }

        internal async Task AttachAsync(Func<CancellationToken, Task<Models.AIChatAttachment[]>> capture, bool analyze)
        {
            if (IsBusy)
            {
                Status = App.Text("AIChat.Busy");
                return;
            }

            using var cancellation = new CancellationTokenSource();
            Begin(cancellation, false, "AIChat.Capturing");
            try
            {
                var items = await capture(cancellation.Token);
                if (items.Length == 0)
                {
                    Status = App.Text("DiffAnalysis.NoChanges");
                    return;
                }

                if (analyze)
                    Activate(new Models.AIChatThread() { Title = items[0].Title, Draft = App.Text("AIChat.DefaultQuestion") }, true);

                var added = 0;
                var skipped = false;
                var budget = DiffAnalysisSnapshot.MaxDiffChars - _current.Pending.Sum(static item => item.Snapshot.Patch.Length);
                foreach (var item in items)
                {
                    if (_current.Pending.Contains(item))
                        continue;
                    if (budget < item.Snapshot.Patch.Length || _current.Pending.Count == 128)
                    {
                        skipped = true;
                        continue;
                    }

                    _current.Pending.Add(item);
                    budget -= item.Snapshot.Patch.Length;
                    added++;
                }

                Status = skipped ? App.Text("AIChat.AttachmentLimit") :
                    added == 0 ? App.Text("AIChat.AlreadyAdded") : App.Text("AIChat.Added", added);
                UpdatePending();
                if (added > 0)
                    SaveCurrent();
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                Status = App.Text("DiffAnalysis.Stopped");
            }
            catch (Exception e)
            {
                Status = App.Text("AIChat.CaptureFailed", e.Message);
            }
            finally
            {
                End();
            }
        }

        public async Task SendAsync()
        {
            if (!CanSend)
                return;

            var profile = SelectedProfile;
            var model = SelectedModel?.Value;
            var effort = SelectedEffort?.Value;
            var text = Draft.Trim();
            var defaultQuestion = App.Text("AIChat.DefaultQuestion");
            var question = text.Length > 0 ? text : defaultQuestion;
            var attachments = Pending;
            var prompt = CreatePrompt(question, defaultQuestion, attachments, out var limited);
            var outgoing = new DiffAnalysisEntry(new("question", null, question), attachments);
            using var cancellation = new CancellationTokenSource();
            Begin(cancellation, true, "AIChat.Running");
            Preview = null;
            UpdateComposer();
            AppendEntry(outgoing);
            try
            {
                if (limited || attachments.Any(static item => item.Snapshot.Truncated))
                    AddNotice(App.Text("DiffAnalysis.Truncated"));
                if (attachments.Any(static item => item.Snapshot.HasNonText))
                    AddNotice(App.Text("DiffAnalysis.NonText"));

                var agent = new AI.ExternalCli(profile, model, effort);
                var reply = await agent.RunAsync(_repo, AI.ExternalCliTask.DiffAnalysis, prompt, cancellation.Token, QueueTrace, _current.Id);
                cancellation.Token.ThrowIfCancellationRequested();
                if (reply.Restarted)
                    AddNotice(App.Text("DiffAnalysis.Restarted"));
                QueueTrace(new("answer", null, reply.Text));
                _current.Turns.Add(new Models.AIChatTurn(question, reply.Text, attachments));
                if (string.IsNullOrEmpty(_current.Title))
                    _current.Title = attachments.FirstOrDefault()?.Title ?? Shorten(question.ReplaceLineEndings(" "), 72);
                _current.ProfileId = profile.Id;
                _current.Pending.Clear();
                _current.Draft = string.Empty;
                Preview = null;
                Status = string.Empty;
                OnPropertyChanged(nameof(Title));
                SaveCurrent();
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                Entries.Remove(outgoing);
                AddNotice(App.Text("DiffAnalysis.Stopped"));
                Status = App.Text("AIChat.DraftKept");
            }
            catch (Exception e)
            {
                Entries.Remove(outgoing);
                AddNotice(App.Text("DiffAnalysis.Error", e.Message));
                Status = App.Text("AIChat.DraftKept");
            }
            finally
            {
                FlushTrace();
                OnPropertyChanged(nameof(HasEntries));
                End();
                UpdateComposer();
            }
        }

        public void Stop() => _request?.Cancel();

        internal void FlushDraft()
        {
            if (_saveTimer.IsEnabled)
                SaveCurrent();
        }

        private string CreatePrompt(string question, string defaultQuestion, Models.AIChatAttachment[] attachments, out bool limited)
        {
            var seen = new HashSet<Models.AIChatAttachment>();
            var patch = new StringBuilder();
            limited = false;
            var budget = DiffAnalysisSnapshot.MaxDiffChars;
            foreach (var item in attachments.Concat(_current.Turns.AsEnumerable().Reverse().SelectMany(static turn => turn.Context)))
            {
                if (!seen.Add(item))
                    continue;
                var snapshot = item.Snapshot;
                var remaining = budget - patch.Length;
                if (remaining <= 0)
                {
                    limited = true;
                    break;
                }

                var heading = $"\n--- {item.Description} | {snapshot.Source} | {snapshot.Scope} ---\n";
                patch.Append(Shorten(heading, remaining));
                remaining = budget - patch.Length;
                if (remaining < snapshot.Patch.Length || snapshot.Truncated)
                    limited = true;
                patch.Append(Shorten(snapshot.Patch, remaining));
            }

            var recent = new StringBuilder();
            for (var i = _current.Turns.Count - 1; i >= 0 && recent.Length < 12 * 1024; i--)
            {
                var turn = _current.Turns[i];
                recent.Insert(0, Shorten($"Question: {turn.Question}\nAnswer: {turn.Answer}\n\n", 12 * 1024 - recent.Length));
            }

            var combined = new DiffAnalysisSnapshot(string.Empty, "Attached changes", string.Empty, "Frozen snapshots", patch.ToString(), limited);
            var initial = _current.Turns.Count == 0 && question == defaultQuestion;
            return AnalysisPrompt.Build(combined, Preferences.Instance.Locale, initial ? null : question, recent.ToString());
        }

        private void Activate(Models.AIChatThread thread, bool flush)
        {
            if (flush)
                FlushDraft();
            _current = thread;
            Preview = null;
            Status = string.Empty;
            ReplaceTranscript(thread.Turns.SelectMany(static turn => new[]
            {
                new DiffAnalysisEntry(new("question", null, turn.Question), turn.Context),
                new DiffAnalysisEntry(new("answer", null, turn.Answer)),
            }));
            RefreshProfiles();
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Draft));
            UpdatePending();
            if (_threads.Contains(thread) && flush)
                SaveCurrent();
        }

        private void Begin(CancellationTokenSource request, bool sending, string statusKey)
        {
            _request = request;
            _isSending = sending;
            Status = App.Text(statusKey);
            UpdateActions();
        }

        private void End()
        {
            _request = null;
            _isSending = false;
            RefreshProfiles();
        }

        private void UpdatePending()
        {
            OnPropertyChanged(nameof(Pending));
            OnPropertyChanged(nameof(PendingLabel));
            OnPropertyChanged(nameof(HasPending));
            OnPropertyChanged(nameof(CanSend));
        }

        private void UpdateComposer()
        {
            OnPropertyChanged(nameof(Draft));
            UpdatePending();
        }

        private void UpdateActions()
        {
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(IsSending));
            OnPropertyChanged(nameof(CanSend));
            OnPropertyChanged(nameof(CanSelectProfile));
        }

        private void ScheduleSave()
        {
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private void SaveCurrent()
        {
            _saveTimer.Stop();
            if (!_threads.Contains(_current))
            {
                if (_current.Turns.Count == 0 && _current.Pending.Count == 0 && string.IsNullOrWhiteSpace(_current.Draft))
                    return;
                _threads.Add(_current);
            }

            _current.LastOpened = DateTimeOffset.UtcNow;
            if (!_store.Save(_current))
                Status = App.Text("AIChat.SaveFailed");
            OnPropertyChanged(nameof(History));
        }

        private static string Shorten(string text, int limit)
        {
            if (text.Length <= limit)
                return text;
            if (limit > 0 && char.IsHighSurrogate(text[limit - 1]))
                limit--;
            return text[..limit];
        }

        private static readonly Dictionary<string, WeakReference<AIChat>> _open = [];
        private readonly string _repo;
        private readonly Guid _preferred;
        private readonly Models.AIChatHistory _store;
        private readonly List<Models.AIChatThread> _threads;
        private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
        private Models.AIChatThread _current;
        private Models.AIChatAttachment _preview;
        private CancellationTokenSource _request;
        private AI.ExternalCliProfile _selectedProfile;
        private bool _isSending;
        private string _status = string.Empty;
        private string _filter = string.Empty;
    }
}
