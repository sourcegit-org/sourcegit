using System.Collections.Generic;
using System.Text;

using Avalonia.Collections;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public abstract class AnalysisTranscript : ObservableObject
    {
        public AvaloniaList<DiffAnalysisEntry> Entries { get; } = [];
        public bool HasEntries => Entries.Count > 0;

        protected void ReplaceTranscript(IEnumerable<DiffAnalysisEntry> entries)
        {
            FlushTrace();
            Entries.Clear();
            Entries.AddRange(entries);
            OnPropertyChanged(nameof(HasEntries));
        }

        protected void AppendEntry(DiffAnalysisEntry entry)
        {
            var hadEntries = HasEntries;
            Entries.Add(entry);
            if (!hadEntries)
                OnPropertyChanged(nameof(HasEntries));
        }

        public string GetTranscript()
        {
            FlushTrace();
            var buffer = new StringBuilder();
            foreach (var entry in Entries)
                entry.WriteTo(buffer);
            return buffer.ToString();
        }

        public string GetAnswerMarkdown()
        {
            for (var i = Entries.Count - 1; i >= 0; i--)
            {
                if (Entries[i].IsAnswer)
                    return Entries[i].Markdown;
            }

            return string.Empty;
        }

        protected void AddNotice(string message) => QueueTrace(new("notice", null, message));

        private protected void QueueTrace(AI.ExternalCliEvent entry)
        {
            lock (_traceBuffer)
            {
                _traceBuffer.Add(entry);
                if (_traceBuffer.Count > 1)
                    return;
            }

            Dispatcher.UIThread.Post(FlushTrace, DispatcherPriority.Background);
        }

        protected void FlushTrace()
        {
            AI.ExternalCliEvent[] batch;
            lock (_traceBuffer)
            {
                if (_traceBuffer.Count == 0)
                    return;

                batch = _traceBuffer.ToArray();
                _traceBuffer.Clear();
            }

            var hadEntries = HasEntries;
            for (var i = 0; i < batch.Length; i++)
            {
                var entry = batch[i];
                if (i + 1 < batch.Length && batch[i + 1].ContinuesThinking)
                {
                    var thought = new StringBuilder(entry.Thinking);
                    while (i + 1 < batch.Length && batch[i + 1].ContinuesThinking)
                        thought.Append(batch[++i].Thinking);
                    entry = entry with { Thinking = thought.ToString() };
                }

                if (entry.ContinuesThinking && Entries.Count > 0)
                    Entries[^1].AppendThinking(entry.Thinking);
                else
                    Entries.Add(new DiffAnalysisEntry(entry));
            }

            if (!hadEntries)
                OnPropertyChanged(nameof(HasEntries));
        }

        private readonly List<AI.ExternalCliEvent> _traceBuffer = [];
    }
}
