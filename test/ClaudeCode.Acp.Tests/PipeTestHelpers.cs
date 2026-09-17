using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using System.Threading.Tasks;

namespace ClaudeCode.Acp.Tests
{
    /// <summary>NDJSON line read/write helpers shared by the framing (<see cref="JsonRpcConnection"/>) and
    /// ACP-translation (<see cref="AcpProcessConnection"/>) tests, both of which drive the code under test
    /// through in-memory <see cref="Pipe"/> pairs standing in for a child process's stdin/stdout.</summary>
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
                if (newlinePosition != null)
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
}
