using System;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    internal sealed class GetAIDiff : Command
    {
        public GetAIDiff(string repo, string range, bool noIndex = false)
        {
            WorkingDirectory = repo;
            Editor = EditorType.None;
            Args = $"--literal-pathspecs diff --no-color --no-ext-diff --no-textconv --unified=3 {range}";
            _noIndex = noIndex;
        }

        public async Task<ExternalProcess.Output> ReadAsync(int limit, CancellationToken cancellation)
        {
            var result = await ExternalProcess.RunAsync(CreateGitStartInfo(true), null, limit, 60, cancellation).ConfigureAwait(false);
            if (result.ExitCode != 0 && !(_noIndex && result.ExitCode == 1 && (result.StdOut.Text.Length > 0 || result.StdErr.Length == 0)))
                throw new InvalidOperationException(App.Text("ExternalCLI.DiffError", ExternalProcess.FormatError(result)));
            return result.StdOut;
        }

        private readonly bool _noIndex;
    }
}
