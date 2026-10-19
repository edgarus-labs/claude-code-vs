using System.Collections.Generic;

namespace ClaudeCode.Acp;

/// <summary>
/// Represents an executable specification containing the file name and arguments for process execution.
/// </summary>
public sealed class AcpExecutableSpec
{
    /// <summary>
    /// Initializes a new instance of the AcpExecutableSpec class with the specified file name and arguments.
    /// </summary>
    /// <param name="fileName">The file name.</param>
    /// <param name="arguments">The collection of arguments.</param>
    public AcpExecutableSpec(string fileName, IReadOnlyList<string> arguments)
    {
        FileName = fileName;
        Arguments = arguments;
    }

    /// <summary>
    /// Gets the file name.
    /// </summary>
    public string FileName { get; }

    /// <summary>
    /// Gets the collection of arguments.
    /// </summary>
    public IReadOnlyList<string> Arguments { get; }
}
