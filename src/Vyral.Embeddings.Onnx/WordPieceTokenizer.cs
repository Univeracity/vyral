using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Vyral.Embeddings.Onnx;

internal sealed class WordPieceTokenizer
{
    private readonly Dictionary<string, long> _vocabulary;
    private readonly bool _lowercase;
    private readonly long _clsId;
    private readonly long _sepId;
    private readonly long _unkId;
    private readonly long _padId;

    public WordPieceTokenizer(string vocabPath, bool lowercase = true)
    {
        if (string.IsNullOrWhiteSpace(vocabPath))
        {
            throw new InvalidOperationException("Tokenizer vocabulary path is required.");
        }

        if (!File.Exists(vocabPath))
        {
            throw new FileNotFoundException($"Tokenizer vocabulary file was not found: {vocabPath}", vocabPath);
        }

        _lowercase = lowercase;
        _vocabulary = File.ReadLines(vocabPath)
            .Select((token, index) => (Token: token.Trim(), Index: index))
            .Where(item => item.Token.Length > 0)
            .ToDictionary(item => item.Token, item => (long)item.Index, StringComparer.Ordinal);

        _clsId = RequireToken("[CLS]");
        _sepId = RequireToken("[SEP]");
        _unkId = RequireToken("[UNK]");
        _padId = _vocabulary.TryGetValue("[PAD]", out var padId) ? padId : 0;
    }

    public long PadId => _padId;

    public long[] Encode(string text, int maxTokens)
    {
        if (maxTokens < 2)
        {
            throw new InvalidOperationException("Tokenizer maxTokens must be at least 2.");
        }

        var tokenIds = new List<long> { _clsId };
        foreach (var token in BasicTokenize(text ?? string.Empty))
        {
            foreach (var wordPieceId in WordPieceTokenize(token))
            {
                if (tokenIds.Count >= maxTokens - 1)
                {
                    tokenIds.Add(_sepId);
                    return tokenIds.ToArray();
                }

                tokenIds.Add(wordPieceId);
            }
        }

        tokenIds.Add(_sepId);
        return tokenIds.ToArray();
    }

    /// <summary>
    /// Encodes a (first, second) pair, truncating to fit <paramref name="maxTokens"/>. By default
    /// (<paramref name="truncateFirstBeforeSecond"/> = false) <c>second</c> is removed before
    /// <c>first</c> — correct for a reranker's (query, document) pairing, where the short query
    /// should survive intact and the document is what gets cut. Pass <c>true</c> for an NLI
    /// (premise, hypothesis) pairing, where it's the opposite: the hypothesis is the short part that
    /// varies across the judgment being made and must survive, while the premise is the shared,
    /// often-long context that should absorb the cut. Using the wrong priority doesn't error — it
    /// silently truncates the varying segment to nothing, so every candidate ends up encoding the
    /// same content and the model returns identical (or near-identical) output for genuinely
    /// different inputs. That exact failure shipped once; see
    /// BUG-20260918-061159-20F269 in this repo's issue history for the full trail. If truncation
    /// would still leave either segment empty after exhausting the other, this throws rather than
    /// returning a pair that can't carry the intended judgment.
    /// </summary>
    public WordPiecePairEncoding EncodePair(string first, string second, int maxTokens, bool truncateFirstBeforeSecond = false)
    {
        if (maxTokens < 3)
        {
            throw new InvalidOperationException("Tokenizer pair maxTokens must be at least 3.");
        }

        var firstIds = TokenizeToIds(first ?? string.Empty).ToList();
        var secondIds = TokenizeToIds(second ?? string.Empty).ToList();
        var maxContentTokens = maxTokens - 3;
        var truncateFirst = truncateFirstBeforeSecond ? firstIds : secondIds;
        var truncateSecond = truncateFirstBeforeSecond ? secondIds : firstIds;

        while (firstIds.Count + secondIds.Count > maxContentTokens)
        {
            if (truncateFirst.Count > 0)
            {
                truncateFirst.RemoveAt(truncateFirst.Count - 1);
                continue;
            }

            if (truncateSecond.Count > 0)
            {
                truncateSecond.RemoveAt(truncateSecond.Count - 1);
                continue;
            }

            break;
        }

        if (firstIds.Count == 0 || secondIds.Count == 0)
        {
            throw new InvalidOperationException(
                $"ONNX tokenizer pair encoding truncated to an empty segment (first={firstIds.Count} tokens, second={secondIds.Count} tokens, budget={maxContentTokens} content tokens). Both inputs cannot fit and truncation cannot produce a meaningful pair. Increase maxTokens, shorten the input, or reconsider which segment this call prioritizes keeping — do not silently proceed with a degenerate pair.");
        }

        var inputIds = new List<long>(firstIds.Count + secondIds.Count + 3) { _clsId };
        var tokenTypeIds = new List<long>(inputIds.Capacity) { 0 };

        foreach (var id in firstIds)
        {
            inputIds.Add(id);
            tokenTypeIds.Add(0);
        }

        inputIds.Add(_sepId);
        tokenTypeIds.Add(0);

        foreach (var id in secondIds)
        {
            inputIds.Add(id);
            tokenTypeIds.Add(1);
        }

        inputIds.Add(_sepId);
        tokenTypeIds.Add(1);

        return new WordPiecePairEncoding(
            inputIds.ToArray(),
            Enumerable.Repeat(1L, inputIds.Count).ToArray(),
            tokenTypeIds.ToArray());
    }

    private long RequireToken(string token)
    {
        return _vocabulary.TryGetValue(token, out var id)
            ? id
            : throw new InvalidOperationException($"Tokenizer vocabulary must contain {token}.");
    }

    private IEnumerable<string> BasicTokenize(string text)
    {
        var normalized = _lowercase ? text.ToLowerInvariant() : text;
        var token = new StringBuilder();

        foreach (var c in normalized)
        {
            if (char.IsWhiteSpace(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Control)
            {
                foreach (var emitted in FlushToken(token))
                {
                    yield return emitted;
                }
                continue;
            }

            if (IsPunctuation(c))
            {
                foreach (var emitted in FlushToken(token))
                {
                    yield return emitted;
                }

                yield return c.ToString();
                continue;
            }

            token.Append(c);
        }

        foreach (var emitted in FlushToken(token))
        {
            yield return emitted;
        }
    }

    private IEnumerable<long> WordPieceTokenize(string token)
    {
        if (_vocabulary.TryGetValue(token, out var exactId))
        {
            yield return exactId;
            yield break;
        }

        var pieces = new List<long>();
        var start = 0;
        while (start < token.Length)
        {
            var end = token.Length;
            long? pieceId = null;
            var nextStart = start;

            while (start < end)
            {
                var piece = token[start..end];
                if (start > 0)
                {
                    piece = "##" + piece;
                }

                if (_vocabulary.TryGetValue(piece, out var id))
                {
                    pieceId = id;
                    nextStart = end;
                    break;
                }

                end--;
            }

            if (!pieceId.HasValue)
            {
                yield return _unkId;
                yield break;
            }

            pieces.Add(pieceId.Value);
            start = nextStart;
        }

        foreach (var piece in pieces)
        {
            yield return piece;
        }
    }

    private IEnumerable<long> TokenizeToIds(string text)
    {
        foreach (var token in BasicTokenize(text))
        {
            foreach (var wordPieceId in WordPieceTokenize(token))
            {
                yield return wordPieceId;
            }
        }
    }

    private static IEnumerable<string> FlushToken(StringBuilder token)
    {
        if (token.Length == 0)
        {
            yield break;
        }

        yield return token.ToString();
        token.Clear();
    }

    private static bool IsPunctuation(char c)
    {
        var category = char.GetUnicodeCategory(c);
        return category is UnicodeCategory.ConnectorPunctuation
            or UnicodeCategory.DashPunctuation
            or UnicodeCategory.OpenPunctuation
            or UnicodeCategory.ClosePunctuation
            or UnicodeCategory.InitialQuotePunctuation
            or UnicodeCategory.FinalQuotePunctuation
            or UnicodeCategory.OtherPunctuation
            or UnicodeCategory.MathSymbol
            or UnicodeCategory.CurrencySymbol
            or UnicodeCategory.ModifierSymbol
            or UnicodeCategory.OtherSymbol;
    }
}

internal sealed record WordPiecePairEncoding(long[] InputIds, long[] AttentionMask, long[] TokenTypeIds);
