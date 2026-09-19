using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace ClaudeCode.Contracts;

/// <summary>
/// Reads one length-bounded line from the VsControl named-pipe handshake (see
/// <c>VsControlPipeServer.TryHandshakeAsync</c> in the net48 Vsix host). Lives here rather than in
/// the net48 host so the net8.0 test suite can exercise the real production logic, exactly like
/// <see cref="PipeSecurityFactory"/> and <see cref="ConstantTimeTokenComparer"/>.
/// <para>
/// <see cref="TextReader.ReadLineAsync()"/> cannot be used for this read: the pipe name is
/// enumerable by any process running as the same Windows user, and an unauthenticated peer that
/// connects and streams newline-free bytes would have every one of them accumulated into a managed
/// buffer inside devenv.exe. The handshake token is a fixed-length base64 value, so a longer line
/// can never be valid; this reader stops at the cap and fails closed instead.
/// </para>
/// </summary>
public static class PipeHandshakeLineReader
{
    /// <summary>
    /// Reads characters up to the first <c>'\n'</c> or <c>'\r'</c> terminator, which is consumed but
    /// not returned. Returns <see langword="null"/> when the peer sent nothing, or when the line
    /// reaches <paramref name="maxChars"/> without a terminator - in the latter case at most one
    /// further character is consumed before the read stops, so a hostile peer can never drive
    /// more than the cap into memory.
    /// A peer that closes the stream mid-line yields what it sent, matching
    /// <see cref="TextReader.ReadLine"/>; the value still has to pass the token comparison.
    /// A <c>'\r'</c> terminator leaves any following <c>'\n'</c> in the reader, where the request
    /// loop reads it as a blank line and skips it.
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

        // One character at a time: the reader is shared with the authenticated request loop that
        // runs after the handshake, so reading ahead in blocks would swallow the first request.
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
