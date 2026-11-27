using System.Linq;
using System.Text.Json.Serialization;

namespace SourceGit.AI
{
    public sealed record ExternalCliSkill(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("source")] string Source)
    {
        [JsonIgnore]
        public string Label => string.IsNullOrWhiteSpace(Name) ? Id : Name;

        internal bool HasValidId => Id is { Length: > 0 and <= 128 } && char.IsAsciiLetterOrDigit(Id[0]) &&
            Id.All(static ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' or ':' or '/');
    }
}
