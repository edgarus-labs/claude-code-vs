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

    public string Name { get; }
    public string MimeType { get; }
    public string Base64Data { get; }
    public bool IsDocument => DocumentPath is not null;
    public bool IsImage => !IsDocument;
    internal string? DocumentPath { get; }

    internal ContentBlock ToContentBlock() => _content ?? new ContentBlock.Image(MimeType, Base64Data);
}
