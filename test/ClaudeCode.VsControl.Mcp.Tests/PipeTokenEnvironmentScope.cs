using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCode.Contracts;
using Xunit;

namespace ClaudeCode.VsControl.Mcp.Tests;

[CollectionDefinition("Pipe token environment", DisableParallelization = true)]
public sealed class PipeTokenEnvironmentScope
{
}
