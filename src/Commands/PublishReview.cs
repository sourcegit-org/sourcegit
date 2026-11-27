using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    internal static class PublishReview
    {
        internal sealed record Branch(string Name, string Head);
        internal sealed record RemoteBranches(string Default, List<Branch> Branches);

        public static async Task<string> GetAddressAsync(string repo, Models.Remote remote, bool push, CancellationToken cancellation)
        {
            var result = await new WorkflowGitRead(repo, $"remote get-url {(push ? "--push " : "")}--all -- {remote.Name.Quoted()}")
                .ReadAsync(8192, cancellation).ConfigureAwait(false);
            var addresses = result.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            if (addresses.Length != 1)
                throw new InvalidOperationException(App.Text("PublishReview.MultipleUrls", remote.Name));
            return addresses[0];
        }

        public static Task<RemoteBranches> GetBranchesAsync(string repo, Models.Remote remote, string address, CancellationToken cancellation) =>
            ReadRemoteAsync(repo, remote, address, "HEAD refs/heads/*", cancellation);

        public static async Task<string> GetTipAsync(string repo, Models.Remote remote, string address, string branch, CancellationToken cancellation)
        {
            var branches = await ReadRemoteAsync(repo, remote, address, $"{(Heads + branch).Quoted()}", cancellation).ConfigureAwait(false);
            return branches.Branches.Find(candidate => candidate.Name.Equals(branch, StringComparison.Ordinal))?.Head;
        }

        public static async Task<string> GetLocalTipAsync(string repo, string branch, CancellationToken cancellation)
        {
            var output = await new WorkflowGitRead(repo, $"rev-parse --verify {(Heads + branch).Quoted()}")
                .ReadAsync(256, cancellation).ConfigureAwait(false);
            return output.Text.Trim();
        }

        public static async Task<Models.PullRequestPublicationResult> PublishAsync(string repo, Models.Remote pushRemote,
            Models.Remote targetRemote, Models.PullRequestPublisher publisher, Models.PullRequestPublication request,
            Models.ICommandLog log, Action<string> progress, CancellationToken cancellation)
        {
            var source = request.Source;
            var target = request.Target;
            var push = await GetAddressAsync(repo, pushRemote, true, cancellation).ConfigureAwait(false);
            var destination = await GetAddressAsync(repo, targetRemote, false, cancellation).ConfigureAwait(false);
            if (SanitizeAddress(push) != source.Url || SanitizeAddress(destination) != target.Url ||
                await GetLocalTipAsync(repo, source.Branch, cancellation).ConfigureAwait(false) != source.Commit ||
                await GetTipAsync(repo, targetRemote, destination, target.Branch, cancellation).ConfigureAwait(false) != target.Commit)
                throw new InvalidOperationException(App.Text("PublishReview.Stale"));

            if (source.Branch == target.Branch && ProjectAddress(source.Url) == ProjectAddress(target.Url))
                throw new InvalidOperationException(App.Text("PublishReview.SameBranch"));

            Func<Models.PullRequestPublication, CancellationToken, Task<Models.PullRequestPublicationResult>> run;
            string publisherName;
            var connection = Models.ReviewCredentials.Find(target.Url, ViewModels.Preferences.Instance.ReviewConnections);
            if (connection != null)
            {
                if (connection.IsGitHub)
                    run = (await GitHubPublication.PrepareAsync(connection, request, cancellation).ConfigureAwait(false)).RunAsync;
                else
                    run = (await GitLabPublication.PrepareAsync(connection, request, cancellation).ConfigureAwait(false)).RunAsync;
                publisherName = connection.Label;
            }
            else if (publisher != null)
            {
                ExternalProcess.CreateStartInfo(publisher.Executable, repo);
                run = (data, token) => RunPublisherAsync(repo, publisher, data, token);
                publisherName = publisher.Name;
            }
            else
            {
                throw new InvalidOperationException(App.Text("PublishReview.TokenRequired"));
            }
            var remoteTip = await GetTipAsync(repo, pushRemote, push, source.Branch, cancellation).ConfigureAwait(false);
            if (remoteTip != source.Commit)
            {
                progress?.Invoke(App.Text("PublishReview.Pushing", source.Branch, pushRemote.Name));
                var command = new Push(repo, new Models.Commit() { SHA = source.Commit }, pushRemote,
                    new Models.Branch() { Name = source.Branch }, false).Use(log);
                if (!await command.ExecAsync().ConfigureAwait(false))
                    throw new InvalidOperationException(App.Text("PublishReview.PushFailed", pushRemote.Name));

                if (await GetTipAsync(repo, pushRemote, push, source.Branch, cancellation).ConfigureAwait(false) != source.Commit)
                    throw new InvalidOperationException(App.Text("PublishReview.Stale"));
            }

            progress?.Invoke(App.Text("PublishReview.Finding", publisherName));
            var existing = await run(request with { Operation = "find" }, cancellation).ConfigureAwait(false);
            if (existing.Status == "existing")
                return existing;

            progress?.Invoke(App.Text("PublishReview.Publishing", publisherName));
            try
            {
                return await run(request with { Operation = "create" }, cancellation).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                throw new InvalidOperationException(App.Text("PublishReview.Unconfirmed", e.Message), e);
            }
        }

        public static async Task<Models.PullRequestPublicationResult> RunPublisherAsync(string repo, Models.PullRequestPublisher publisher,
            Models.PullRequestPublication request, CancellationToken cancellation)
        {
            var start = ExternalProcess.CreateStartInfo(publisher.Executable, repo);
            foreach (var argument in AI.ExternalCliProfile.ReadArguments(publisher.Arguments))
                start.ArgumentList.Add(argument);

            var input = JsonSerializer.Serialize(request, JsonCodeGen.Default.PullRequestPublication);
            var result = await ExternalProcess.RunAsync(start, input, 32 * 1024, Math.Clamp(publisher.TimeoutSeconds, 1, 1800), cancellation)
                .ConfigureAwait(false);
            if (result.ExitCode != 0)
                throw new InvalidOperationException(ExternalProcess.FormatError(result));
            if (!result.StdOut.Truncated)
            {
                try
                {
                    var response = JsonSerializer.Deserialize(result.StdOut.Text, JsonCodeGen.Default.PullRequestPublicationResult);
                    if (request.Operation == "find" && response?.Status == "missing")
                        return response;
                    if ((response?.Status == "existing" || (request.Operation == "create" && response?.Status == "created")) &&
                        Uri.TryCreate(response.Url, UriKind.Absolute, out var url) &&
                        url.Scheme is "http" or "https" && string.IsNullOrEmpty(url.UserInfo))
                        return response;
                }
                catch (JsonException)
                {
                    // A malformed result does not confirm the remote operation.
                }
            }

            throw new InvalidOperationException(App.Text("PublishReview.InvalidResult"));
        }

        public static string SanitizeAddress(string address)
        {
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "ssh" or "git"))
                return address;
            var clean = new UriBuilder(uri) { UserName = "", Password = "", Query = "", Fragment = "" };
            return clean.Uri.AbsoluteUri;
        }

        private static string ProjectAddress(string address)
        {
            var project = address.TrimEnd('/');
            if (Uri.TryCreate(address, UriKind.Absolute, out var uri) && !uri.IsFile)
                project = uri.Host.ToLowerInvariant() + uri.AbsolutePath.TrimEnd('/');
            else if (address.IndexOf('@') is var at && at >= 0 && address.IndexOf(':', at) is var colon && colon > at)
                project = address[(at + 1)..colon].ToLowerInvariant() + "/" + address[(colon + 1)..].Trim('/');
            return project.EndsWith(".git", StringComparison.Ordinal) ? project[..^4] : project;
        }

        private static async Task<RemoteBranches> ReadRemoteAsync(string repo, Models.Remote remote,
            string address, string patterns, CancellationToken cancellation)
        {
            var command = new WorkflowGitRead(repo, $"ls-remote --symref -- {address.Quoted()} {patterns}", remote.PrivateSSHKey);
            string text;
            try
            {
                text = (await command.ReadAsync(2 * 1024 * 1024, cancellation).ConfigureAwait(false)).Text;
            }
            catch (InvalidOperationException)
            {
                // Git errors may contain the authenticated URL.
                throw new InvalidOperationException(App.Text("PublishReview.RemoteReadFailed", remote.Name));
            }

            var branches = new List<Branch>();
            string defaultBranch = null;
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var separator = line.IndexOf('\t');
                if (separator < 0)
                    continue;

                var value = line[..separator];
                var reference = line[(separator + 1)..];
                if (reference == "HEAD" && value.StartsWith("ref: " + Heads, StringComparison.Ordinal))
                    defaultBranch = value[(5 + Heads.Length)..];
                else if (reference.StartsWith(Heads, StringComparison.Ordinal))
                    branches.Add(new Branch(reference[Heads.Length..], value));
            }
            return new RemoteBranches(defaultBranch, branches);
        }

        private const string Heads = "refs/heads/";
    }
}
