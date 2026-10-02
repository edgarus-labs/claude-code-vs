using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace ClaudeCode.Acp;

/// <summary>Reads the stdout of a helper subprocess under a hard character bound.</summary>
public static class BoundedProcessOutput
{
    /// <summary>
    /// Reads <paramref name="reader"/> to end and returns its text, or an empty string if the output
    /// would exceed <paramref name="maxCharacters"/>.
    /// <para>
    /// An oversize payload is discarded, but the reader is still drained to end of stream.
    /// </para>
    /// </summary>
    public static async Task<string> ReadBoundedAsync(TextReader reader, int maxCharacters)
    {
        var buffer = new char[1024];
        var output = new StringBuilder();
        bool exceededBound = false;
        int count;
        while ((count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) != 0)
        {
            if (exceededBound)
            {
                continue;
            }

            if (output.Length + count > maxCharacters)
            {
                exceededBound = true;
                output.Clear();
                continue;
            }

            output.Append(buffer, 0, count);
        }

        return output.ToString();
    }
}
