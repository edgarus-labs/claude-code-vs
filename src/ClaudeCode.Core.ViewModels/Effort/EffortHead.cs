using System;
using System.Globalization;
using System.IO;
using System.Linq;

namespace ClaudeCode.Core.Effort;

/// <summary>
/// The trained 384 -> 3 multinomial logistic-regression head over a <see cref="MiniLmEncoder"/>
/// embedding. File format (written by tools/EffortClassifierTrainer): '#' comment lines, then one
/// line per class in <see cref="EffortLevel"/> order: the bias followed by 384 weights, invariant culture.
/// </summary>
public sealed class EffortHead
{
    public const string FileName = "effort-head.txt";
    private const int ClassCount = 3;
    private readonly float[][] _rows;

    private EffortHead(float[][] rows) => _rows = rows;

    public static EffortHead Load(string path)
    {
        var rows = File.ReadLines(path)
            .Where(line => line.Length > 0 && line[0] != '#')
            .Select(line => line.Split(' ').Select(value => float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray())
            .ToArray();
        if (rows.Length != ClassCount || rows.Any(row => row.Length != MiniLmEncoder.Dimensions + 1))
            throw new InvalidDataException($"{path} is not a {ClassCount} x ({MiniLmEncoder.Dimensions} + 1) effort head.");
        return new EffortHead(rows);
    }

    /// <summary>Softmax probabilities in <see cref="EffortLevel"/> order.</summary>
    public float[] Predict(float[] embedding)
    {
        var logits = new double[ClassCount];
        for (int c = 0; c < ClassCount; c++)
        {
            var row = _rows[c];
            double sum = row[0];
            for (int d = 0; d < MiniLmEncoder.Dimensions; d++) sum += row[d + 1] * embedding[d];
            logits[c] = sum;
        }
        double max = logits.Max();
        var exp = logits.Select(logit => Math.Exp(logit - max)).ToArray();
        double total = exp.Sum();
        return exp.Select(value => (float)(value / total)).ToArray();
    }
}
