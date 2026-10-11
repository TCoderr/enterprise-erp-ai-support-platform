---
name: Kantar
description: Depo raf adres sistemi — boyalı çelik mavisi kabuk, kiriş turuncusu eylem, terazi şeridiyle dengelenen miktarlar.
colors:
  ink: "#16202B"
  ink-muted: "#4A5866"
  placeholder: "#5C6A78"
  ground: "#EEF2F5"
  panel: "#FFFFFF"
  zebra: "#F6F8FA"
  line: "#C5CED6"
  line-strong: "#7C8A97"
  rail: "#14304A"
  rail-active: "#0C2135"
  rail-text: "#E8EFF6"
  rail-text-muted: "#AEC2D4"
  accent: "#B4410A"
  accent-hover: "#933407"
  accent-text: "#A23A08"
  accent-tint: "#FCEDE2"
  link: "#1A568A"
  focus: "#0B5FCC"
  focus-on-rail: "#FFD25A"
  ok: "#17603B"
  ok-bg: "#E3F2E9"
  warn: "#6F4700"
  warn-bg: "#FFF0C7"
  danger: "#9C2219"
  danger-bg: "#FCE7E4"
  info: "#1A568A"
  info-bg: "#E2EDF7"
  neutral: "#3F4C59"
  neutral-bg: "#E7ECF0"
typography:
  title:
    fontFamily: "'Sofia Sans Variable', 'Segoe UI', system-ui, sans-serif"
    fontSize: "1.5rem"
    fontWeight: 650
    lineHeight: 1.2
  section:
    fontFamily: "'Sofia Sans Variable', 'Segoe UI', system-ui, sans-serif"
    fontSize: "1.125rem"
    fontWeight: 650
    lineHeight: 1.3
  body:
    fontFamily: "'Sofia Sans Variable', 'Segoe UI', system-ui, sans-serif"
    fontSize: "1rem"
    fontWeight: 400
    lineHeight: 1.5
  data:
    fontFamily: "'Sofia Sans Variable', 'Segoe UI', system-ui, sans-serif"
    fontSize: "0.9375rem"
    fontWeight: 400
    lineHeight: 1.35
  label:
    fontFamily: "'Sofia Sans Variable', 'Segoe UI', system-ui, sans-serif"
    fontSize: "0.8125rem"
    fontWeight: 600
    lineHeight: 1.3
  plate:
    fontFamily: "'Sofia Sans Condensed Variable', 'Arial Narrow', 'Segoe UI', sans-serif"
    fontSize: "1.375rem"
    fontWeight: 700
    lineHeight: 1.1
    letterSpacing: "0.01em"
  code:
    fontFamily: "'Sofia Sans Condensed Variable', 'Arial Narrow', 'Segoe UI', sans-serif"
    fontSize: "0.9375rem"
    fontWeight: 600
    lineHeight: 1.35
rounded:
  plate: "2px"
  panel: "4px"
spacing:
  "1": "4px"
  "2": "8px"
  "3": "12px"
  "4": "16px"
  "6": "24px"
  "8": "32px"
  "12": "48px"
components:
  button-primary:
    backgroundColor: "{colors.accent}"
    textColor: "{colors.panel}"
    rounded: "{rounded.plate}"
    padding: "8px 16px"
    height: "40px"
  button-primary-hover:
    backgroundColor: "{colors.accent-hover}"
  button-secondary:
    backgroundColor: "{colors.panel}"
    textColor: "{colors.ink}"
    rounded: "{rounded.plate}"
    padding: "8px 16px"
    height: "40px"
  status-tag:
    backgroundColor: "{colors.neutral-bg}"
    textColor: "{colors.neutral}"
    rounded: "{rounded.plate}"
    padding: "2px 8px"
  document-plate:
    backgroundColor: "{colors.rail}"
    textColor: "{colors.rail-text}"
    typography: "{typography.plate}"
    rounded: "{rounded.plate}"
    padding: "4px 10px"
---

<!-- SEED: established before implementation from the design prototype (design/preview.html); re-run /impeccable document once the React UI exists to capture the actual tokens and components. -->

# Design System: Kantar

## Overview

Kantar looks like the place its stock lives: a pallet-racking aisle. The shell is the deep painted-steel blue of rack uprights; the one action colour is the orange of the load beams; working surfaces are the cool white and galvanised grey of label plates and shelf decking. The world lends exactly four things — type, palette, density and one signature move — and nothing else. Navigation, tables, forms and buttons are the standard web ones.

**Signature move — the terazi şeridi (balance strip).** Every document shows its quantity identity as one proportional bar with the figures underneath: *sipariş = sevk/kabul + iptal + açık*. The segments always fill the bar exactly, so a document that does not add up would visibly not add up. The same strip, reduced to *eldeki = rezerve + kullanılabilir*, heads a stock balance. It is content, not decoration: it never appears without its numbers and never stands in for them.

Material character: flat, matte, ruled. No gradients, glass, glow or imagery. Motion is state feedback only (150–200 ms, ease-out, opacity/transform), disabled under `prefers-reduced-motion`.

Light theme is the committed theme: the product is read in bright warehouse and office light, at arm's length, sometimes standing. A dark theme is possible through the same token names but is not designed yet.

## Colors

Strategy: **Restrained ground, committed shell.** Colour does five jobs and no others: the shell rail, the primary action / current selection, state, the balance strip's segments, and focus.

| Role | Token | Hex | Contrast |
| --- | --- | --- | --- |
| Text | `ink` | `#16202B` | 16.46:1 on panel, 14.62:1 on ground |
| Secondary text | `ink-muted` | `#4A5866` | 7.29:1 on panel, 6.48:1 on ground |
| Placeholder | `placeholder` | `#5C6A78` | 5.54:1 on panel |
| App ground | `ground` | `#EEF2F5` | – |
| Panel / table | `panel` | `#FFFFFF` | – |
| Zebra row | `zebra` | `#F6F8FA` | – |
| Hairline | `line` | `#C5CED6` | decorative separation only |
| Control border | `line-strong` | `#7C8A97` | 3.54:1 on panel, 3.14:1 on ground |
| Shell rail | `rail` | `#14304A` | text `#E8EFF6` 11.67:1, muted `#AEC2D4` 7.39:1 |
| Rail current item | `rail-active` | `#0C2135` | white 16.35:1 |
| Primary action | `accent` | `#B4410A` | white 5.67:1 |
| Primary hover/active | `accent-hover` | `#933407` | white 7.68:1 |
| Accent as text | `accent-text` | `#A23A08` | 6.68:1 on panel |
| Link | `link` | `#1A568A` | 7.66:1 on panel |
| Focus ring | `focus` | `#0B5FCC` | 5.96:1 on panel; on the rail `#FFD25A` 9.42:1 |
| Success | `ok` on `ok-bg` | `#17603B` / `#E3F2E9` | 6.54:1 |
| Warning | `warn` on `warn-bg` | `#6F4700` / `#FFF0C7` | 7.19:1 |
| Danger | `danger` on `danger-bg` | `#9C2219` / `#FCE7E4` | 6.66:1 |
| Info | `info` on `info-bg` | `#1A568A` / `#E2EDF7` | 6.45:1 |
| Neutral state | `neutral` on `neutral-bg` | `#3F4C59` / `#E7ECF0` | 7.39:1 |

Balance strip segments: fulfilled = `rail` (solid), open = `accent` (diagonal hatch), cancelled = `line-strong` (outlined, empty). Each segment differs by pattern as well as hue and is always labelled with its figure.

## Typography

One family, two widths, both from the Sofia Sans superfamily (SIL OFL 1.1) — a grotesque drawn for a city's signage and transport system, which is the register of rack-address plates and aisle signs.

- **Sofia Sans** (`@fontsource-variable/sofia-sans`) — everything: navigation, labels, body, table data, buttons.
- **Sofia Sans Condensed** (`@fontsource-variable/sofia-sans-condensed`) — only for identifiers that behave like address plates: document numbers, SKU and code cells, and the document plate in a page header.

Fixed rem scale (ratio ≈ 1.125–1.2), no fluid sizing: 0.8125 label · 0.875 meta · 0.9375 data · 1 body · 1.125 section · 1.25 subtitle · 1.5 title. Weights: 400 text, 600 labels and codes, 650–700 headings and plates.

Numbers: `font-variant-numeric: tabular-nums lining-nums` on every quantity, money and date column; quantity columns are right-aligned. Turkish text is set with `lang="tr"` so the font's localised forms and correct `i/İ`, `ı/I` casing apply; UI copy is never upper-cased with CSS (`text-transform`) for that reason.

To verify once fonts are bundled: `tnum` is active and `ğ ü ş ı İ ö ç` render from the font file, not a fallback. Fallback candidate if either fails: Source Sans 3 (`@fontsource-variable/source-sans-3`).

## Layout

- Shell: a fixed 232 px rail on the left from 1024 px up; below that the rail collapses to a top bar with a menu button that opens the same navigation as a drawer. Content column max-width 1360 px, left-aligned.
- Document pages: header (plate, title, status, flow steps) → balance strip → line table → related records → action bar. The action bar is sticky to the bottom of the viewport on narrow screens and inline at the end of the header on wide ones.
- Spacing on a 4 px base: 4, 8, 12, 16, 24, 32, 48. Tight inside a group (4–8), 16 between groups, 24–32 between regions. More space above a heading than below it.
- Tables: 40 px rows at desktop density. Below 720 px each row becomes a labelled stack (every column kept, each value preceded by its header text); nothing is dropped. Where a table must stay tabular (movement journal), it scrolls horizontally inside its own focusable region with a sticky first column.
- The assistance panel is a 380 px side column on wide screens and a full-height sheet over the page on narrow ones; it never replaces the page.

## Elevation & Depth

Flat. Regions are separated by 1 px rules and the ground/panel tone step, not by shadows. Only layers that float above the page — the navigation drawer and the assistance sheet on narrow screens, menus — carry a shadow, and it has an offset and blur (`0 8px 24px rgba(12, 33, 53, .18)`).

## Shapes

Label-plate geometry: 2 px radius on controls, tags and plates; 4 px on panels. No pills, no circles except status glyphs. Borders are 1 px; the focus ring is 2 px with a 2 px offset.

Status is a glyph + word, never colour alone. Glyphs share one 16 px grid and 1.5 px stroke: hollow circle (taslak), filled circle (onaylı), half-filled circle (kısmi), circle with check (tamamlandı), circle with slash (iptal), triangle with mark (uyarı).

## Do's and Don'ts

- Do show the balance strip with its figures wherever a document's or a balance's quantities appear.
- Do keep one primary (accent) action per view; everything else is secondary or a link.
- Do state what happened and what to do next in every error, stale-version and empty state.
- Do label the assistance provider ("Demo sağlayıcı — dil modeli değil") inside the answer, next to its sources.
- Don't open screens with rows of KPI cards; lead with the task's table or document.
- Don't wrap content in cards inside cards; use rules and headings.
- Don't use the accent for decoration, and don't introduce a second accent.
- Don't use gradients, glow, glass, emoji-as-icons, eyebrow labels above headings, or coloured side stripes on callouts.
- Don't hide columns on small screens; restack them.
- Don't turn assistance into a full-screen conversation.
