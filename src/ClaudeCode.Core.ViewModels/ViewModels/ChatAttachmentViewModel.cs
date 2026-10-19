using ClaudeCode.Contracts;
using System;
using System.IO;

namespace ClaudeCode.Core.ViewModels;

/// <summary>
/// Represents a chat attachment view model that encapsulates a file&apos;s name, MIME type, and Base64-encoded content, and converts it into a content block for AI chat message payloads.
/// </summary>
public sealed class ChatAttachmentViewModel
{
    /// <summary>
    /// Initializes a new instance of the ChatAttachmentViewModel class with the specified attachment name, MIME type, and Base64-encoded data.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <param name="mimeType">The mime type.</param>
    /// <param name="base64Data">The base64 data.</param>
    public ChatAttachmentViewModel(string name, string mimeType, string base64Data)
    {
        Name = name;
        MimeType = mimeType;
        Base64Data = base64Data;
    }

    /// <summary>
    /// Initializes a new instance of the ChatAttachmentViewModel class from an editor document snapshot, resolving its full path and file name, setting the MIME type to text/plain, and embedding its text content as a resource.
    /// </summary>
    /// <param name="document">The document.</param>
    internal ChatAttachmentViewModel(EditorDocumentSnapshot document)
    {
        DocumentPath = Path.GetFullPath(document.Path);
        Name = Path.GetFileName(DocumentPath);
        MimeType = "text/plain";
        Base64Data = string.Empty;
        _content = new ContentBlock.EmbeddedTextResource(new Uri(DocumentPath, UriKind.Absolute).AbsoluteUri, document.Text);
    }

    private readonly ContentBlock? _content;

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
    /// <summary>
    /// Gets a value indicating whether is document.
    /// </summary>
    public bool IsDocument => DocumentPath is not null;
    /// <summary>
    /// Gets a value indicating whether is image.
    /// </summary>
    public bool IsImage => !IsDocument;
    /// <summary>
    /// Gets the document path.
    /// </summary>
    internal string? DocumentPath { get; }

    /// <summary>
    /// Returns the cached content block if one has been assigned; otherwise, creates a new image content block from the stored MIME type and Base64-encoded data.
    /// </summary>
    /// <returns>The content block result.</returns>
    internal ContentBlock ToContentBlock() => _content ?? new ContentBlock.Image(MimeType, Base64Data);
}
