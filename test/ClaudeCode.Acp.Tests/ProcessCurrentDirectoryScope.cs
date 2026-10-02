using System;
using System.IO;
using ClaudeCode.Acp;
using Xunit;

namespace ClaudeCode.Acp.Tests;

/// <summary>Test collection that runs classes calling <see cref="Directory.SetCurrentDirectory(string)"/>
/// sequentially.</summary>
[CollectionDefinition("Process current directory", DisableParallelization = true)]
public sealed class ProcessCurrentDirectoryScope
{
}
