using System;
using System.Collections.Generic;
using System.IO;

namespace ClaudeCode.Acp;

public sealed class AcpExecutableSpec
{
    public AcpExecutableSpec(string fileName, IReadOnlyList<string> arguments)
    {
        FileName = fileName;
        Arguments = arguments;
    }

    public string FileName { get; }

    public IReadOnlyList<string> Arguments { get; }
}
