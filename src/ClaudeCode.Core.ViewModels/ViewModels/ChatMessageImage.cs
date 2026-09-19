namespace ClaudeCode.Core.ViewModels;

public sealed class ChatMessageImage
{
    public ChatMessageImage(string name, string mimeType, string base64Data)
    {
        Name = name;
        MimeType = mimeType;
        Base64Data = base64Data;
    }

    public string Name { get; }

    public string MimeType { get; }

    public string Base64Data { get; }
}
