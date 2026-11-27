using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    internal sealed class ReviewRestClient(Uri root, string token, bool isGitHub)
    {
        public async Task<JsonDocument> SendAsync(HttpMethod method, string path, JsonObject body, CancellationToken cancellation)
        {
            var url = new Uri(root, path);
            for (var redirects = 0; redirects <= 4; redirects++)
            {
                using var request = new HttpRequestMessage(method, url);
                if (isGitHub)
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    request.Headers.UserAgent.ParseAdd("SourceGit");
                    request.Headers.Accept.ParseAdd("application/vnd.github+json");
                }
                else
                {
                    request.Headers.Add("PRIVATE-TOKEN", token);
                }
                if (body != null)
                    request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

                using var response = await _client.SendAsync(request, cancellation).ConfigureAwait(false);
                if (method == HttpMethod.Get && (int)response.StatusCode is 301 or 302 or 307 or 308 &&
                    response.Headers.Location is { } location && Uri.TryCreate(url, location, out var next) &&
                    next.GetLeftPart(UriPartial.Authority) == root.GetLeftPart(UriPartial.Authority) &&
                    next.AbsolutePath.StartsWith(root.AbsolutePath, StringComparison.Ordinal))
                {
                    url = next;
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                {
                    var message = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                        ? App.Text("PublishReview.AuthFailed") : App.Text("PublishReview.RequestFailed", (int)response.StatusCode);
                    throw new InvalidOperationException(message);
                }
                return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false));
            }
            throw new InvalidOperationException(App.Text("PublishReview.RequestFailed", 302));
        }

        public static string ReadLink(JsonElement item, string field)
        {
            var value = item.GetProperty(field).GetString();
            if (!Uri.TryCreate(value, UriKind.Absolute, out var url) || url.Scheme is not ("https" or "http") || url.UserInfo.Length > 0)
                throw new InvalidOperationException(App.Text("PublishReview.InvalidResult"));
            return url.AbsoluteUri;
        }

        private static readonly HttpClient _client = new(new HttpClientHandler() { AllowAutoRedirect = false, UseCookies = false })
        {
            Timeout = TimeSpan.FromSeconds(45),
            MaxResponseContentBufferSize = 4 * 1024 * 1024,
        };
    }
}
