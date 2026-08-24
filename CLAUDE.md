# DiSky Atlas

Blazor Server site: the DiSky v5 syntax reference (generated from `wwwroot/data/atlas.json`)
plus hand-written documentation pages in `Docs/`.

## Writing documentation pages

- Follow `Docs/contributing/style-guide.md` for every page under `Docs/` (structure, wording,
  code conventions, referencing). `Docs/contributing/{writing-pages,components,linking}.md`
  describe the markdown format itself.
- Recommend only the imperative bot API (`a new discord bot` + `login`); never the
  `define bot` structure. Pair every `login` with a `shutdown`.
- Look up syntax/event ids in `wwwroot/data/atlas.json` before writing a reference. Anchor
  format: `bot#token`, `core#effect-login-bot`, `events#bot-ready-event` (entity/`event-`
  prefix stripped).
- Source of truth for DiSky behaviour: the plugin code in `C:\Users\simulateur\Documents\DiSky5`.

## Running locally

`dotnet` on PATH is SDK 9; the project targets .NET 10. Use
`~/.dotnet/dotnet.exe run --no-launch-profile --urls http://127.0.0.1:5987`. In Development
mode, the first request to a `/docs/...` page logs `Docs lint [...]` warnings for broken
references: fetch the page with curl and read the console to validate a new page.
