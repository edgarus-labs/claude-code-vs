using System;
using System.Collections.Generic;

namespace ClaudeCode.Contracts;

public abstract class ContentBlock
{
    public sealed class Text : ContentBlock
    {
        public Text(string text) => Value = text;

        public string Value { get; }
    }

    public sealed class Image : ContentBlock
    {
        public Image(string mimeType, string base64Data)
        {
            MimeType = mimeType; Base64Data = base64Data;
        }

        public string MimeType { get; }

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

        public string Uri { get; }

        public new string Text { get; }

        public string? MimeType { get; }
    }

    public sealed class ResourceLink : ContentBlock
    {
        public ResourceLink(string uri, string? name)
        {
            Uri = uri; Name = name;
        }

        public string Uri { get; }

        public string? Name { get; }
    }
}
