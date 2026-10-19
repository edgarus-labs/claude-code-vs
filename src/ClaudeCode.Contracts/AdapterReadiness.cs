using System;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.Contracts;

/// <summary>
/// Specifies the readiness states of a local adapter, indicating whether it is ready for use or missing its adapter or required Node.js and npm runtime dependencies.
/// </summary>
public enum AdapterReadiness
{
    Ready,
    MissingAdapter,
    MissingNodeOrNpm
}
