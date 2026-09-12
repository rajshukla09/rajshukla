using System.Text.Json.Serialization;

namespace rajshukla.Models;

public sealed record ArticleSummary(
    string Slug,
    string Title,
    string Description,
    DateOnly Published,
    [property: JsonPropertyName("type")] string ContentType,
    string? Image,
    string? ImageAlt,
    string[] Tags,
    bool Featured,
    string? SourceUrl);

public sealed record SiteProfile(
    string Name,
    string Title,
    string Intro,
    string HeroLead,
    string ProfileImage,
    string ProfileImageAlt,
    string GitHubUrl,
    string LinkedInUrl,
    string AboutHeading,
    string[] Biography);

public sealed record BookItem(
    string Title,
    string Subtitle,
    string Description,
    string? Image,
    string? ImageAlt,
    string AmazonUrl,
    string GitHubUrl,
    bool Featured);

public sealed record ProjectItem(string Title, string Description, string[] Technologies, string GitHubUrl,
    string? ArticleUrl = null, string? Screenshot = null);
