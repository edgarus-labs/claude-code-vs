using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace ClaudeCode.Contracts;

/// <summary>
/// Reads one length-bounded line from the VsControl named-pipe handshake.
/// </summary>
public static class PipeHandshakeLineReader
{
    /// <summary>
    /// Reads characters up to the first <c>'\n'</c> or <c>'\r'</c> terminator, which is consumed but
    /// not returned. Returns <see langword="null"/> when the peer sent nothing, or when the line
    /// reaches <paramref name="maxChars"/> without a terminator; in the latter case at most one
    /// further character is consumed before the read stops.
    /// A peer that closes the stream mid-line yields what it sent, matching
    /// <see cref="TextReader.ReadLine"/>.
    /// A <c>'\r'</c> terminator leaves any following <c>'\n'</c> in the reader.
    /// </summary>
    public static async Task<string?> ReadBoundedLineAsync(TextReader reader, int maxChars)
    {
        if (reader is null)
        {
            throw new ArgumentNullException(nameof(reader));
        }

        if (maxChars <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxChars), maxChars, "The line cap must be positive.");
        }

        var line = new StringBuilder(Math.Min(maxChars, 64));

        var one = new char[1];
        while (true)
        {
            if (await reader.ReadAsync(one, 0, 1).ConfigureAwait(false) == 0)
            {
                return line.Length == 0 ? null : line.ToString();
            }

            var c = one[0];
            if (c == '\n' || c == '\r')
            {
                return line.ToString();
            }

            if (line.Length == maxChars)
            {
                return null;
            }

            line.Append(c);
        }
    }
}
