---
title: Style guide
icon: ruler
description: How a topic page is structured, worded and referenced, so every page reads the same.
tags: [reference, format, style]
order: 5
---

This guide applies to every hand-written page under `Docs/` except this *Contributing* section.
It has three levels: **must** rules keep pages consistent, **default** rules are what to do
unless you have a reason not to, and everything else is up to you. When a rule and good
judgement disagree, follow judgement and say why in the pull request.

## Page anatomy

**Must.** A topic page is built in this order:

1. Frontmatter (`title`, `icon`, `description`, `tags`, `syntaxes`, `order`).
2. An **intro** of one to three sentences: what the page covers, and what the reader is
   assumed to have already (a token, a bot, a channel…). No heading above it.
3. `##` sections only, `###` at most for sub-cases inside one section. No `#` title in the body.
4. Each section starts with **prose**, then the **canonical code example**, then details.
   Never open a section with a code block or a table.

**Default.** Sections follow the same progression, dropping what doesn't apply:

| Section | Contains |
| --- | --- |
| `## How X works` | The concept in plain words, the ordered steps if there are any, one complete example. |
| `## Options` (or a domain word: *Fields*, *Filters*…) | One table per group of options, then a fully configured example. |
| `## <Related behaviour>` | Events fired, what happens on reload, limits: one section per topic. |
| `## Managing X` | Reading, listing, deleting: short and code-first. |
| `## Errors you may see` | A `Message · Cause · Fix` table. Only if the topic has known failure modes. |

The *Starting a bot* page is the reference implementation of this layout; copy its skeleton.

**Must.** One page = one topic a reader can finish in one sitting (roughly 100 to 200 lines of
markdown). Split when a section needs its own `## Options` table.

## Voice and wording

**Must.**

- English, second person (*you*), present tense, short sentences.
- Never use em dashes; use a colon, a semicolon, parentheses or a new sentence.
- `code` for anything the reader types or DiSky prints as an identifier: properties, values,
  effects, file names, `{_variables}`. **Bold** for UI paths on Discord's side
  (**Bot → Privileged Gateway Intents**). *Italics* for console messages.
- State facts about DiSky's behaviour, not intentions or history ("V4 used to…", "we plan to…").

**Default vocabulary.** Use the left column; avoid the right one.

| Say | Not |
| --- | --- |
| bot, the bot named `"x"` | client, JDA, instance |
| log in / `login` | connect, start (except in page titles) |
| the bot's **name** | id, identifier, tag |
| property (of a bot, guild, member…) | field, attribute, setting |
| connection option / presence option | config, parameter |
| privileged intents | special permissions |
| guild | server (unless quoting Discord's UI) |
| fires (an event) | triggers, is called |
| readable error in the console | exception, stack trace |

## Code examples

**Must.**

- ` ```skript ` fences only, never inline code for multi-line snippets.
- Recommend the **imperative API only**: `a new discord bot` → `set … of {_bot}` → `login`.
  Never show or mention the `define bot` structure.
- Every `login` shown in a complete example is paired with a `shutdown` (an `on unload`
  block, or a leading `shutdown` line in a command).
- Canonical names, always the same: bot `"my-bot"`, token `"YOUR-TOKEN"` in an `options:` block
  read as `{@token}`, variable `{_bot}`, command `/startbot <text>` with `arg-1`,
  guild `event-guild`, second bot `"music"`.
- Output goes to the console in lifecycle events (`send "…" to console`); `broadcast` and
  `reply with` only where a player or a Discord channel is really the audience.

**Default.**

- The first example of a section is **complete and runnable** as pasted. Later examples may be
  fragments, but then the surrounding prose says where they go ("set before `login`").
- 15 lines or fewer per block. Comments only for what the code doesn't say
  (`# none if not loaded`), never to narrate the obvious.
- Show the minimal form first, the fully configured form last.

## Referencing the atlas

**Must.**

- Frontmatter `syntaxes:` lists only the syntaxes this page is *the* guide for (one to five).
  Anything merely mentioned is not listed there.
- Anchors follow the site's URLs: `bot#token`, `core#effect-login-bot`,
  `events#bot-ready-event`, `guild#members`. Check them in the atlas before writing them.
- The first mention of a syntax in a section is an inline link
  (`[login](syntax:core#effect-login-bot)`); later mentions in the same section are plain `code`.

**Default.**

- Cards (`syntax: … compact`) sit at the **end** of a section, two or three at most, for the
  syntaxes the section is about. `full` cards only on pages that are pure reference.
- An `entity:` card at most once per page, at the end.
- Option tables link each property in its first column and use fixed headers:
  `Property · Default · Accepted values`. Error tables use `Message (console) · Cause · Fix`.
- External links: the Discord Developer Portal and official Discord/Skript docs only.

## Components budget

**Default.** Rich components are seasoning, not structure.

| Component | Use it for | Limit |
| --- | --- | --- |
| `!!! warning` | Something Discord or DiSky will refuse (intents, phases, rate limits) | one per screen |
| `!!! danger` | Security and irreversible actions (token, deletions) | when it applies |
| `!!! tip` | Organisation advice (`options:` block, file layout) | one per page |
| `!!! info` | A behaviour worth knowing that isn't a pitfall | sparingly |
| `??? question` | Troubleshooting entries, collapsed | troubleshooting sections only |
| tables | Options, events, errors; never for prose | |
| `::: steps`, `=== tabs`, `toggle:` / `::: when` | Tutorials and pages comparing two equivalent forms | not on topic pages |

**Must.** No `!!! note` (say it in prose), no nested admonitions, no admonition as the first
element of a section.

## Before you submit

- [ ] Intro says what the page covers and what the reader already has.
- [ ] Every section opens with prose, and its first example runs as pasted.
- [ ] Only the imperative API; every `login` has its `shutdown`.
- [ ] `syntaxes:` holds one to five refs; every anchor resolves (no lint warning on `/docs`).
- [ ] Canonical names (`"my-bot"`, `"YOUR-TOKEN"`, `{_bot}`), no em dashes, no `!!! note`.
- [ ] Under about 200 lines, one topic.

## Skeleton

````markdown
---
title: <Verb-ing a thing>
icon: <lucide-icon>
description: <One sentence: what, and for whom.>
tags: [<area>, <entity>]
syntaxes: [<entity>#<anchor>, core#<anchor>]
order: <n>
---

<What this page covers. What the reader is assumed to have.>

## How <thing> works

<Concept in two or three sentences, ordered steps if any.>

```skript
<complete, minimal example>
```

<What the example does, line by line where useful.>

## Options

<One sentence introducing the groups.>

| Property | Default | Accepted values |
| --- | --- | --- |
| [prop](syntax:<entity>#<anchor>) | `…` | `…` |

```skript
<fully configured example>
```

syntax: <entity>#<anchor> compact

## Errors you may see

| Message (console) | Cause | Fix |
| --- | --- | --- |
| *…* | … | … |
````
