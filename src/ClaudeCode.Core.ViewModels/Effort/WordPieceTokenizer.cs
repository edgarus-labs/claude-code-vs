using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace ClaudeCode.Core.Effort;

/// <summary>
/// The BERT uncased WordPiece tokenizer all-MiniLM-L6-v2 was trained with (Hugging Face
/// <c>BertNormalizer</c> + <c>BertPreTokenizer</c> + <c>WordPiece</c>), written out here instead of
/// taking a tokenizer package whose netstandard2.0 dependency graph (System.Text.Json 9 and friends)
/// would have to coexist with Visual Studio's own copies inside devenv.exe.
/// </summary>
public sealed class WordPieceTokenizer
{
    /// <summary>The model's tokenizer.json truncates to 128 tokens, [CLS] and [SEP] included.</summary>
    public const int MaxSequenceLength = 128;

    private const int MaxCharsPerWord = 100;
    private readonly Dictionary<string, long> _vocab;
    private readonly long _cls;
    private readonly long _sep;
    private readonly long _unk;

    private WordPieceTokenizer(Dictionary<string, long> vocab)
    {
        _vocab = vocab;
        _cls = Id("[CLS]");
        _sep = Id("[SEP]");
        _unk = Id("[UNK]");
    }

    public static WordPieceTokenizer Load(string vocabPath)
    {
        var vocab = new Dictionary<string, long>(StringComparer.Ordinal);
        long id = 0;
        foreach (var line in File.ReadLines(vocabPath))
            vocab[line] = id++;
        return new WordPieceTokenizer(vocab);
    }

    private long Id(string token) =>
        _vocab.TryGetValue(token, out var id) ? id : throw new InvalidDataException($"Vocabulary has no {token} token.");

    /// <summary>[CLS] + WordPiece ids + [SEP], truncated to <see cref="MaxSequenceLength"/>.</summary>
    public IReadOnlyList<long> Encode(string text)
    {
        var ids = new List<long> { _cls };
        foreach (var word in PreTokenize(Normalize(text)))
        {
            if (ids.Count >= MaxSequenceLength - 1) break;
            AppendWordPieces(word, ids);
        }
        if (ids.Count > MaxSequenceLength - 1) ids.RemoveRange(MaxSequenceLength - 1, ids.Count - (MaxSequenceLength - 1));
        ids.Add(_sep);
        return ids;
    }

    // BertNormalizer(clean_text, handle_chinese_chars, strip_accents, lowercase).
    private static string Normalize(string text)
    {
        var cleaned = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c == 0 || c == 0xFFFD || IsControl(c)) continue;
            if (IsWhitespace(c)) cleaned.Append(' ');
            else if (IsChinese(c)) cleaned.Append(' ').Append(c).Append(' ');
            else cleaned.Append(c);
        }

        var decomposed = cleaned.ToString().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var stripped = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) stripped.Append(c);
        return stripped.ToString();
    }

    // BertPreTokenizer: split on whitespace, and every punctuation character is a word of its own.
    private static IEnumerable<string> PreTokenize(string text)
    {
        var word = new StringBuilder();
        foreach (var c in text)
        {
            if (c == ' ' || IsPunctuation(c))
            {
                if (word.Length > 0) { yield return word.ToString(); word.Clear(); }
                if (c != ' ') yield return c.ToString();
            }
            else
            {
                word.Append(c);
            }
        }
        if (word.Length > 0) yield return word.ToString();
    }

    // Greedy longest-match-first; a word with any unmatchable remainder becomes one [UNK].
    private void AppendWordPieces(string word, List<long> ids)
    {
        if (word.Length > MaxCharsPerWord) { ids.Add(_unk); return; }
        int wordStart = ids.Count;
        int start = 0;
        while (start < word.Length)
        {
            long match = -1;
            int end = word.Length;
            for (; end > start; end--)
            {
                var piece = start == 0 ? word.Substring(0, end) : "##" + word.Substring(start, end - start);
                if (_vocab.TryGetValue(piece, out match)) break;
                match = -1;
            }
            if (match < 0)
            {
                ids.RemoveRange(wordStart, ids.Count - wordStart);
                ids.Add(_unk);
                return;
            }
            ids.Add(match);
            start = end;
        }
    }

    private static bool IsWhitespace(char c) =>
        c == ' ' || c == '\t' || c == '\n' || c == '\r' || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator;

    private static bool IsControl(char c)
    {
        if (c == '\t' || c == '\n' || c == '\r') return false;
        var category = CharUnicodeInfo.GetUnicodeCategory(c);
        return category == UnicodeCategory.Control || category == UnicodeCategory.Format;
    }

    private static bool IsPunctuation(char c)
    {
        if ((c >= 33 && c <= 47) || (c >= 58 && c <= 64) || (c >= 91 && c <= 96) || (c >= 123 && c <= 126)) return true;
        switch (CharUnicodeInfo.GetUnicodeCategory(c))
        {
            case UnicodeCategory.ConnectorPunctuation:
            case UnicodeCategory.DashPunctuation:
            case UnicodeCategory.OpenPunctuation:
            case UnicodeCategory.ClosePunctuation:
            case UnicodeCategory.InitialQuotePunctuation:
            case UnicodeCategory.FinalQuotePunctuation:
            case UnicodeCategory.OtherPunctuation:
                return true;
            default:
                return false;
        }
    }

    // CJK Unified Ideographs (BMP blocks; supplementary-plane ideographs arrive as surrogates and
    // are left to WordPiece, which maps them to [UNK] exactly as a vocabulary miss).
    private static bool IsChinese(char c) =>
        (c >= 0x4E00 && c <= 0x9FFF) || (c >= 0x3400 && c <= 0x4DBF) || (c >= 0xF900 && c <= 0xFAFF);
}
