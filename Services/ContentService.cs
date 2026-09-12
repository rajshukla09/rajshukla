using System.Net.Http.Json;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using rajshukla.Models;

namespace rajshukla.Services;

public sealed partial class ContentService(HttpClient http)
{
    private IReadOnlyList<ArticleSummary>? _articles;

    public async Task<IReadOnlyList<ArticleSummary>> GetArticlesAsync() =>
        _articles ??= (await http.GetFromJsonAsync<List<ArticleSummary>>("content/articles.json") ?? [])
            .OrderByDescending(article => article.Published).ToList();

    public async Task<ArticleSummary?> GetArticleAsync(string slug) =>
        (await GetArticlesAsync()).FirstOrDefault(article => string.Equals(article.Slug, slug, StringComparison.OrdinalIgnoreCase));

    public async Task<string> GetRenderedMarkdownAsync(ArticleSummary article) =>
        RenderMarkdown(await http.GetStringAsync($"content/articles/{article.MarkdownFile}"));

    public async Task<IReadOnlyList<ProjectItem>> GetProjectsAsync() =>
        await http.GetFromJsonAsync<List<ProjectItem>>("content/projects.json") ?? [];

    private static string RenderMarkdown(string markdown)
    {
        var html = new StringBuilder();
        var paragraph = new List<string>();
        var inCode = false;
        var inList = false;
        var code = new StringBuilder();

        void FlushParagraph() { if (paragraph.Count == 0) return; html.Append("<p>").Append(Inline(string.Join(' ', paragraph))).AppendLine("</p>"); paragraph.Clear(); }
        void CloseList() { if (!inList) return; html.AppendLine("</ul>"); inList = false; }

        foreach (var rawLine in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (line.StartsWith("```"))
            {
                FlushParagraph(); CloseList();
                if (inCode) { html.Append("<pre><code>").Append(HtmlEncoder.Default.Encode(code.ToString().TrimEnd())).AppendLine("</code></pre>"); code.Clear(); }
                inCode = !inCode; continue;
            }
            if (inCode) { code.AppendLine(rawLine); continue; }
            if (string.IsNullOrWhiteSpace(line)) { FlushParagraph(); CloseList(); continue; }
            var heading = HeadingRegex().Match(line);
            if (heading.Success)
            {
                FlushParagraph(); CloseList(); var level = heading.Groups[1].Length;
                html.Append($"<h{level}>").Append(Inline(heading.Groups[2].Value)).AppendLine($"</h{level}>");
            }
            else if (line.StartsWith("> ")) { FlushParagraph(); CloseList(); html.Append("<blockquote><p>").Append(Inline(line[2..])).AppendLine("</p></blockquote>"); }
            else if (line.StartsWith("- ")) { FlushParagraph(); if (!inList) { html.AppendLine("<ul>"); inList = true; } html.Append("<li>").Append(Inline(line[2..])).AppendLine("</li>"); }
            else paragraph.Add(line.Trim());
        }
        FlushParagraph(); CloseList(); return html.ToString();
    }

    private static string Inline(string value)
    {
        var encoded = HtmlEncoder.Default.Encode(value);
        encoded = LinkRegex().Replace(encoded, "<a href=\"$2\">$1</a>");
        encoded = CodeRegex().Replace(encoded, "<code>$1</code>");
        encoded = StrongRegex().Replace(encoded, "<strong>$1</strong>");
        return EmphasisRegex().Replace(encoded, "<em>$1</em>");
    }

    [GeneratedRegex("^(#{1,4})\\s+(.+)$")] private static partial Regex HeadingRegex();
    [GeneratedRegex("\\[([^]]+)\\]\\((https?://[^)]+)\\)")] private static partial Regex LinkRegex();
    [GeneratedRegex("`([^`]+)`")] private static partial Regex CodeRegex();
    [GeneratedRegex("\\*\\*([^*]+)\\*\\*")] private static partial Regex StrongRegex();
    [GeneratedRegex("(?<!\\*)\\*([^*]+)\\*(?!\\*)")] private static partial Regex EmphasisRegex();
}
