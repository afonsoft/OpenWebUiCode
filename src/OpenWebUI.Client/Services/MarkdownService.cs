using System.Text.RegularExpressions;
using Ganss.Xss;
using Markdig;

namespace OpenWebUI.Client.Services;

/// <summary>Renderiza Markdown em HTML sanitizado, seguro para exibição.</summary>
public static partial class MarkdownService
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    private static readonly HtmlSanitizer Sanitizer = CreateSanitizer();

    /// <summary>Converte Markdown em HTML sanitizado.</summary>
    /// <param name="markdown">Texto em Markdown.</param>
    /// <returns>HTML seguro para injeção via MarkupString.</returns>
    public static string ToHtml(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        var html = Markdown.ToHtml(markdown, Pipeline);
        return OpenLinksInNewTab(Sanitizer.Sanitize(html));
    }

    /// <summary>
    /// Links externos abrem em nova aba (comportamento do upstream): o
    /// target/rel é injetado depois do sanitize — o sanitizer não precisa
    /// liberar os atributos e nenhum esquema perigoso passa (o href já
    /// foi validado). Âncoras internas (#footnote, #heading) ficam na
    /// mesma aba.
    /// </summary>
    private static string OpenLinksInNewTab(string html) =>
        AnchorTagRegex().Replace(html, m => m.Groups[1].Value.StartsWith('#')
            ? m.Value
            : $"<a href=\"{m.Groups[1].Value}\" target=\"_blank\" rel=\"noopener noreferrer\"");

    [GeneratedRegex("<a href=\"([^\"]*)\"")]
    private static partial Regex AnchorTagRegex();

    private static HtmlSanitizer CreateSanitizer()
    {
        var sanitizer = new HtmlSanitizer();
        sanitizer.AllowedAttributes.Add("class");
        sanitizer.AllowedAttributes.Add("id");
        return sanitizer;
    }
}
