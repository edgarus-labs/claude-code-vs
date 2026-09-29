using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.Core.Effort;

/// <summary>
/// Auto effort's classifier: <see cref="MiniLmEncoder"/> + <see cref="EffortHead"/> +
/// <see cref="AutoEffortPolicy"/>, all local. Construction only records where the model lives; the
/// ~23 MB model is loaded on the first <see cref="ClassifyAsync"/>, off the calling thread, so a user
/// who never picks Auto never pays for it. A load failure is kept and reported by every call.
/// </summary>
public sealed class MiniLmEffortClassifier : IEffortClassifier, IDisposable
{
    private readonly Lazy<(MiniLmEncoder Encoder, EffortHead Head)> _model;

    public MiniLmEffortClassifier(string modelDirectory)
    {
        _model = new Lazy<(MiniLmEncoder, EffortHead)>(() =>
        {
            var head = EffortHead.Load(Path.Combine(modelDirectory, EffortHead.FileName));
            return (MiniLmEncoder.Load(modelDirectory), head);
        }, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public bool IsLoaded => _model.IsValueCreated;

    public Task<EffortLevel> ClassifyAsync(string prompt, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var (encoder, head) = _model.Value;
            cancellationToken.ThrowIfCancellationRequested();
            return AutoEffortPolicy.Decide(head.Predict(encoder.Embed(prompt)));
        }, cancellationToken);

    public void Dispose()
    {
        if (_model.IsValueCreated) _model.Value.Encoder.Dispose();
    }
}
