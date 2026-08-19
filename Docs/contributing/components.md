---
title: Components
icon: puzzle
description: Every rich markdown construct available in doc pages, demonstrated.
tags: [reference, format, components]
syntaxes: [guild#members, core/effects#effect-await]
order: 3
---

This page demonstrates every construct the doc renderer supports. Open its source
(`Docs/contributing/components.md`) side by side to see what the markdown looks like.

## Text basics

Regular markdown works everywhere: **bold**, *italic*, ~~strikethrough~~, `inline code`,
==highlighted==, H~2~O subscript and x^2^ superscript, plus [links](https://docs.disky.me)
(external links open in a new tab).

> Blockquotes are for quoting text — for callouts, prefer admonitions below.

Raw HTML is intentionally **not** supported: it renders as plain text. Every rich element
on this page is a real component instead.

---

That was a thematic break (`---`).

## Lists

1. Ordered lists…
2. …stay ordered,
    - and nest bullets,
    - like this.

- [x] Task lists render real checkboxes
- [ ] …and unchecked ones too

Definition lists document parameters nicely:

Term to define
:   Its definition, indented with a colon marker.

`another term`
:   Definitions can contain markdown too — *emphasis*, `code`, [links](writing-pages.md).

## Tables

| Column        | Aligned | Notes                          |
| ------------- | :-----: | ------------------------------ |
| Pipe tables   | center  | Standard markdown table syntax |
| Long content  | center  | Cells scroll on small screens  |

## Code

Skript code fences get the atlas highlighter and a copy button:

```skript
on message receive:
    if message content contains "hello":
        reply with "Hello %mention tag of event-user%!"
```

Any other language renders plain with a copy button and a language caption:

```yaml
title: Components
tags: [reference]
```

## Admonitions

The mkdocs syntax you know: `!!! kind "Optional title"`, body indented by 4 spaces.

!!! note
    A note without a custom title — the kind name is used.

!!! tip "Pro tip"
    Kinds: `note`, `tip`, `info`, `success`, `warning`, `danger`, `bug`, `example`,
    `question`, `quote` (plus the usual mkdocs aliases).

!!! warning "Watch out"
    Admonitions nest **markdown**, including code:

    ```skript
    set {_count} to member count of event-guild
    ```

!!! danger "Destructive"
    Use `danger` for anything irreversible.

??? question "Collapsed by default (click me)"
    `???` renders a collapsed block — perfect for optional details.

???+ example "Collapsible, but open by default"
    `???+` starts expanded and can be collapsed.

## Content tabs

=== "Skript"
    ```skript
    reply with "same feature, first variant"
    ```

=== "With embed"
    ```skript
    reply with last embed
    ```

=== "Notes"
    Tabs hold any markdown, not just code.

## Buttons

Links become buttons with the mkdocs-material attribute syntax:

[Back to the docs index](/docs){ .md-button }
[Primary button](writing-pages.md){ .md-button .md-button--primary }

## Numbered steps

::: steps
1. Write your page in `Docs/<section>/<page>.md`.

2. Reference syntaxes with a `syntax:` line — see [Linking](linking.md).

3. Refresh the browser: in development mode the docs reload on save.
:::

## Toggles & conditional content

Pages can carry their own interactive state — toggles with ids, and `::: when` blocks that
show or hide based on boolean expressions over them:

toggle: use-cache "Use the cache"
toggle: advanced "Show advanced notes"

::: when use-cache
```skript
set {_m} to member with id "123" in event-guild
```
:::

::: when !use-cache
```skript
retrieve member with id "123" in event-guild and store it in {_m}
```
:::

::: when advanced && use-cache
!!! info "Advanced"
    Cache lookups return instantly but can be stale; conditions mix toggles with
    `&&`, `||`, `!` and parentheses.
:::

Flip the toggles above — the code block follows. A full playground (inputs interpolated
into code) is planned on top of this same state engine.

!!! warning "Indentation rules"
    Admonition (`!!!`) and tab (`===`) bodies are **indented by 4 spaces**;
    `::: name` container bodies are **not indented** and end with a closing `:::`.

## Referencing the atlas

Covered in depth on the next page, but the star of the show belongs here too — a one-line
`syntax:` directive renders a live card:

syntax: guild#member-count

The amount of detail on these cards is yours to choose in **Settings → Documentation**.
