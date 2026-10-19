using System;

namespace ClaudeCode.Core.ViewModels;

/// <summary>
/// Represents a snapshot of an editor document containing the file path and text content.
/// </summary>
public sealed class EditorDocumentSnapshot
{
    /// <summary>
    /// Initializes a new instance of the EditorDocumentSnapshot class with the specified document path and text, validating that the path is not null or whitespace and that the text is not null.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <param name="text">The text.</param>
    /// <exception cref="ArgumentException">Thrown when an error occurs during execution.</exception>
    /// <exception cref="ArgumentNullException">Thrown when an error occurs during execution.</exception>
    public EditorDocumentSnapshot(string path, string text)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A document path is required.", nameof(path));
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
