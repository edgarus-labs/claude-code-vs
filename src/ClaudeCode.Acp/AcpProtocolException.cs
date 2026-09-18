using System;

namespace ClaudeCode.Acp;

public sealed class AcpProtocolException : Exception
{
    public AcpProtocolException(string message) : base(message)
    {
    }
}
