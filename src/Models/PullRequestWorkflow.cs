using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.Models
{
    public class PullRequestWorkflow
    {
        public Guid PublisherId { get; set; }
        public string PushRemote { get; set; } = string.Empty;
        public string TargetRemote { get; set; } = string.Empty;
        public string TargetBranch { get; set; } = string.Empty;
        public string DescriptionTemplate { get; set; } = string.Empty;
        public Dictionary<string, string> LastTargets { get; set; } = new(StringComparer.Ordinal);
    }

    public class PullRequestPublisher : ObservableObject
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        public string Executable { get; set; } = string.Empty;
        public string Arguments { get; set; } = string.Empty;
        public int TimeoutSeconds { get; set; } = 120;

        internal bool IsConfigured => Id != Guid.Empty && !string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(Executable);

        private string _name = string.Empty;
    }

    internal sealed record PullRequestEndpoint(
        [property: JsonPropertyName("remote")] string Remote,
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("branch")] string Branch,
        [property: JsonPropertyName("commit")] string Commit);

    internal sealed record PullRequestPublication(
        [property: JsonPropertyName("operation")] string Operation,
        [property: JsonPropertyName("source")] PullRequestEndpoint Source,
        [property: JsonPropertyName("target")] PullRequestEndpoint Target,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("description")] string Description);

    internal sealed record PullRequestPublicationResult(
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("url")] string Url);
}
