---
title: Linking
icon: link
description: Referencing syntaxes, entities, events and other doc pages.
tags: [reference, links]
order: 4
---

## The reference format

References mirror the site's URLs: open any syntax on its atlas page, click its link
button, and paste the path without the origin:

| You want                | You write                     | Lands on                        |
| ----------------------- | ----------------------------- | ------------------------------- |
| A syntax on an entity   | `guild#members`               | `/guild#members`                |
| A Core / Global syntax  | `core/effects#effect-await`   | `/core/effects#effect-await`    |
| An event                | `events#message-receive`      | `/events#message-receive`       |
| An entity page          | `guild`                       | `/guild`                        |
| Another doc page        | `contributing/setup`          | `/docs/contributing/setup`      |

## Syntax cards

A standalone `syntax:` line renders a live card of the syntax: kind, pattern, description
and meta, all clickable:

syntax: guild#members

Readers pick how much detail these cards show in **Settings → Documentation**; you can
force a level per card when it matters, with a trailing `compact`, `standard` or `full`:

syntax: member#nickname compact

syntax: core/effects#effect-await full

Events and entities get cards the same way:

event: events#message-receive

entity: member

## Doc-to-doc cards

doc: contributing/writing-pages

## Inline links

Reference anything mid-sentence with a scheme link: the
[members property](syntax:guild#members) of a guild, the
[message receive event](event:events#message-receive), the
[Member entity](entity:member), or the [setup guide](doc:contributing/setup).

Hovering an inline `syntax:` / `entity:` / `event:` link previews the element's reference
card without leaving the page; try it on the links above.

Relative links between pages just work with plain file paths: [Components](components.md)
or with a heading fragment, [the admonition list](components.md#admonitions).

## What links back

Every reference you make is indexed both ways:

- The syntax's record shows a **mentioned in** chip pointing to your page (deep-linked to
  the nearest heading above the mention).
- Pages listed in your frontmatter `syntaxes:` get the stronger *"Feeling lost?"* guide
  banner on their records instead.
- Entity pages list every page that touches them in a **Documentation** section.

## When a reference breaks

Typos never crash a page; they degrade into a visible warning card and are listed at
startup (and on `/docs` in development mode). This one is intentionally broken:

??? bug "A deliberately broken reference (click to see the degraded card)"
    syntax: guild#this-does-not-exist
