using System;
using System.Text.Json.Nodes;

namespace ClaudeCode.Acp;

public sealed class AcpProtocolException : Exception
{
    public AcpProtocolException(string message) : base(message)
    {
    }
}
