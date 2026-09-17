using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using System.Threading.Tasks;

namespace ClaudeCode.Acp.Tests;

internal static class PipeTestHelpers
{
    public static async Task WriteLineAsync(PipeWriter writer, string json)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(json + "\n");
        await writer.WriteAsync(bytes);
    }

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
