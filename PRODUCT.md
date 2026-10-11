# Product

<!-- impeccable:product-schema 1 -->

## Platform

web

## Stack

React 19 + TypeScript + Vite single-page app, built into the ASP.NET Core host's `wwwroot` and served from the same origin as the API. Plain CSS with custom properties (no utility framework, no component kit). Fonts self-hosted through `@fontsource` packages.

## Users

Staff of one small or mid-sized Turkish wholesale business (hardware and electrical supplies in the demo data), each signed in with one or more of seven roles:

- **Satış temsilcisi / satış yöneticisi** — at an office desk, on the phone with a customer, building an order and needing to know right now whether stock can be promised.
- **Satın alma uzmanı** — at an office desk, raising purchase orders and following what is still outstanding from a supplier.
- **Depo görevlisi / depo sorumlusu** — standing at a counter PC or a shared terminal near the loading door, gloves off for a minute, a delivery note in one hand; keys in what actually arrived or left. Often interrupted mid-entry.
- **Destek uzmanı / destek yöneticisi** — answers "how do I…" and "what does this error mean" tickets from colleagues and maintains the knowledge base.
- **Sistem yöneticisi** — manages users, roles, warehouse grants and master data; does not read commercial documents by default.

The UI language is Turkish. Users are fluent in the business and in spreadsheet-style tools; they are not looking for a tour.

## Product Purpose

Kantar is a bounded order, purchasing, stock and application-support system for a single business with several warehouses. It exists so that every unit of stock is accounted for: what was ordered, what is reserved, what left, what arrived, what was cancelled. Success means a user can read the state of a document and its quantities at a glance, perform the next permitted step without doubt about what it will do, and trust that a retried or duplicated action did not double-count anything.

## Positioning

Quantities always balance and the screen shows it: every document displays the identity `ordered = fulfilled + cancelled + open` and every stock figure is backed by an immutable movement journal. Support answers come only from knowledge-base sources the asking user is entitled to read, and each answer shows the exact document, version and section it rests on.

## Operating Context

- Documents: satış siparişi → sevkiyat; satın alma siparişi → mal kabul; depo transferi; stok düzeltmesi; destek talebi; bilgi bankası makalesi.
- Paper travels alongside the screen: supplier delivery notes (irsaliye no) and dispatch references are typed in as business references.
- Partial fulfilment is normal, not an exception: partial shipments, partial receipts, cancelling only the remainder.
- Warehouse counter PCs are commonly 1366–1440 px wide; office desktops 1440 px and up; a phone or small tablet (360–768 px) is used for quick stock look-ups and for confirming a receipt on the floor.
- Lighting is bright office or warehouse daylight/fluorescent; screens are read at arm's length and sometimes standing.
- Sessions are shared-terminal sensitive: users log out and another colleague logs in on the same machine.

## Capabilities and Constraints

- Integer quantities, one unit per product, prices in TRY. No tax, discount, invoicing, payment, returns, lot/serial tracking.
- Permissions combine role, per-warehouse grant level (View / Operate / Supervise) and document ownership. Out-of-scope documents respond as not found.
- Mutating document actions use optimistic concurrency (a stale copy is rejected with 412) and idempotency keys.
- The assistance feature ("Kaynaklı Yardım") answers only from published, permitted knowledge-base sections; it never reads or changes ERP data. Saving an answer to a ticket is a separate, explicit, user-confirmed action. The default answer provider is a deterministic demo provider that is **not** a language model and must be labelled as such.
- Print-friendly order summary exists but is explicitly not a legal invoice or delivery note.
- All data in the product and in design artefacts is synthetic.

## Brand Commitments

- Name: **Kantar** (a weighbridge/scale): precision, balance, nothing unaccounted for.
- Must not resemble the author's other products (Loca, Kulvar, Halka, LST); IBM Plex is excluded because Kulvar uses it.
- No generic AI aesthetic: no purple gradients, glow, card walls, luxury cream + serif, brochure hero. The product is never turned into a chat screen.
- Not every screen opens with the same KPI cards; screens are organised by task.

## Evidence on Hand

- Domain rules, state machines and the permission matrix: `docs/02-domain-and-state-machines.md`.
- API contracts and error codes the UI must surface: `docs/05-api-contracts.md`.
- Assistance flow and its answer statuses: `docs/10-ai-grounding-and-access-design.md`.
- Static design prototype with synthetic data: `design/preview.html`.
- No real customer data, logos, photography, testimonials or usage metrics exist; none may be invented.

## Product Principles

1. **Show the balance.** Wherever quantities appear, the accounting identity is visible and adds up on screen.
2. **State before action.** A document says what state it is in and what the next permitted step is before it offers a button.
3. **Never a false success.** A result is shown only after the server confirmed it; rejected, stale and unknown outcomes each have their own honest state.
4. **Provenance over fluency.** An assistance answer is worth what its cited sources are worth; sources, version and provider label are part of the answer, not a footnote.
5. **Dense, not crowded.** Many rows and figures per screen, with hierarchy doing the work that whitespace does on a marketing page.

## Accessibility & Inclusion

Target WCAG 2.2 AA. Full keyboard operation of every document flow; visible focus; status never conveyed by colour alone; form errors summarised and linked to fields; async results announced; reduced-motion respected; text scales to 200 % without loss of content; usable at 360 px width. Automated axe checks are run but do not replace a manual screen-reader pass, which has not been performed.
