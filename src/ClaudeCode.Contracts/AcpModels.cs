namespace ClaudeCode.Contracts;

public abstract class ContentBlock
{
    public sealed class Text : ContentBlock
    {
        public Text(string text) => Value = text;

        /// <summary>
        /// Gets the value.
        /// </summary>
        public string Value { get; }
    }

    public sealed class Image : ContentBlock
    {
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

    public sealed class EmbeddedTextResource : ContentBlock
    {
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

    public sealed class ResourceLink : ContentBlock
    {
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
