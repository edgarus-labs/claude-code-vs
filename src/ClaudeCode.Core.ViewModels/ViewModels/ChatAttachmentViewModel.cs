using ClaudeCode.Contracts;
using System;
using System.IO;

namespace ClaudeCode.Core.ViewModels;

public sealed class ChatAttachmentViewModel
{
    public ChatAttachmentViewModel(string name, string mimeType, string base64Data)
    {
        Name = name;
        MimeType = mimeType;
        Base64Data = base64Data;
    }

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
    /// Converts the stored content into a ContentBlock.Image instance using the MIME type and Base64 data, returning null when no content is present.
    /// </summary>
    /// <returns>The content block result.</returns>
    internal ContentBlock ToContentBlock() => _content ?? new ContentBlock.Image(MimeType, Base64Data);
}
