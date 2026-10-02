using System;

namespace ClaudeCode.Core.ViewModels;

public sealed class EditorDocumentSnapshot
{
    public EditorDocumentSnapshot(string path, string text)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A document path is required.", nameof(path));
        }

        Path = path;
        Text = text ?? throw new ArgumentNullException(nameof(text));
    }

    /// <summary>
    /// Gets the path.
    /// </summary>
    public string Path { get; }
    /// <summary>
    /// Gets the text.
    /// </summary>
    public string Text { get; }
}
