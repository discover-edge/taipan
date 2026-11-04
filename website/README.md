# Taipan's website

The documentation site, built with [Astro Starlight](https://starlight.astro.build/) and published to GitHub Pages from `main` by `.github/workflows/pages.yml`.

```sh
npm ci
npm run dev      # a local server, with the generated pages
npm run build    # the site, in dist/
```

- **Pages** are Markdown in `src/content/docs/`, one directory per sidebar section. Link between them with relative links (`../optionals/`), so they work under any base path.
- **Generated pages:** `scripts/generate.mjs` writes `reference/specification.md` from `SPEC.md` and a page per diagnostic code under `diagnostics/`, from SPEC §9 and the golden cases. They aren't committed.
- **Code samples are tests.** A ` ```taipan ` block followed by an ` ```output ` or ` ```error ` block is run by `dotnet test` (`tests/Taipan.Tests/WebsiteSampleTests.cs`), which compares its stdout and stderr. A titled block with nothing after it (` ```taipan title="shapes.tpn" `) is a module the page's later samples can import. Any other block is shown, not run.
- **Highlighting** uses the VS Code extension's grammar, `editors/vscode/syntaxes/taipan.tmLanguage.json`.
- **The URL** is set by `site` and `base` in `astro.config.mjs`. For a custom domain, set `site` to it, `base` to `'/'`, and add `public/CNAME`.
