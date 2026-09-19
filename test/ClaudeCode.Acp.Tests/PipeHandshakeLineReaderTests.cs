using ClaudeCode.Contracts;
using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace ClaudeCode.Acp.Tests;

/// <summary>
/// Covers the byte-bounded handshake read used by the VsControl named-pipe server. The pipe name is
/// enumerable, so any same-user process can connect and stream newline-free bytes; the reader must
/// refuse past a small cap instead of accumulating them, and must not consume anything the
/// authenticated request loop still needs.
/// </summary>
public sealed class PipeHandshakeLineReaderTests
{
    private const int _cap = 512;

    [Fact]
    public async Task ReadBoundedLineAsync_WhenLineExceedsCap_RejectsWithoutConsumingTheRest()
    {
        using var reader = new CountingReader(new string('a', 100_000) + "\n");

        var line = await PipeHandshakeLineReader.ReadBoundedLineAsync(reader, _cap);

        Assert.Null(line);
        Assert.True(
            reader.CharsRead <= _cap + 1,
            $"Expected the read to stop at the {_cap}-char cap, but it consumed {reader.CharsRead} characters.");
    }

    [Fact]
    public async Task ReadBoundedLineAsync_ReturnsLineWithoutTerminatorAndLeavesTheRestReadable()
    {
        using var reader = new CountingReader("token-line\n{\"method\":\"saveAll\"}\n");

        var line = await PipeHandshakeLineReader.ReadBoundedLineAsync(reader, _cap);

        Assert.Equal("token-line", line);
        Assert.Equal("{\"method\":\"saveAll\"}", await reader.ReadLineAsync());
    }

    [Fact]
    public async Task ReadBoundedLineAsync_AcceptsALineExactlyAtTheCap()
    {
        var token = new string('b', 44);
        using var reader = new CountingReader(token + "\n");

        Assert.Equal(token, await PipeHandshakeLineReader.ReadBoundedLineAsync(reader, token.Length));
    }

    [Fact]
    public async Task ReadBoundedLineAsync_TreatsCarriageReturnAsATerminator()
    {
        using var reader = new CountingReader("token-line\r\n");

        Assert.Equal("token-line", await PipeHandshakeLineReader.ReadBoundedLineAsync(reader, _cap));
    }

    [Fact]
    public async Task ReadBoundedLineAsync_WhenPeerSendsNothing_ReturnsNull()
    {
        using var reader = new CountingReader(string.Empty);

        Assert.Null(await PipeHandshakeLineReader.ReadBoundedLineAsync(reader, _cap));
    }

    [Fact]
    public async Task ReadBoundedLineAsync_WhenPeerClosesWithoutATerminator_ReturnsWhatItSent()
    {
        using var reader = new CountingReader("token-line");

        Assert.Equal("token-line", await PipeHandshakeLineReader.ReadBoundedLineAsync(reader, _cap));
    }

    /// <summary>A <see cref="TextReader"/> that records how much of its content was handed out, so a
    /// reader that ignores the cap is observable rather than merely slow.</summary>
    private sealed class CountingReader : TextReader
    {
        private readonly string _content;
        private int _position;

        public CountingReader(string content) => _content = content;

        public int CharsRead { get; private set; }

        public override int Peek() => _position >= _content.Length ? -1 : _content[_position];

        public override int Read()
        {
            if (_position >= _content.Length)
            {
                return -1;
            }

            CharsRead++;
            return _content[_position++];
        }

        public override int Read(char[] buffer, int index, int count)
        {
            var available = Math.Min(count, _content.Length - _position);
            if (available <= 0)
            {
                return 0;
            }

            _content.CopyTo(_position, buffer, index, available);
            _position += available;
            CharsRead += available;
            return available;
        }
    }
}
