using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    internal sealed class GitHubPublication(ReviewRestClient api, string source, string target)
    {
        public static async Task<GitHubPublication> PrepareAsync(Models.ReviewConnection connection, Models.PullRequestPublication request,
            CancellationToken cancellation)
        {
            var server = Models.ReviewCredentials.ParseServer(connection.Server);
            var source = Models.ReviewCredentials.Project(server, request.Source.Url);
            var target = Models.ReviewCredentials.Project(server, request.Target.Url);
            if (!IsRepository(source) || !IsRepository(target))
                throw new InvalidOperationException(App.Text("PublishReview.SameInstance"));

            var token = Models.ReviewCredentials.Read(connection);
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException(App.Text("PublishReview.TokenRequired"));

            var root = server.IdnHost == "github.com" && server.IsDefaultPort ? new Uri("https://api.github.com/") :
                new Uri(server, "/api/v3/");
            var api = new ReviewRestClient(root, token, true);
            var canonicalSource = await ResolveAsync(api, source, cancellation).ConfigureAwait(false);
            var canonicalTarget = source.Equals(target, StringComparison.OrdinalIgnoreCase) ? canonicalSource :
                await ResolveAsync(api, target, cancellation).ConfigureAwait(false);
            if (canonicalSource.Equals(canonicalTarget, StringComparison.OrdinalIgnoreCase) && request.Source.Branch == request.Target.Branch)
                throw new InvalidOperationException(App.Text("PublishReview.SameBranch"));
            return new GitHubPublication(api, canonicalSource, canonicalTarget);
        }

        public async Task<Models.PullRequestPublicationResult> RunAsync(Models.PullRequestPublication request, CancellationToken cancellation)
        {
            var fork = !source.Equals(target, StringComparison.OrdinalIgnoreCase);
            var owner = source[..source.IndexOf('/')];
            var head = $"{owner}:{request.Source.Branch}";
            var path = "repos/" + Encode(target) + "/pulls";
            if (request.Operation == "find")
            {
                var query = $"?state=open&head={Uri.EscapeDataString(head)}&base={Uri.EscapeDataString(request.Target.Branch)}&per_page=100";
                for (var page = 1; page <= 50; page++)
                {
                    using var result = await api.SendAsync(HttpMethod.Get, $"{path}{query}&page={page}", null, cancellation).ConfigureAwait(false);
                    foreach (var candidate in result.RootElement.EnumerateArray())
                    {
                        var sourceRef = candidate.GetProperty("head");
                        if (sourceRef.GetProperty("repo") is { ValueKind: JsonValueKind.Object } repo &&
                            string.Equals(repo.GetProperty("full_name").GetString(), source, StringComparison.OrdinalIgnoreCase) &&
                            sourceRef.GetProperty("ref").GetString() == request.Source.Branch &&
                            candidate.GetProperty("base").GetProperty("ref").GetString() == request.Target.Branch)
                            return new("existing", ReviewRestClient.ReadLink(candidate, "html_url"));
                    }
                    if (result.RootElement.GetArrayLength() < 100)
                        return new("missing", null);
                }
                throw new InvalidOperationException(App.Text("PublishReview.TooManyResults"));
            }

            var body = new JsonObject()
            {
                ["head"] = fork ? head : request.Source.Branch,
                ["base"] = request.Target.Branch,
                ["title"] = request.Title,
                ["body"] = request.Description,
            };
            if (fork)
                body["head_repo"] = source[(source.IndexOf('/') + 1)..];
            using var created = await api.SendAsync(HttpMethod.Post, path, body, cancellation).ConfigureAwait(false);
            return new("created", ReviewRestClient.ReadLink(created.RootElement, "html_url"));
        }

        private static async Task<string> ResolveAsync(ReviewRestClient api, string project, CancellationToken cancellation)
        {
            using var result = await api.SendAsync(HttpMethod.Get, "repos/" + Encode(project), null, cancellation).ConfigureAwait(false);
            var name = result.RootElement.GetProperty("full_name").GetString();
            return IsRepository(name) ? name : throw new InvalidOperationException(App.Text("PublishReview.InvalidResult"));
        }

        private static bool IsRepository(string project) =>
            project?.Split('/') is [var owner, var name] && owner.Length > 0 && name.Length > 0;

        private static string Encode(string project) =>
            string.Join('/', Array.ConvertAll(project.Split('/'), Uri.EscapeDataString));
    }
}
