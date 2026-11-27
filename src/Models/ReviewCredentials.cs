using System;
using System.Collections.Generic;

namespace SourceGit.Models
{
    public sealed record ReviewConnection(string Server, bool IsGitHub)
    {
        public string Label => $"{(IsGitHub ? "GitHub" : "GitLab")} · {Server}";
    }

    internal static class ReviewCredentials
    {
        public static Uri ParseServer(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;
            text = text.Trim();
            if (!text.Contains("://", StringComparison.Ordinal))
                text = "https://" + text;
            return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
                uri.Host.Length > 0 && uri.AbsolutePath == "/" &&
                uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 ? uri : null;
        }

        public static bool Matches(Uri server, string remote)
        {
            var (host, port, _) = ParseRemote(remote);
            return host != null && server.IdnHost.Equals(host, StringComparison.OrdinalIgnoreCase) &&
                (port == null || port == server.Port);
        }

        public static ReviewConnection Find(string remote, IEnumerable<ReviewConnection> configured)
        {
            ReviewConnection result = null;
            foreach (var connection in configured)
            {
                if (connection == null)
                    continue;
                var server = ParseServer(connection.Server);
                if (server != null && Matches(server, remote))
                {
                    if (result != null && result != connection)
                        return null;
                    result = connection;
                }
            }
            return result;
        }

        public static bool LooksLikeSupportedRemote(string remote)
        {
            var (host, _, project) = ParseRemote(remote);
            return project != null && host != null &&
                (host.StartsWith("gitlab", StringComparison.OrdinalIgnoreCase) ||
                 host.StartsWith("github", StringComparison.OrdinalIgnoreCase));
        }

        public static bool SuggestGitHub(string remote) => ParseRemote(remote).Host?.StartsWith("github", StringComparison.OrdinalIgnoreCase) == true;

        public static string SuggestServer(string remote)
        {
            var (host, port, _) = ParseRemote(remote);
            return host == null ? string.Empty : new UriBuilder("https", host, port ?? -1).Uri.GetLeftPart(UriPartial.Authority);
        }

        public static string Project(Uri server, string remote) => Matches(server, remote) ? ParseRemote(remote).Project : null;

        public static bool IsAvailable => Native.OS.Keychain != null;
        public static string Read(ReviewConnection connection) => Native.OS.Keychain?.Read(Key(connection), "SourceGit");
        public static void Store(ReviewConnection connection, string token) => Native.OS.Keychain.Store(Key(connection), "SourceGit", token);
        public static void Remove(ReviewConnection connection) => Native.OS.Keychain?.Erase(Key(connection), "SourceGit");

        private static string Key(ReviewConnection connection) =>
            (connection.IsGitHub ? "SourceGit.GitHub:" : "SourceGit.GitLab:") + connection.Server;

        private static (string Host, int? Port, string Project) ParseRemote(string remote)
        {
            if (string.IsNullOrEmpty(remote))
                return default;

            string host;
            int? port = null;
            string path;
            if (Uri.TryCreate(remote, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "ssh" or "git")
            {
                host = uri.IdnHost;
                if (uri.Scheme is "https" or "http")
                    port = uri.Port;
                path = Uri.UnescapeDataString(uri.AbsolutePath);
            }
            else
            {
                var at = remote.IndexOf('@');
                var colon = remote.IndexOf(':', at + 1);
                if (at < 0 || colon <= at + 1)
                    return default;
                host = remote[(at + 1)..colon];
                if (Uri.CheckHostName(host) == UriHostNameType.Unknown)
                    return default;
                path = remote[(colon + 1)..];
            }

            path = path.Trim('/');
            if (path.EndsWith(".git", StringComparison.Ordinal))
                path = path[..^4];
            return (host, port, path.Contains('/') ? path : null);
        }
    }
}
