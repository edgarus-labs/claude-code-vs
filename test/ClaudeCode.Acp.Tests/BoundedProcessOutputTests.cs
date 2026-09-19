using System.IO;
using System.Threading.Tasks;
using ClaudeCode.Acp;
using Xunit;

namespace ClaudeCode.Acp.Tests;

/// <summary>Covers <see cref="BoundedProcessOutput.ReadBoundedAsync(TextReader, int)"/>, which reads
/// a helper subprocess's stdout under a hard bound. The output is untrusted: its size is chosen by
/// whatever the helper's endpoint returns.</summary>
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
        // The bound is inclusive, so a payload landing exactly on it is still usable output; an
        // off-by-one here would silently discard a legitimate response.
        var payload = new string('a', 1024);

        Assert.Equal(payload, await BoundedProcessOutput.ReadBoundedAsync(new StringReader(payload), 1024));
    }

    [Fact]
    public async Task ReadBoundedAsync_OutputExceedingBound_DiscardsItButStillDrainsToEndOfStream()
    {
        // Draining is the load-bearing half: stopping at the bound leaves the child blocked writing
        // into a full stdout pipe, so it never exits and the caller burns its whole process timeout
        // before killing it. Asserting only the empty result would pass with that bug present.
        var reader = new StringReader(new string('a', 4096));

        var result = await BoundedProcessOutput.ReadBoundedAsync(reader, 1024);

        Assert.Equal(string.Empty, result);
        Assert.Equal(-1, reader.Read());
    }

    [Fact]
    public async Task ReadBoundedAsync_OutputOneCharacterOverBound_IsDiscardedEntirely()
    {
        // Oversize output must not be handed back truncated: a half-read JSON document would parse
        // as a malformed response rather than as "unavailable".
        var result = await BoundedProcessOutput.ReadBoundedAsync(new StringReader(new string('a', 1025)), 1024);

        Assert.Equal(string.Empty, result);
    }
}
