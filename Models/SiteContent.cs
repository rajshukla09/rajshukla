namespace rajshukla.Models;

public sealed record ArticleSummary(string Title, string Slug, string Description, DateOnly Published,
    string[] Tags, string ContentType, string MarkdownFile, string? HeroImage = null, string? SourceUrl = null);

public sealed record ProjectItem(string Title, string Description, string[] Technologies, string GitHubUrl,
    string? ArticleUrl = null, string? Screenshot = null);
