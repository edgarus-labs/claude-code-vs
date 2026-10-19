namespace ClaudeCode.Core.ViewModels;

/// <summary>
/// Represents an image attachment in a chat message, carrying its name, MIME type, and Base64-encoded content.
/// </summary>
public sealed class ChatMessageImage
{
    /// <summary>
    /// Initializes a new instance of the ChatMessageImage class with the specified image name, MIME type, and Base64-encoded image data.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <param name="mimeType">The mime type.</param>
    /// <param name="base64Data">The base64 data.</param>
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
