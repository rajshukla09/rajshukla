# Raj Shukla — Technical Publication

The personal technical website of **Raj Shukla** — Software Architect, Generative AI Engineer, and Technical Author. It is a static Blazor WebAssembly publication focused on .NET, Azure, Generative AI, AI agents, agentic workflows, distributed systems, and production AI architecture.

## Technology

- Blazor WebAssembly on .NET 10
- Razor components and custom responsive CSS
- Repository-managed Markdown and JSON content
- No backend, database, authentication, CMS, or JavaScript framework

## Repository structure

```text
Components/                 Reusable publication components
Layout/                     Shared site header and footer
Models/                     Article and project content models
Pages/                      Routable Blazor pages
Services/ContentService.cs  Content loading and Markdown rendering
wwwroot/content/            Article metadata, Markdown, and project data
wwwroot/images/books/       Future book covers
wwwroot/images/articles/    Optional article hero images
wwwroot/images/projects/    Optional project screenshots
wwwroot/css/app.css         Site design and responsive styles
```

## Run locally

Install the .NET 10 SDK, then run:

```powershell
dotnet restore
dotnet run
```

Open the local URL printed by the development server. The launch profile uses HTTP for straightforward local development.

## Add an article or technical note

1. Add a Markdown file to `wwwroot/content/articles/`.
2. Add its metadata to `wwwroot/content/articles.json`.
3. Set `contentType` to either `Article` or `Technical Note`.

Supported metadata fields are `title`, `slug`, `description`, `published`, `tags`, `contentType`, `markdownFile`, and the optional `heroImage` and `sourceUrl`. Dates use `YYYY-MM-DD`. Hero image paths should be site-relative, for example `images/articles/my-article.webp`.

The built-in renderer supports headings, paragraphs, unordered lists, block quotes, fenced code blocks, links, inline code, bold, and emphasis. Content is trusted because it is owned in this repository; do not use it to render untrusted user input.

## Add a project

Add an object to `wwwroot/content/projects.json`. A project supports `title`, `description`, `technologies`, `githubUrl`, and optional `articleUrl` and `screenshot` fields. Store screenshots in `wwwroot/images/projects/`.

Book covers can later be added under `wwwroot/images/books/`. Until then, intentional typographic placeholders keep the Books page complete.

## Production build

```powershell
dotnet publish -c Release
```

The static site output is produced under `bin/Release/net10.0/publish/wwwroot/`.

## Deployment

For **Azure Static Web Apps**, build with the command above and deploy the publish `wwwroot` directory as the application artifact. Configure the host to fall back unknown routes to `index.html` so direct article URLs work.

For **GitHub Pages**, publish the same static output. If deploying beneath a repository subpath, update the `<base href="/">` value in `wwwroot/index.html` to the repository path (for example `/rajshukla/`) and include a SPA route fallback appropriate to the Pages workflow. A custom-domain/root deployment can retain `/`.

## Content and SEO

Each page supplies its own title, description, canonical URL, Open Graph metadata, and Twitter card metadata through `PageMetadata`. For a public production deployment, ensure the host's canonical domain and social-image URLs are configured consistently.
