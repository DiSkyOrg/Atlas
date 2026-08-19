---
title: Writing pages
icon: pencil
description: File layout, frontmatter and everything a doc page is made of.
tags: [reference, format]
order: 2
---

## File layout

Documentation lives in the repository's `Docs/` folder. The structure is intentionally simple:

```text
Docs/
  contributing/            ← a section (the folder name is its id)
    index.md               ← section metadata + landing page
    setup.md               ← a page → /docs/contributing/setup
    writing-pages.md       ← this page → /docs/contributing/writing-pages
```

- **One folder = one section.** Sections appear in the sidebar and on the `/docs` index.
- **One file = one page.** The file name becomes the URL slug, so keep names lowercase
  and dash-separated (`writing-pages.md`, not `Writing Pages.md`).
- **`index.md` is special.** Its frontmatter names the section (title, icon, order) and its
  body is the section's landing page at `/docs/<section>`.
- Root-level `.md` files are ignored (with a warning) — every page belongs to a section.

## Frontmatter

Every page starts with a frontmatter block between `---` markers:

```yaml
---
title: Working with members
icon: users
description: Everything about members.
tags: [guild, members]
syntaxes: [guild#members, member#nickname]
order: 2
hidden: false
---
```

| Key           | Required | What it does                                                                     |
| ------------- | -------- | -------------------------------------------------------------------------------- |
| `title`       | yes      | Page title — shown in the header, sidebar, search and cards.                     |
| `icon`        | no       | A [Lucide](https://lucide.dev/icons/) icon code name (defaults to `book-open`).  |
| `description` | no       | One sentence shown under the title, in search and on cards.                      |
| `tags`        | no       | Chips shown in the page header; also searchable via `Ctrl+K`.                    |
| `syntaxes`    | no       | Atlas refs this page is *the* guide for — see below.                             |
| `order`       | no       | Sort position within the section (lower first; unordered pages sort last).       |
| `hidden`      | no       | `true` keeps the page routable but out of nav, search and prev/next.             |

!!! tip "The `syntaxes:` key is a superpower"
    Every syntax listed there gets a *"Feeling lost? This is covered in the guide"* banner on
    its atlas record, pointing back to your page — and your page ends with a
    **Related syntaxes** section showing a card per entry. One line, two-way linking.

## What happens automatically

- **Search** — every page is searchable in the `Ctrl+K` palette
  (title, tags, description and headings all match).
- **Table of contents** — your `##` headings become jump chips at the top of the page,
  and every heading gets a hover copy-link anchor.
- **Prev / next** — pages chain in section order for continuous reading.
- **Backlinks** — any syntax you reference (cards or inline links) gains a
  *"mentioned in"* chip pointing back to your page, next to the exact section that mentions it.
- **Validation** — broken references and links are reported at startup and in a banner on
  `/docs` when the site runs in development mode. A broken reference never crashes a page:
  it renders as a visible warning card instead.

## Editing workflow

Run the site locally and just edit the markdown: in development mode the docs reload on
every save — refresh the browser to see the change. No build step, no cache to clear.

## Images

Put doc images in `wwwroot/assets/docs/` and reference them site-absolute:

```markdown
![The settings dialog](/assets/docs/settings-dialog.png)
```

Relative image paths are flagged by the lint pass, so everything stays in one predictable place.
