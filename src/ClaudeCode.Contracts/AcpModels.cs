using System;
using System.Collections.Generic;

namespace ClaudeCode.Contracts;

public abstract class ContentBlock
{
    /// <summary>
    /// Represents a text content block that holds a string value for rendering within a document or message body.
    /// </summary>
    public sealed class Text : ContentBlock
    {
        /// <summary>
        /// Initializes a new instance of the Text class with the specified text value.
        /// </summary>
        /// <param name="text">The text.</param>
        public Text(string text) => Value = text;

        /// <summary>
        /// Gets the value.
        /// </summary>
        public string Value { get; }
    }

    /// <summary>
    /// Represents an image content block containing a MIME type and base64-encoded image data.
    /// </summary>
    public sealed class Image : ContentBlock
    {
        /// <summary>
        /// Initializes a new instance of the Image class with the specified MIME type and Base64-encoded data.
        /// </summary>
        /// <param name="mimeType">The mime type.</param>
        /// <param name="base64Data">The base64 data.</param>
        public Image(string mimeType, string base64Data)
        {
            MimeType = mimeType; Base64Data = base64Data;
        }

        /// <summary>
        /// Gets the mime type.
        /// </summary>
        public string MimeType { get; }

        /// <summary>
        /// Gets the base64 data.
        /// </summary>
        public string Base64Data { get; }
    }

    /// <summary>
    /// Represents an embedded text resource content block that carries its textual content, a URI identifier, and an optional MIME type within a message.
    /// </summary>
    public sealed class EmbeddedTextResource : ContentBlock
    {
        /// <summary>
        /// Initializes a new instance of the EmbeddedTextResource class with the specified URI, text content, and optional MIME type, which defaults to &quot;text/plain&quot;.
        /// </summary>
        /// <param name="uri">The uri.</param>
        /// <param name="text">The text.</param>
        /// <param name="mimeType">The mime type.</param>
        public EmbeddedTextResource(string uri, string text, string? mimeType = "text/plain")
        {
            Uri = uri;
            Text = text;
            MimeType = mimeType;
        }

        /// <summary>
        /// Gets the uri.
        /// </summary>
        public string Uri { get; }

        /// <summary>
        /// Gets the text.
        /// </summary>
        public new string Text { get; }

        /// <summary>
        /// Gets the mime type.
        /// </summary>
        public string? MimeType { get; }
    }

    /// <summary>
    /// Represents a content block that links to an external resource by its URI, with an optional display name.
    /// </summary>
    public sealed class ResourceLink : ContentBlock
    {
        /// <summary>
        /// Initializes a new instance of the ResourceLink class with the specified URI and optional display name.
        /// </summary>
        /// <param name="uri">The uri.</param>
        /// <param name="name">The name.</param>
        public ResourceLink(string uri, string? name)
        {
            Uri = uri; Name = name;
        }

        /// <summary>
        /// Gets the uri.
        /// </summary>
        public string Uri { get; }

        /// <summary>
        /// Gets the name.
        /// </summary>
        public string? Name { get; }
    }
}
