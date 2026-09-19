namespace ClaudeCode.Core.ViewModels;

public sealed class UsageWarningViewModel
{
    public UsageWarningViewModel(string message)
    {
        Message = message;
    }

    public string Message { get; }
}
