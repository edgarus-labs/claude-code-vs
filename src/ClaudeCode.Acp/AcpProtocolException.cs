using System;

namespace ClaudeCode.Acp;

/// <summary>
/// Represents an exception thrown when an error occurs during ACP protocol processing.
/// </summary>
public sealed class AcpProtocolException : Exception
{
    /// <summary>
    /// Initializes a new instance of the AcpProtocolException class with the specified error message.
    /// </summary>
    /// <param name="message">The message.</param>
    public AcpProtocolException(string message) : base(message)
    {
    }
}
