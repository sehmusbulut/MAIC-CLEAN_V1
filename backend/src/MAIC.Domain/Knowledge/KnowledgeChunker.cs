using System.Text;

namespace MAIC.Domain.Knowledge;

public sealed record KnowledgeChunkSpan(int Sequence, int StartOffset, string Content);

/// <summary>
/// Deterministic text normalization and chunking. Chunks prefer paragraph, line, sentence and word boundaries, never
/// split a surrogate pair, and overlap the previous chunk by up to <see cref="Overlap"/> units so context survives
/// boundaries. The same input always produces the same chunks.
/// </summary>
public static class KnowledgeChunker
{
    public const int MaxChunkLength = 2000;
    public const int Overlap = 200;
    public const int MaxTextLength = 1_000_000;

    /// <summary>
    /// Unifies line endings to LF, removes NUL/controls (tab and line feed become spaces/lines), replaces unpaired
    /// surrogates with U+FFFD, collapses horizontal whitespace runs, trims lines and keeps at most one blank line.
    /// Output stops one unit past <see cref="MaxTextLength"/>, so a longer result means the content is too large.
    /// </summary>
    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder(Math.Min(text.Length, MaxTextLength + 1));
        var pendingSpace = false;
        var newlines = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                c = '\n';
            }
            if (c is '\n' or '\u2028' or '\u2029' or '\f' or '\v' or '\u0085')
            {
                pendingSpace = false;
                if (builder.Length > 0) newlines++;
                continue;
            }
            if (char.IsWhiteSpace(c) || char.IsControl(c) || c == '\ufeff')
            {
                if (builder.Length > 0 && newlines == 0 && char.IsWhiteSpace(c)) pendingSpace = true;
                continue;
            }
            if (newlines > 0)
            {
                builder.Append(newlines > 1 ? "\n\n" : "\n");
                newlines = 0;
            }
            else if (pendingSpace) builder.Append(' ');
            pendingSpace = false;

            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                builder.Append(c).Append(text[++i]);
            }
            else builder.Append(char.IsSurrogate(c) ? '\ufffd' : c);
            if (builder.Length > MaxTextLength) break;
        }
        return builder.ToString();
    }

    /// <summary>Splits normalized text into bounded, nonblank chunks with sequences starting at 1.</summary>
    public static IReadOnlyList<KnowledgeChunkSpan> Split(string normalized)
    {
        ArgumentNullException.ThrowIfNull(normalized);
        var chunks = new List<KnowledgeChunkSpan>();
        var position = 0;
        while (position < normalized.Length)
        {
            while (position < normalized.Length && char.IsWhiteSpace(normalized[position])) position++;
            if (position >= normalized.Length) break;

            var end = Math.Min(position + MaxChunkLength, normalized.Length);
            if (end < normalized.Length) end = FindBreak(normalized, position, end);
            var trimmedEnd = end;
            while (trimmedEnd > position && char.IsWhiteSpace(normalized[trimmedEnd - 1])) trimmedEnd--;
            chunks.Add(new(chunks.Count + 1, position, normalized[position..trimmedEnd]));
            if (end >= normalized.Length) break;

            // Start the next chunk inside the overlap window at a word start, but always make progress.
            var next = Math.Max(end - Overlap, position + 1);
            var space = normalized.IndexOfAny([' ', '\n'], next, end - next);
            next = space >= 0 ? space + 1 : end;
            if (next < normalized.Length && char.IsLowSurrogate(normalized[next])) next++;
            position = next;
        }
        return chunks;
    }

    private static int FindBreak(string text, int start, int limit)
    {
        var floor = start + MaxChunkLength / 2;
        var window = text.AsSpan(floor, limit - floor);
        var paragraph = window.LastIndexOf("\n\n");
        if (paragraph >= 0) return floor + paragraph + 2;
        var line = window.LastIndexOf('\n');
        if (line >= 0) return floor + line + 1;
        var sentence = -1;
        foreach (var mark in (ReadOnlySpan<string>)[". ", "? ", "! ", "; "]) sentence = Math.Max(sentence, window.LastIndexOf(mark));
        if (sentence >= 0) return floor + sentence + 2;
        var word = window.LastIndexOf(' ');
        if (word >= 0) return floor + word + 1;
        // No boundary: hard split, but never between the halves of a surrogate pair.
        return char.IsLowSurrogate(text[limit]) ? limit - 1 : limit;
    }
}
