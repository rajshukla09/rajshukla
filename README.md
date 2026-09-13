# Raj Shukla — Technical Publication

The personal technical website of **Raj Shukla** — Software Architect · Generative AI Engineer · Technical Author. It is a static Blazor WebAssembly publication focused on .NET, Azure, Agentic AI, AI agents, agentic workflows, distributed systems, and production AI architecture.

## Technology

- Blazor WebAssembly on .NET 10
- Razor components and custom responsive CSS
- Repository-managed Markdown and JSON content
- No backend, database, authentication, CMS, or JavaScript framework

## Repository structure

```text
Components/                 Reusable publication components
Layout/                     Shared site header and footer
Models/                     Static content models
Pages/                      Routable Blazor pages
Services/ContentService.cs  Content loading and Markdown rendering
wwwroot/content/site/       Global profile, biography, and social links
wwwroot/content/books/      Book metadata and companion repository links
wwwroot/content/projects/   Independent project metadata
wwwroot/content/articles/   Per-article metadata and Markdown
wwwroot/images/profile/     Optional profile image
wwwroot/images/books/       Book covers
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

## Update profile and site details

Edit `wwwroot/content/site/profile.json` to update Raj's name, professional title, homepage introduction, biography, profile image, GitHub URL, or LinkedIn URL. Store an optional profile image in `wwwroot/images/profile/` and set `profileImage` to its site-relative path. Leave `profileImage` empty to use the intentional image-free layout.

## Update a book

Edit the relevant entry in `wwwroot/content/books/books.json`. Each entry contains its title, subtitle, description, optional image and alt text, Amazon URL, companion GitHub repository, and featured status. Store book covers in `wwwroot/images/books/`; leaving `image` empty displays the designed typographic fallback.

Book companion repositories belong in each book's `githubUrl`. They are not independent projects and should not be added to the Projects data.

## Add an article or technical note

1. Create `wwwroot/content/articles/<slug>/`.
2. Add the article body as `content.md` inside that folder.
3. Add an `article.json` file in the same folder with `slug`, `title`, `description`, `published`, `type`, `image`, `imageAlt`, `tags`, `featured`, and `sourceUrl` fields.
4. Add the slug to `wwwroot/content/articles/articles-index.json`.

Set `type` to either `Article` or `Technical Note`. Dates use `YYYY-MM-DD`. `image`, `imageAlt`, and `sourceUrl` are optional and may be empty strings; `featured` is a boolean. Image paths should be site-relative, for example `/images/articles/my-article.webp`. Store article images in `wwwroot/images/articles/`.

The built-in renderer supports headings, paragraphs, unordered lists, block quotes, fenced code blocks, links, inline code, bold, and emphasis. Content is trusted because it is owned in this repository; do not use it to render untrusted user input.

## Add a project

Add a genuine independent project object to `wwwroot/content/projects/projects.json`. A project supports `title`, `description`, `technologies`, `githubUrl`, and optional `articleUrl` and `screenshot` fields. Store screenshots in `wwwroot/images/projects/`. Keep the file as `[]` when there are no independent projects; the Projects page provides a deliberate empty state.

Do not add book companion repositories here. Their links are maintained with their corresponding entries in `wwwroot/content/books/books.json`.

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
