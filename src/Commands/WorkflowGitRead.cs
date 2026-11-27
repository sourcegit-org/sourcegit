using System;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    internal sealed class WorkflowGitRead : Command
    {
        public WorkflowGitRead(string repo, string args, string sshKey = null)
        {
            WorkingDirectory = repo;
            Args = args;
            Editor = EditorType.None;
            SSHKey = sshKey;
        }

        public async Task<ExternalProcess.Output> ReadAsync(int limit, CancellationToken cancellation, bool allowTruncated = false)
        {
            var output = await ExternalProcess.RunAsync(CreateGitStartInfo(true), null, limit, 60, cancellation).ConfigureAwait(false);
            if (output.ExitCode != 0)
                throw new InvalidOperationException(ExternalProcess.FormatError(output));
            if (!allowTruncated && output.StdOut.Truncated)
                throw new InvalidOperationException(App.Text("ExternalCLI.OutputLimit"));
            return output.StdOut;
        }
    }
}
