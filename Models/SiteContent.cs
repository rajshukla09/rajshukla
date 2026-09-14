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
    string? SourceUrl,
    string? Book = null,
    int? SourceChapter = null);

public sealed record SiteProfile(
    string Name,
    string Title,
    string Intro,
    string ProfileImage,
    string ProfileImageAlt,
    string GitHubUrl,
    string LinkedInUrl,
    string[] Biography);

public sealed record BookItem(
    string Slug,
    string Title,
    string Subtitle,
    string Description,
    string? Image,
    string? ImageAlt,
    string AmazonUrl,
    string GitHubUrl,
    bool Featured,
    BookChapter[] Chapters);

public sealed record BookChapter(int Number, string Title);
