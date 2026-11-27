using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.ViewModels
{
    public record DiffAnalysisSnapshot(string Scope, string Path, string OriginalPath, string Source, string Patch,
        bool Truncated, bool HasNonText = false, bool HasChangesInScope = true)
    {
        internal const int MaxDiffChars = 200 * 1024;

        public static DiffAnalysisSnapshot Create(TextDiffContext context, int start, int end, bool hasSelection, bool isOldSide)
        {
            var lines = context.Data.Lines;
            var side = context is TwoSideTextDiff twoSide ? (isOldSide ? twoSide.Old : twoSide.New) : lines;
            if (start < 0 || start >= side.Count || end < start || lines.Count == 0)
                return null;

            end = Math.Min(end, side.Count - 1);
            if (context is TwoSideTextDiff split)
            {
                if (hasSelection)
                {
                    var selected = new HashSet<Models.TextDiffLine>();
                    for (var i = start; i <= end; i++)
                    {
                        selected.Add(split.Old[i]);
                        selected.Add(split.New[i]);
                    }

                    var excerpt = lines.Where(selected.Contains).ToList();
                    return HasChanges(excerpt, 0, excerpt.Count - 1)
                        ? Format(context.Option, excerpt, 0, excerpt.Count - 1, MaxDiffChars) : null;
                }

                if (side[start].Type == Models.TextDiffLineType.None)
                {
                    var opposite = isOldSide ? split.New : split.Old;
                    start = end = lines.IndexOf(opposite[start]);
                }
                else
                {
                    split.GetCombinedRangeForSingleSide(ref start, ref end, isOldSide);
                }
            }

            if (start < 0 || end < start || end >= lines.Count)
                return null;

            if (!hasSelection)
            {
                var (blockStart, blockEnd) = IsChange(lines[start])
                    ? context.FindRangeByIndex(lines, start)
                    : (-1, -1);
                if (blockStart >= 0)
                {
                    start = blockStart;
                    end = lines[blockEnd].Type == Models.TextDiffLineType.Indicator ? blockEnd - 1 : blockEnd;
                }
                else
                {
                    while (start > 0 && lines[start].Type != Models.TextDiffLineType.Indicator)
                        start--;
                    while (end + 1 < lines.Count && lines[end + 1].Type != Models.TextDiffLineType.Indicator)
                        end++;
                }
            }
            if (!HasChanges(lines, start, end))
                return null;

            return Format(context.Option, lines, start, end, MaxDiffChars);
        }

        internal static async Task<DiffAnalysisSnapshot> ReadCommitAsync(
            string repo, Models.Commit commit, CancellationToken cancellation)
        {
            var parent = commit.FirstParentToCompare;
            var diff = await new Commands.GetAIDiff(repo, $"{parent} {commit.SHA} --")
                .ReadAsync(MaxDiffChars, cancellation).ConfigureAwait(false);
            return diff.Text.Length == 0 ? null : new(string.Empty, $"Commit {commit.SHA}", string.Empty,
                $"{parent} → {commit.SHA}", diff.Text, diff.Truncated, HasNonTextChanges(diff.Text));
        }

        public static async Task<DiffAnalysisSnapshot> ReadSelectionAsync(
            string repo, Models.DiffOption[] options, CancellationToken cancellation)
        {
            var patch = new StringBuilder();
            var truncated = false;
            var nonText = false;
            var hasChanges = false;
            for (var i = 0; i < options.Length; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                var option = options[i];
                var path = DisplayPath(option.Path, option.OrgPath);
                var heading = $"\n--- {path} ({DescribeSource(option)}) ---\n";
                var remaining = MaxDiffChars - patch.Length - heading.Length;
                if (remaining <= 0)
                {
                    truncated = true;
                    break;
                }

                patch.Append(heading);
                // Leave room for samples from the remaining files when this file has a large diff.
                var budget = remaining / (options.Length - i);
                if (budget < 2)
                {
                    truncated = true;
                    continue;
                }

                var diff = await ReadFileAsync(repo, option, budget, cancellation).ConfigureAwait(false);
                truncated |= diff.Truncated;
                nonText |= diff.HasNonText;
                if (!diff.HasChangesInScope)
                {
                    const string note = "[No text diff available]\n";
                    patch.Append(note.AsSpan(0, Math.Min(budget, note.Length)));
                    truncated |= budget < note.Length;
                }
                else
                {
                    hasChanges = true;
                    patch.Append(diff.Patch);
                    if (diff.Patch[^1] != '\n' && patch.Length < MaxDiffChars)
                        patch.Append('\n');
                }
            }

            var subject = $"{options.Length} selected files";
            return new(string.Empty, subject, string.Empty, "See the source next to each file below", patch.ToString(), truncated, nonText, hasChanges);
        }

        internal static async Task<DiffAnalysisSnapshot> ReadFileAsync(string repo, Models.DiffOption option, int limit, CancellationToken cancellation)
        {
            var range = option.ToString();
            var diff = await new Commands.GetAIDiff(repo, range, range.StartsWith("--no-index ", StringComparison.Ordinal))
                .ReadAsync(limit, cancellation).ConfigureAwait(false);
            return new(string.Empty, option.Path, option.OrgPath, DescribeSource(option), diff.Text, diff.Truncated,
                HasNonTextChanges(diff.Text), diff.Text.Length > 0);
        }

        private static bool HasNonTextChanges(string diff) =>
            diff.Split("\ndiff --git ", StringSplitOptions.None)
                .Any(static file => !file.Contains("\n@@ ", StringComparison.Ordinal) || file.Contains("\nBinary files ", StringComparison.Ordinal));

        public static string DisplayPath(string path, string originalPath) =>
            string.IsNullOrEmpty(originalPath) || originalPath == "/dev/null" ? path : $"{originalPath} → {path}";

        private static DiffAnalysisSnapshot Format(
            Models.DiffOption option, List<Models.TextDiffLine> lines, int start, int end, int limit)
        {
            var fromOld = 0;
            var toOld = 0;
            var fromNew = 0;
            var toNew = 0;
            var patch = new StringBuilder();
            var truncated = false;
            for (var i = start; i <= end; i++)
            {
                var line = lines[i];
                if (line.OldLineNumber > 0)
                {
                    fromOld = fromOld == 0 ? line.OldLineNumber : fromOld;
                    toOld = line.OldLineNumber;
                }

                if (line.NewLineNumber > 0)
                {
                    fromNew = fromNew == 0 ? line.NewLineNumber : fromNew;
                    toNew = line.NewLineNumber;
                }

                if (truncated)
                    continue;

                var remaining = limit - patch.Length;
                if (remaining < 2)
                {
                    truncated = true;
                    continue;
                }

                if (line.Type != Models.TextDiffLineType.Indicator)
                {
                    patch.Append(line.Type switch
                    {
                        Models.TextDiffLineType.Added => '+',
                        Models.TextDiffLineType.Deleted => '-',
                        _ => ' ',
                    });
                }

                var count = Math.Min(line.Content.Length, limit - patch.Length - 1);
                if (count > 0 && count < line.Content.Length && char.IsHighSurrogate(line.Content[count - 1]))
                    count--;

                patch.Append(line.Content.AsSpan(0, count)).Append('\n');
                truncated = count < line.Content.Length;
                if (line.NoNewLineEndOfFile && !truncated)
                {
                    const string marker = "\\ No newline at end of file\n";
                    if (marker.Length <= limit - patch.Length)
                        patch.Append(marker);
                    else
                        truncated = true;
                }
            }

            var scope = $"-{FormatRange(fromOld, toOld)} +{FormatRange(fromNew, toNew)}";
            return new(scope, option.Path, option.OrgPath, DescribeSource(option), patch.ToString(), truncated);
        }

        private static string DescribeSource(Models.DiffOption option)
        {
            if (option.IsLocalChange)
                return option.IsUnstaged ? "index → worktree (unstaged)" : "base revision → index (staged)";

            var from = option.Revisions[0] == "-R" ? "worktree" : option.Revisions[0];
            var to = string.IsNullOrEmpty(option.Revisions[1]) ? "worktree" : option.Revisions[1];
            return $"{from} → {to}";
        }

        private static bool HasChanges(List<Models.TextDiffLine> lines, int start, int end)
        {
            for (var i = start; i <= end; i++)
            {
                if (IsChange(lines[i]))
                    return true;
            }

            return false;
        }

        private static bool IsChange(Models.TextDiffLine line) =>
            line.Type is Models.TextDiffLineType.Added or Models.TextDiffLineType.Deleted;

        private static string FormatRange(int start, int end) => start == end ? $"{start}" : $"{start}–{end}";
    }
}
