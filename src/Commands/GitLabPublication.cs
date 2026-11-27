using System;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    internal sealed class GitLabPublication
    {
        public static async Task<GitLabPublication> PrepareAsync(Models.ReviewConnection connection, Models.PullRequestPublication request,
            CancellationToken cancellation)
        {
            var server = Models.ReviewCredentials.ParseServer(connection.Server);
            var source = Models.ReviewCredentials.Project(server, request.Source.Url);
            var target = Models.ReviewCredentials.Project(server, request.Target.Url);
            if (source == null || target == null)
                throw new InvalidOperationException(App.Text("PublishReview.SameInstance"));

            var token = Models.ReviewCredentials.Read(connection);
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException(App.Text("PublishReview.TokenRequired"));

            var publisher = new GitLabPublication(server, token);
            publisher._sourceId = await publisher.ReadProjectIdAsync(source, cancellation).ConfigureAwait(false);
            publisher._targetId = source == target ? publisher._sourceId :
                await publisher.ReadProjectIdAsync(target, cancellation).ConfigureAwait(false);
            if (publisher._sourceId == publisher._targetId && request.Source.Branch == request.Target.Branch)
                throw new InvalidOperationException(App.Text("PublishReview.SameBranch"));
            return publisher;
        }

        public async Task<Models.PullRequestPublicationResult> RunAsync(Models.PullRequestPublication request, CancellationToken cancellation)
        {
            if (request.Operation == "find")
            {
                var sourceBranch = Uri.EscapeDataString(request.Source.Branch);
                var targetBranch = Uri.EscapeDataString(request.Target.Branch);
                for (var page = 1; page <= 50; page++)
                {
                    var path = $"projects/{_targetId}/merge_requests?state=opened&scope=all&source_branch={sourceBranch}&target_branch={targetBranch}&per_page=100&page={page}";
                    using var result = await _api.SendAsync(HttpMethod.Get, path, null, cancellation).ConfigureAwait(false);
                    foreach (var candidate in result.RootElement.EnumerateArray())
                    {
                        if (candidate.GetProperty("source_project_id").GetInt64() == _sourceId &&
                            candidate.GetProperty("target_project_id").GetInt64() == _targetId &&
                            candidate.GetProperty("source_branch").GetString() == request.Source.Branch &&
                            candidate.GetProperty("target_branch").GetString() == request.Target.Branch)
                            return new("existing", ReviewRestClient.ReadLink(candidate, "web_url"));
                    }
                    if (result.RootElement.GetArrayLength() < 100)
                        return new("missing", null);
                }
                throw new InvalidOperationException(App.Text("PublishReview.TooManyResults"));
            }

            var body = new JsonObject()
            {
                ["source_branch"] = request.Source.Branch,
                ["target_branch"] = request.Target.Branch,
                ["target_project_id"] = _targetId,
                ["title"] = request.Title,
                ["description"] = request.Description,
            };
            using var created = await _api.SendAsync(HttpMethod.Post, $"projects/{_sourceId}/merge_requests", body, cancellation).ConfigureAwait(false);
            return new("created", ReviewRestClient.ReadLink(created.RootElement, "web_url"));
        }

        private GitLabPublication(Uri server, string token)
        {
            _api = new ReviewRestClient(new Uri(server, "/api/v4/"), token, false);
        }

        private async Task<long> ReadProjectIdAsync(string project, CancellationToken cancellation)
        {
            using var result = await _api.SendAsync(HttpMethod.Get, "projects/" + Uri.EscapeDataString(project), null, cancellation)
                .ConfigureAwait(false);
            return result.RootElement.GetProperty("id").GetInt64();
        }

        private readonly ReviewRestClient _api;
        private long _sourceId;
        private long _targetId;
    }
}
