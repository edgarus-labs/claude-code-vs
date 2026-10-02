namespace ClaudeCode.Core.ViewModels;

public sealed class UsageWarningViewModel
{
    /// <summary>
    /// Initializes a new instance of the UsageWarningViewModel class with the specified warning message.
    /// </summary>
    /// <param name="message">The message.</param>
    public UsageWarningViewModel(string message)
    {
        Message = message;
    }

    /// <summary>
    /// Gets the message.
    /// </summary>
    public string Message { get; }
}
