namespace SourceGit.ViewModels
{
    /// <summary>
    /// Shared contract for the AI response views, so that a single
    /// <c>AIResponseView</c> can serve multiple assistant backends.
    /// </summary>
    public interface IAssistant
    {
        void Use(string text);
    }
}
