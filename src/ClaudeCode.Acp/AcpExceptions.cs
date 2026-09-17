using System;
using System.Text.Json.Nodes;

namespace ClaudeCode.Acp
{
    /// <summary>
    /// A JSON-RPC 2.0 error object, either returned by the remote agent in response to a request we sent,
    /// or thrown by an inbound-request handler in this process to control the error written back to the
    /// agent for a request it sent us (e.g. fs/read_text_file, session/request_permission).
    /// </summary>
    public sealed class AcpRemoteException : Exception
    {
        public AcpRemoteException(int code, string message, JsonNode? data = null)
            : base(message)
        {
            Code = code;
            Data2 = data;
        }

        public int Code { get; }

        /// <summary>Optional structured error payload from the JSON-RPC `error.data` field. Named to avoid
        /// colliding with <see cref="Exception.Data"/>.</summary>
        public JsonNode? Data2 { get; }
    }

    /// <summary>Thrown when a message received from the agent does not conform to the shape this client expects
    /// (missing required field, wrong JSON type, etc.) - a local parsing failure, not a remote error response.</summary>
    public sealed class AcpProtocolException : Exception
    {
        public AcpProtocolException(string message) : base(message)
        {
        }
    }
}
