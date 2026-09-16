using System.Net;
using System.Text;
using System.Text.Json;

const string SiteUrl = "https://www.rajshukla.dev";
const string HomeTitle = "Raj Shukla — Software Architect · AI Engineer · Technical Author";
const string HomeDescription = "Software architect, AI engineer, and technical author focused on Agentic AI, AI agents, .NET, Azure, RAG, and dependable production AI systems.";

if (args.Length != 1)
    throw new ArgumentException("Usage: SeoGenerator <published-wwwroot>");

var root = Path.GetFullPath(args[0]);
var indexPath = Path.Combine(root, "index.html");
var template = await File.ReadAllTextAsync(indexPath, Encoding.UTF8);
var pages = new List<SeoPage>
{
    new("/", HomeTitle, HomeDescription, "website", "/images/profile/profile.jpg"),
    new("/articles", "Articles — Raj Shukla", "Writing by Raj Shukla on Agentic AI, AI agents, agentic systems, .NET, Azure, and production AI architecture.", "website", null),
    new("/books", "Books — Raj Shukla", "Books by Raj Shukla on building production-ready AI agents and advanced agentic workflows with .NET.", "website", null),
    new("/about", "About Raj Shukla", "Software architect, AI engineer, and technical author building dependable Agentic AI systems with .NET and Azure.", "website", "/images/profile/profile.jpg")
};

var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
var articlesRoot = Path.Combine(root, "content", "articles");
var slugs = JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(Path.Combine(articlesRoot, "articles-index.json")), jsonOptions) ?? [];
foreach (var slug in slugs)
{
    var article = JsonSerializer.Deserialize<Article>(await File.ReadAllTextAsync(Path.Combine(articlesRoot, slug, "article.json")), jsonOptions)
        ?? throw new InvalidDataException($"Invalid article metadata: {slug}");
    pages.Add(new($"/articles/{article.Slug}", $"{article.Title} | Raj Shukla", article.Description, "article", article.Image));
}

var books = JsonSerializer.Deserialize<Book[]>(await File.ReadAllTextAsync(Path.Combine(root, "content", "books", "books.json")), jsonOptions) ?? [];
pages.AddRange(books.Select(book => new SeoPage($"/books/{book.Slug}", $"{book.Title} | Raj Shukla", book.Description, "website", book.Image)));

foreach (var page in pages)
{
    var html = ReplaceSeo(template, page);
    var destination = page.Path == "/" ? indexPath : Path.Combine(root, page.Path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar), "index.html");
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    await File.WriteAllTextAsync(destination, html, new UTF8Encoding(false));
}

Console.WriteLine($"Generated crawler-visible metadata for {pages.Count} routes.");

static string ReplaceSeo(string template, SeoPage page)
{
    var markerStart = "    <!-- seo:start -->";
    var markerEnd = "    <!-- seo:end -->";
    var start = template.IndexOf(markerStart, StringComparison.Ordinal);
    var end = template.IndexOf(markerEnd, StringComparison.Ordinal);
    if (start < 0 || end < start) throw new InvalidDataException("SEO markers were not found in index.html.");
    end += markerEnd.Length;

    var canonical = page.Path == "/" ? $"{SiteUrl}/" : $"{SiteUrl}{page.Path}";
    var title = WebUtility.HtmlEncode(page.Title);
    var description = WebUtility.HtmlEncode(page.Description);
    var tags = new StringBuilder()
        .AppendLine(markerStart)
        .AppendLine($"    <title>{title}</title>")
        .AppendLine($"    <meta name=\"description\" content=\"{description}\" />")
        .AppendLine($"    <link rel=\"canonical\" href=\"{canonical}\" />")
        .AppendLine($"    <meta property=\"og:type\" content=\"{page.Type}\" />")
        .AppendLine($"    <meta property=\"og:title\" content=\"{title}\" />")
        .AppendLine($"    <meta property=\"og:description\" content=\"{description}\" />")
        .AppendLine($"    <meta property=\"og:url\" content=\"{canonical}\" />")
        .AppendLine("    <meta property=\"og:site_name\" content=\"Raj Shukla\" />");
    if (!string.IsNullOrWhiteSpace(page.Image)) tags.AppendLine($"    <meta property=\"og:image\" content=\"{SiteUrl}{page.Image}\" />");
    tags.AppendLine($"    <meta name=\"twitter:card\" content=\"{(string.IsNullOrWhiteSpace(page.Image) ? "summary" : "summary_large_image")}\" />")
        .AppendLine($"    <meta name=\"twitter:title\" content=\"{title}\" />")
        .AppendLine($"    <meta name=\"twitter:description\" content=\"{description}\" />");
    if (!string.IsNullOrWhiteSpace(page.Image)) tags.AppendLine($"    <meta name=\"twitter:image\" content=\"{SiteUrl}{page.Image}\" />");
    tags.Append(markerEnd);
    return template[..start] + tags + template[end..];
}

sealed record SeoPage(string Path, string Title, string Description, string Type, string? Image);
sealed record Article(string Slug, string Title, string Description, string? Image);
sealed record Book(string Slug, string Title, string Description, string? Image);
