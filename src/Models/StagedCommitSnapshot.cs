using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Models
{
    internal readonly record struct StagedCommitSnapshot(string Head, string Branch, string Index, int FileCount)
    {
        public static async Task<StagedCommitSnapshot> ReadAsync(string repo, CancellationToken cancellation)
        {
            var head = await new Commands.WorkflowGitRead(repo, "rev-parse --verify HEAD").ReadAsync(256, cancellation).ConfigureAwait(false);
            var branch = await new Commands.WorkflowGitRead(repo, "symbolic-ref --quiet --short HEAD").ReadAsync(2048, cancellation).ConfigureAwait(false);
            var staged = await new Commands.WorkflowGitRead(repo, "diff --raw --cached --no-ext-diff --no-textconv --no-renames --abbrev=64 -z --")
                .ReadAsync(4 * 1024 * 1024, cancellation).ConfigureAwait(false);

            var entries = staged.Text.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < entries.Length; i += 2)
            {
                if (entries[i].EndsWith(" U", StringComparison.Ordinal))
                    throw new InvalidOperationException(App.Text("BranchWorkflow.NotReady"));
            }

            var index = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(staged.Text)));
            return new(head.Text.Trim(), branch.Text.TrimEnd('\r', '\n'), index, entries.Length / 2);
        }
    }
}
