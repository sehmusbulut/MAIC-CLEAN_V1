using System.IO.Compression;
using System.Text;
using System.Xml;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using MAIC.Application.Knowledge;
using MAIC.Domain.Knowledge;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace MAIC.Infrastructure.Knowledge;

/// <summary>
/// Extracts plain text from UTF-8 text/markdown, PDF and HTML. Scripts, styles and other non-content elements are
/// dropped from HTML; block elements become line breaks. Output is bounded; the chunker normalizes it afterwards.
/// </summary>
public sealed class DocumentTextExtractor : IDocumentTextExtractor
{
    public const int MaxPdfPages = 2000;
    // Bounds DOM size before AngleSharp builds a tree; a legitimate page stays far below this.
    public const int MaxHtmlTags = 100_000;
    // Raw extraction bound before normalization collapses whitespace.
    private const int MaxRawCharacters = KnowledgeChunker.MaxTextLength * 4;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    // Fetched pages may declare legacy code pages such as windows-1252 or iso-8859-9.
    static DocumentTextExtractor() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
        { "script", "style", "noscript", "template", "svg", "math", "iframe", "object", "embed", "canvas", "head", "select", "button" };
    private static readonly HashSet<string> Blocks = new(StringComparer.OrdinalIgnoreCase)
    {
        "address", "article", "aside", "blockquote", "br", "dd", "details", "div", "dl", "dt", "fieldset", "figcaption", "figure",
        "footer", "form", "h1", "h2", "h3", "h4", "h5", "h6", "header", "hr", "li", "main", "nav", "ol", "p", "pre", "section",
        "summary", "table", "tbody", "td", "tfoot", "th", "thead", "tr", "ul", "title"
    };

    /// <summary>In-process extraction. The Worker runs <see cref="Extract"/> in an isolated child process instead.</summary>
    public Task<string> ExtractAsync(string contentType, string? charset, byte[] content, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Extract(contentType, charset, content));
    }

    public string Extract(string contentType, string? charset, byte[] content) => contentType switch
    {
        KnowledgeDocument.TextPlain or KnowledgeDocument.TextMarkdown => DecodeText(content, charset),
        KnowledgeDocument.ApplicationPdf => ExtractPdf(content),
        KnowledgeDocument.ApplicationDocx => ExtractDocx(content),
        KnowledgeDocument.TextHtml or "application/xhtml+xml" => ExtractHtml(DecodeText(content, charset, lenient: true)),
        _ => throw new IngestionException(IngestionFailure.UNSUPPORTED_CONTENT, false)
    };

    private static string DecodeText(byte[] content, string? charset, bool lenient = false)
    {
        var encoding = ResolveEncoding(charset);
        try
        {
            // Uploads are validated as strict UTF-8; fetched pages with a declared charset use it.
            var text = encoding is UTF8Encoding && !lenient ? StrictUtf8.GetString(content) : encoding.GetString(content);
            return text.Length > MaxRawCharacters ? throw new IngestionException(IngestionFailure.CONTENT_TOO_LARGE, false) : text;
        }
        catch (DecoderFallbackException error)
        {
            throw new IngestionException(IngestionFailure.INVALID_CONTENT, false, error);
        }
    }

    private static Encoding ResolveEncoding(string? charset)
    {
        if (string.IsNullOrWhiteSpace(charset)) return Encoding.UTF8;
        try
        {
            return Encoding.GetEncoding(charset.Trim().Trim('"'));
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
    }

    private static string ExtractPdf(byte[] content)
    {
        try
        {
            using var pdf = PdfDocument.Open(content);
            if (pdf.NumberOfPages > MaxPdfPages) throw new IngestionException(IngestionFailure.CONTENT_TOO_LARGE, false);
            var builder = new StringBuilder();
            foreach (var page in pdf.GetPages())
            {
                builder.Append(ContentOrderTextExtractor.GetText(page)).Append("\n\n");
                if (builder.Length > MaxRawCharacters) throw new IngestionException(IngestionFailure.CONTENT_TOO_LARGE, false);
            }
            return builder.ToString();
        }
        catch (IngestionException)
        {
            throw;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Corrupt, encrypted or unsupported PDFs are permanent content errors.
            throw new IngestionException(IngestionFailure.INVALID_CONTENT, false, error);
        }
    }

    private static string ExtractDocx(byte[] content)
    {
        try
        {
            using var memory = new MemoryStream(content, writable: false);
            using var archive = new ZipArchive(memory, ZipArchiveMode.Read, leaveOpen: false);
            var entry = archive.GetEntry("word/document.xml")
                ?? throw new IngestionException(IngestionFailure.INVALID_CONTENT, false);
            using var stream = entry.Open();
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxRawCharacters * 2L
            });
            var builder = new StringBuilder();
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    if (reader.LocalName is "p" or "br" or "tr") builder.Append('\n');
                    else if (reader.LocalName == "tab") builder.Append(' ');
                }
                else if (reader.NodeType is XmlNodeType.Text or XmlNodeType.SignificantWhitespace)
                {
                    builder.Append(reader.Value);
                    if (builder.Length > MaxRawCharacters)
                        throw new IngestionException(IngestionFailure.CONTENT_TOO_LARGE, false);
                }
            }
            return builder.ToString();
        }
        catch (IngestionException)
        {
            throw;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            throw new IngestionException(IngestionFailure.INVALID_CONTENT, false, error);
        }
    }

    private static string ExtractHtml(string html)
    {
        if (html.AsSpan().Count('<') > MaxHtmlTags) throw new IngestionException(IngestionFailure.CONTENT_TOO_LARGE, false);
        var document = new HtmlParser().ParseDocument(html);
        var builder = new StringBuilder();
        var title = document.Title;
        if (!string.IsNullOrWhiteSpace(title)) builder.Append(title).Append("\n\n");
        if (document.Body is null) return builder.ToString();

        // Iterative walk: deeply nested markup cannot exhaust the stack.
        var stack = new Stack<(INode Node, bool Closing)>();
        stack.Push((document.Body, false));
        while (stack.Count > 0)
        {
            var (node, closing) = stack.Pop();
            if (node is IElement element)
            {
                if (closing)
                {
                    if (Blocks.Contains(element.LocalName)) builder.Append('\n');
                    continue;
                }
                if (Skipped.Contains(element.LocalName) || element.HasAttribute("hidden")) continue;
                if (Blocks.Contains(element.LocalName)) builder.Append('\n');
                stack.Push((node, true));
                for (var child = node.LastChild; child is not null; child = child.PreviousSibling) stack.Push((child, false));
            }
            else if (node.NodeType == NodeType.Text)
            {
                builder.Append(node.TextContent);
                if (builder.Length > MaxRawCharacters) throw new IngestionException(IngestionFailure.CONTENT_TOO_LARGE, false);
            }
        }
        return builder.ToString();
    }
}
