using ClaudeCode.Core.Effort;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ClaudeCode.Core.Tests;

public sealed class EffortClassifierTests
{
    private static readonly string ModelDirectory = Path.Combine(AppContext.BaseDirectory, "Effort", "Model");

    // Reference ids from the model's own Hugging Face tokenizer (tokenizers 0.21, tokenizer.json of
    // sentence-transformers/all-MiniLM-L6-v2): uncased, accent-stripped, punctuation split, WordPiece.
    [Theory]
    [InlineData("git status", new long[] { 101, 21025, 2102, 3570, 102 })]
    [InlineData("Implement OAuth authentication!", new long[] { 101, 10408, 1051, 4887, 2705, 27280, 999, 102 })]
    [InlineData("Café naïve résumé", new long[] { 101, 7668, 15743, 13746, 102 })]
    [InlineData("ChatViewModel.RunTurnAsync() races", new long[] { 101, 11834, 8584, 5302, 9247, 1012, 2448, 22299, 3022, 6038, 2278, 1006, 1007, 3837, 102 })]
    [InlineData("日本語 text", new long[] { 101, 1864, 1876, 1950, 3793, 102 })]
    [InlineData("   ", new long[] { 101, 102 })]
    public void Tokenizer_MatchesReferenceTokenizer(string text, long[] expected)
    {
        var tokenizer = WordPieceTokenizer.Load(Path.Combine(ModelDirectory, "vocab.txt"));
        Assert.Equal(expected, tokenizer.Encode(text));
    }

    [Fact]
    public void Tokenizer_TruncatesToModelSequenceLength_KeepingSeparator()
    {
        var tokenizer = WordPieceTokenizer.Load(Path.Combine(ModelDirectory, "vocab.txt"));
        var ids = tokenizer.Encode(string.Join(" ", Enumerable.Repeat("status", 500)));
        Assert.Equal(WordPieceTokenizer.MaxSequenceLength, ids.Count);
        Assert.Equal(101, ids[0]);
        Assert.Equal(102, ids[ids.Count - 1]);
    }

    // Reference: sentence-transformers mean pooling + L2 normalisation over the same ONNX model.
    [Theory]
    [InlineData("git status", new[] { 0.00322f, -0.04797f, 0.00325f, 0.00057f })]
    [InlineData("Implement OAuth authentication", new[] { -0.06729f, 0.10744f, -0.05817f, -0.04895f })]
    public void Encoder_ProducesReferenceSentenceEmbedding(string text, float[] expectedPrefix)
    {
        using var encoder = MiniLmEncoder.Load(ModelDirectory);
        var embedding = encoder.Embed(text);
        Assert.Equal(MiniLmEncoder.Dimensions, embedding.Length);
        for (int i = 0; i < expectedPrefix.Length; i++)
            Assert.Equal(expectedPrefix[i], embedding[i], 3);
        Assert.Equal(1.0, Math.Sqrt(embedding.Sum(value => (double)value * value)), 4);
    }

    [Theory]
    [InlineData(0.90f, 0.08f, 0.02f, EffortLevel.Low)]
    [InlineData(0.05f, 0.90f, 0.05f, EffortLevel.Medium)]
    [InlineData(0.02f, 0.08f, 0.90f, EffortLevel.High)]
    // Leaning Low but not confident enough: under-effort is the costly mistake, so Medium.
    [InlineData(0.60f, 0.35f, 0.05f, EffortLevel.Medium)]
    // Split between Medium and High, or spread over all three: the safe fallback.
    [InlineData(0.10f, 0.46f, 0.44f, EffortLevel.Medium)]
    [InlineData(0.34f, 0.33f, 0.33f, EffortLevel.Medium)]
    // A High majority wins even when Low is the runner-up.
    [InlineData(0.45f, 0.00f, 0.55f, EffortLevel.High)]
    public void Policy_IsConservativeAgainstUnderEffort(float low, float medium, float high, EffortLevel expected)
    {
        Assert.Equal(expected, AutoEffortPolicy.Decide(new[] { low, medium, high }));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void Policy_NonFiniteProbabilities_FallBackToMedium(float corrupt)
    {
        Assert.Equal(EffortLevel.Medium, AutoEffortPolicy.Decide(new[] { corrupt, 0f, 0f }));
        Assert.Equal(EffortLevel.Medium, AutoEffortPolicy.Decide(new[] { 0f, 0f, corrupt }));
    }

    [Theory]
    [InlineData("git status")]
    [InlineData("git push")]
    [InlineData("run the tests")]
    [InlineData("format this file")]
    [InlineData("update the package version to 2.4.1")]
    [InlineData("rename this variable to customerId")]
    [InlineData("commit these changes with a short message")]
    [InlineData("show me the git log for the last five commits")]
    public async Task Classifier_RepresentativeMechanicalTurns_AreLow(string prompt)
    {
        using var classifier = new MiniLmEffortClassifier(ModelDirectory);
        Assert.Equal(EffortLevel.Low, await classifier.ClassifyAsync(prompt, CancellationToken.None));
    }

    [Theory]
    [InlineData("fix this failing unit test")]
    [InlineData("add validation to this endpoint")]
    [InlineData("implement this small API endpoint")]
    [InlineData("add logging around this operation")]
    [InlineData("refactor this method")]
    [InlineData("write unit tests for the date parser")]
    [InlineData("add a null check and return a 400 when the id is missing")]
    public async Task Classifier_RepresentativeNormalCodingTurns_AreMedium(string prompt)
    {
        using var classifier = new MiniLmEffortClassifier(ModelDirectory);
        Assert.Equal(EffortLevel.Medium, await classifier.ClassifyAsync(prompt, CancellationToken.None));
    }

    [Theory]
    [InlineData("implement OAuth authentication")]
    [InlineData("refactor the authentication middleware")]
    [InlineData("implement refresh-token rotation")]
    [InlineData("investigate why this integration test intermittently fails")]
    [InlineData("find the concurrency bug between these workers")]
    [InlineData("design a migration strategy to split the monolith database per service")]
    [InlineData("track down the memory leak that appears after several hours in production")]
    public async Task Classifier_RepresentativeComplexTurns_AreHigh(string prompt)
    {
        using var classifier = new MiniLmEffortClassifier(ModelDirectory);
        Assert.Equal(EffortLevel.High, await classifier.ClassifyAsync(prompt, CancellationToken.None));
    }

    // Label = required reasoning, not length: a long mechanical request stays Low, a terse hard one is High.
    [Fact]
    public async Task Classifier_DoesNotJudgeByPromptLength()
    {
        using var classifier = new MiniLmEffortClassifier(ModelDirectory);
        Assert.Equal(EffortLevel.Low, await classifier.ClassifyAsync(
            "please run git status for me and then just tell me which files are currently modified in the working tree, nothing else is needed",
            CancellationToken.None));
        Assert.Equal(EffortLevel.High, await classifier.ClassifyAsync("fix the deadlock", CancellationToken.None));
    }

    [Theory]
    [InlineData("hmm")]
    [InlineData("ok")]
    [InlineData("what do you think?")]
    public async Task Classifier_AmbiguousTurns_UseMediumFallback(string prompt)
    {
        using var classifier = new MiniLmEffortClassifier(ModelDirectory);
        Assert.Equal(EffortLevel.Medium, await classifier.ClassifyAsync(prompt, CancellationToken.None));
    }

    [Fact]
    public async Task Classifier_LoadsModelOnlyOnFirstClassification()
    {
        using var classifier = new MiniLmEffortClassifier(ModelDirectory);
        Assert.False(classifier.IsLoaded);
        await classifier.ClassifyAsync("git status", CancellationToken.None);
        Assert.True(classifier.IsLoaded);
    }

    [Fact]
    public async Task Classifier_MissingModel_FailsTheClassificationNotTheConstructor()
    {
        using var classifier = new MiniLmEffortClassifier(Path.Combine(ModelDirectory, "missing"));
        await Assert.ThrowsAnyAsync<IOException>(() => classifier.ClassifyAsync("git status", CancellationToken.None));
    }
}
