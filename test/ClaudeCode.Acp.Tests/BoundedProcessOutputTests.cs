using System.IO;
using System.Threading.Tasks;
using ClaudeCode.Acp;
using Xunit;

namespace ClaudeCode.Acp.Tests;

/// <summary>Covers <see cref="BoundedProcessOutput.ReadBoundedAsync(TextReader, int)"/>, which reads
/// a subprocess's stdout up to a hard bound and discards output that exceeds it.</summary>
public sealed class BoundedProcessOutputTests
{
    [Fact]
    public async Task ReadBoundedAsync_OutputWithinBound_ReturnsItVerbatim()
    {
        var payload = new string('a', 900);

        Assert.Equal(payload, await BoundedProcessOutput.ReadBoundedAsync(new StringReader(payload), 1024));
    }

    [Fact]
    public async Task ReadBoundedAsync_OutputExactlyAtBound_ReturnsItVerbatim()
    {
        var payload = new string('a', 1024);

        Assert.Equal(payload, await BoundedProcessOutput.ReadBoundedAsync(new StringReader(payload), 1024));
    }

    [Fact]
    public async Task ReadBoundedAsync_OutputExceedingBound_DiscardsItButStillDrainsToEndOfStream()
    {
        var reader = new StringReader(new string('a', 4096));

        var result = await BoundedProcessOutput.ReadBoundedAsync(reader, 1024);

        Assert.Equal(string.Empty, result);
        Assert.Equal(-1, reader.Read());
    }

    [Fact]
    public async Task ReadBoundedAsync_OutputOneCharacterOverBound_IsDiscardedEntirely()
    {
        var result = await BoundedProcessOutput.ReadBoundedAsync(new StringReader(new string('a', 1025)), 1024);

        Assert.Equal(string.Empty, result);
    }
}
