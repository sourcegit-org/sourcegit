using System;
using System.Linq;
using System.Text.Json.Serialization;

using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.AI
{
    public class ExternalCliProfile : ObservableObject
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        public string Executable { get; set; } = string.Empty;
        public string Arguments { get; set; } = string.Empty;
        public string ResumeArguments { get; set; } = string.Empty;
        public string ChatModels { get; set; } = string.Empty;
        public string ChatModelArguments { get; set; } = string.Empty;
        public string ChatEfforts { get; set; } = string.Empty;
        public string ChatEffortArguments { get; set; } = string.Empty;
        public string AdditionalPrompt { get; set; } = string.Empty;
        public bool CanGenerateCommit { get; set; } = true;
        public bool CanAnalyze { get; set; } = true;
        public bool CanDraftPullRequest { get; set; }
        public bool UsePromptFile { get; set; }
        public bool StructuredOutput { get; set; }
        public bool ResumeGeneration { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string LegacyArguments { get; set; }

        [JsonIgnore]
        internal bool IsConfigured => Id != Guid.Empty && !string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(Executable);

        internal static string[] ReadArguments(string value) => (value ?? string.Empty).ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        internal const string ChatModelToken = "{model}";
        internal const string ChatEffortToken = "{effort}";

        internal static string[] ReadChatChoices(string choices, string arguments, string token) =>
            arguments?.Contains(token, StringComparison.Ordinal) == true
                ? ReadArguments(choices).Select(static choice => choice.Trim())
                    .Where(static choice => choice.Length > 0 && !choice.Contains('\0') && !choice.Contains('{') && !choice.Contains('}'))
                    .Distinct(StringComparer.Ordinal).ToArray()
                : [];

        internal static string[] ExpandChatArguments(string choices, string arguments, string token, string value) =>
            !string.IsNullOrEmpty(value) && ReadChatChoices(choices, arguments, token).Contains(value, StringComparer.Ordinal)
                ? ReadArguments(arguments).Select(argument => argument.Replace(token, value, StringComparison.Ordinal)).ToArray()
                : [];

        internal static ExternalCliProfile[] Available() => ViewModels.Preferences.Instance.ExternalCliProfiles
            ?.Where(static profile => profile is { IsConfigured: true }).ToArray() ?? [];

        private string _name = string.Empty;
    }
}
