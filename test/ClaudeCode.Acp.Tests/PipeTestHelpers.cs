using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using System.Threading.Tasks;

namespace ClaudeCode.Acp.Tests;

/// <summary>
/// Represents utility methods for asynchronous pipe operations.
/// </summary>
internal static class PipeTestHelpers
{
    /// <summary>
    /// Asynchronously writes the specified JSON string followed by a newline to the provided PipeWriter.
    /// </summary>
    /// <param name="writer">The writer.</param>
    /// <param name="json">The json.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public static async Task WriteLineAsync(PipeWriter writer, string json)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(json + "\n");
        await writer.WriteAsync(bytes);
    }

    /// <summary>
    /// Asynchronously reads a UTF-8 encoded line terminated by a newline character from the specified pipe reader, advancing the reader past the consumed data and throwing an InvalidOperationException if the pipe completes before a full line is available.
    /// </summary>
    /// <param name="reader">The reader.</param>
    /// <returns>A task representing the asynchronous operation. The task result contains the string.</returns>
    /// <exception cref="InvalidOperationException">Thrown when an error occurs during execution.</exception>
    public static async Task<string> ReadLineAsync(PipeReader reader)
    {
        while (true)
        {
            ReadResult result = await reader.ReadAsync();
            ReadOnlySequence<byte> buffer = result.Buffer;
            SequencePosition? newlinePosition = buffer.PositionOf((byte)'\n');
            if (newlinePosition is not null)
            {
                ReadOnlySequence<byte> linePart = buffer.Slice(0, newlinePosition.Value);
                string line = Encoding.UTF8.GetString(linePart.ToArray());
                reader.AdvanceTo(buffer.GetPosition(1, newlinePosition.Value));

                return line;
            }

            if (result.IsCompleted)
            {
                throw new InvalidOperationException("Pipe completed before a full line was available.");
            }

            reader.AdvanceTo(buffer.Start, buffer.End);
        }
    }
}
