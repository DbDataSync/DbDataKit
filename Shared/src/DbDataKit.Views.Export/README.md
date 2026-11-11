# DbDataKit.Views.Export

The exporters of dashboards and reports (plan 32 DBR15, DBR17): `DataExports` writes the CSV and xlsx of a rendered widget's rows (ClosedXML, no browser; see `docs/views.md`), and `IReportExporter.ExportSvgAsync` takes a chart's SVG out of the print page. The PDF exporter: one `IReportExporter` (`ChromiumExporter`) over PuppeteerSharp and one Chromium. **No Node, no download at run time, no network but the page's own files.**
The `views` module (`DbDataKit.Views.Hosting`) composes the print page and calls it; a runner task for scheduled exports (DBR18) will call the same interface.

```csharp
var exporter = new ChromiumExporter(new ExportSettings { Mode = BrowserMode.Pinned });      // or ExportSettings.Read(section, "server.yaml")
var availability = await exporter.CheckAsync();                                           // browser starts, is the pinned version, fonts match their pin
var result = await exporter.ExportPdfAsync(new PrintJob("Title", html, assets, PdfLayout.A4));   // PDF bytes, pages, browser version, warnings, embedded fonts
// var svg = await exporter.ExportSvgAsync(job, "chart-widget-id");   // the chart's <svg> as the page drew it (the visuals mark it data-testid="vz-chart-<id>")
```

- **The browser** is launched from a path, never fetched (`BrowserFetcher` is not used and a test checks the assembly does not mention it): `mode: pinned` (the Chrome for Testing build `chromium.pin` names, installed by `scripts/fetch-chromium`
  under `.chromium/<rid>/`; a browser of another version makes export unavailable, naming both versions) or `mode: system` (an installed Chrome or Edge at `path`; allowed, its version is in every result). One browser per export, a throwaway profile, flags in `ChromiumExporter`.
- **The page** is served from memory under `http://print.localhost/` by request interception: its own files (the HTML, `assets`, the bundled fonts) are answered, everything else is **aborted** and returned as a warning (`A request to ... was blocked`); the host-resolver rules refuse every name but that one as well.
  It says it is drawn with `window.__reportReady = true` (after its charts and `document.fonts.ready`), or cannot be with `window.__reportError = "words"`.
- **The PDF**: `Page.printToPDF` with the layout's paper (or, with `PreferCssPage`, the page's own `@page`), margins, header and footer templates (`pageNumber`, `totalPages`, `title`, `date` classes), background, outline and a tagged PDF; the page count and the embedded fonts are read back with PdfPig (Apache-2.0) and the page limit is checked on them.
- **Limits**: a deadline for the whole export (the browser is stopped), at most `max_concurrent` at once on the host (the next waits for the deadline, then `busy`), `max_pages`. Refusals are `ExportException` with a code: `unavailable`, `deadline`, `pages`, `page`, `busy`, `cancelled`.
- **Fonts** (`BundledFonts`): Noto Sans, Serif and Sans Mono as WOFF2 from `.fonts/` (`scripts/fetch-fonts`), declared by `@font-face` under `DbDataKit Sans`, `DbDataKit Serif` and `DbDataKit Mono` and loaded from the page's own origin, every file checked against `fonts.pin` when the set is loaded (a changed byte names the file).
  `fonts.required` lists host fonts the host promises: the check starts the browser, sets a probe in each family and asks the DevTools protocol (`CSS.getPlatformFontsForNode`) which font it used; a missing one makes export unavailable (`export unavailable: fonts: ...`).
- **Settings** (`views.export`, strict, an unknown key fails naming the file): see `ExportSettings`.

## What was verified for DBR15 (plan 32 section 14)

Run on **Chrome for Testing 153.0.8010.12** (Playwright's build in a cloud session, `mode: system`), PuppeteerSharp **25.15.0** on **net10.0**. **The pinned build (156.0.8078.47) was not downloaded or exercised here**; the job `views-export` of `shared-ci.yml` runs the same tests on it.

| Question | Result |
| --- | --- |
| PuppeteerSharp version line, licence, net10.0 | 25.15.0 is the newest on NuGet today (25.x line); MIT (its dependencies WebDriverBiDi and ReactiveExtensionsSharp are MIT too); the package has a `net10.0` asset and the exporter builds and runs on it |
| `ExecutablePath` and no download | works; no `BrowserFetcher` in the exporter's assembly; nothing is fetched at run time |
| Header and footer templates, margins, `PreferCSSPageSize` | work: "Page n of N" from `pageNumber` and `totalPages` on every page, `{title}` and `{asof}`; the paper of a layout is the PDF's page size (A4 landscape asserted). **Pitfalls (Chromium's known behaviour, not separately tested here):** a template is its own document, so it does not see the page's `@font-face` (it is set in the browser's default font) and does not inherit the page's CSS: give it `font-size` and the margin it needs |
| Outline and tagged PDF | `PdfOptions.Outline` and `Tagged` exist; the outline lists the headings (asserted with PdfPig's bookmarks) |
| Cancellation and deadline | protocol calls take no token: the exporter races them against the token and **kills the browser**; a never-ready page ends at the deadline with `deadline`, a cancelled export ends with `cancelled`, no browser process or profile folder is left (asserted) |
| Arguments with spaces | PuppeteerSharp joins `Args` into one command line: `--host-resolver-rules="MAP * ~NOTFOUND , EXCLUDE print.localhost"` must carry its own quotes, or Chrome reads the rest as extra pages (`Multiple targets are not supported in headless mode`) |
| `CSS.getPlatformFontsForNode` through `CreateCDPSessionAsync` | works: an absent family is found absent and a present one present (the missing-font test) |
| `@page` margin boxes (`@bottom-right { content: counter(page) }`) | work in Chrome 153 (checked with the browser's own `--print-to-pdf`); **not verified on the pinned 156 and not used**: the exporter uses the header and footer templates the plan decided on |
| A root user | needs `--no-sandbox` (`no_sandbox: true`, logged); on a CI runner whose kernel forbids user namespaces the same |
| The fallback own CDP client | not needed |
