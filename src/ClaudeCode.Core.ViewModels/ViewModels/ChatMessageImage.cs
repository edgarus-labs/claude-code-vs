namespace ClaudeCode.Core.ViewModels;

public sealed class ChatMessageImage
{
    public ChatMessageImage(string name, string mimeType, string base64Data)
    {
        Name = name;
        MimeType = mimeType;
        Base64Data = base64Data;
    }

    /// <summary>
    /// Gets the name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the mime type.
    /// </summary>
    public string MimeType { get; }

    /// <summary>
    /// Gets the base64 data.
    /// </summary>
    public string Base64Data { get; }
}
