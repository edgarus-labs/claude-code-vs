using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System;
using System.Collections.Generic;
using System.IO;

namespace ClaudeCode.Core.Effort;

/// <summary>
/// Frozen all-MiniLM-L6-v2 sentence encoder (quantised ONNX): token embeddings mean-pooled over the
/// attention mask and L2-normalised, exactly as sentence-transformers produces them.
/// </summary>
public sealed class MiniLmEncoder : IDisposable
{
    public const int Dimensions = 384;
    public const string ModelFileName = "all-MiniLM-L6-v2.quint8-avx2.onnx";
    public const string VocabFileName = "vocab.txt";

    private readonly WordPieceTokenizer _tokenizer;
    private readonly InferenceSession _session;

    private MiniLmEncoder(WordPieceTokenizer tokenizer, InferenceSession session)
    {
        _tokenizer = tokenizer;
        _session = session;
    }

    public static MiniLmEncoder Load(string modelDirectory)
    {
        var modelPath = Path.Combine(modelDirectory, ModelFileName);
        // InferenceSession reports a missing file as a native OnnxRuntimeException; surface it as the IO failure it is.
        if (!File.Exists(modelPath)) throw new FileNotFoundException("Auto effort model not found.", modelPath);
        var tokenizer = WordPieceTokenizer.Load(Path.Combine(modelDirectory, VocabFileName));
        // One short sentence per turn: a single intra-op thread keeps the UI host's CPU usage flat.
        using var options = new SessionOptions { IntraOpNumThreads = 1, InterOpNumThreads = 1 };
        return new MiniLmEncoder(tokenizer, new InferenceSession(modelPath, options));
    }

    /// <summary>Thread-safe: InferenceSession.Run may be called concurrently.</summary>
    public float[] Embed(string text)
    {
        var ids = _tokenizer.Encode(text);
        int length = ids.Count;
        var shape = new[] { 1, length };
        var inputIds = new DenseTensor<long>(shape);
        var attentionMask = new DenseTensor<long>(shape);
        var tokenTypeIds = new DenseTensor<long>(shape);
        for (int i = 0; i < length; i++)
        {
            inputIds[0, i] = ids[i];
            attentionMask[0, i] = 1;
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask),
            NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIds),
        };
        using var results = _session.Run(inputs);
        var hidden = results[0].AsTensor<float>();

        // No padding in a single-sentence batch, so the masked mean is the plain mean over tokens.
        var embedding = new float[Dimensions];
        for (int token = 0; token < length; token++)
            for (int d = 0; d < Dimensions; d++)
                embedding[d] += hidden[0, token, d];
        double norm = 0;
        for (int d = 0; d < Dimensions; d++)
        {
            embedding[d] /= length;
            norm += (double)embedding[d] * embedding[d];
        }
        norm = Math.Max(Math.Sqrt(norm), 1e-12);
        for (int d = 0; d < Dimensions; d++) embedding[d] = (float)(embedding[d] / norm);
        return embedding;
    }

    public void Dispose() => _session.Dispose();
}
