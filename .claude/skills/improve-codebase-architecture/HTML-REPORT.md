# HTML Report Format

> Source: [mattpocock/skills](https://github.com/mattpocock/skills) `skills/engineering/improve-codebase-architecture/HTML-REPORT.md` at
> `a496f55`, MIT, © 2026 Matt Pocock, see [THIRD-PARTY-NOTICES](../THIRD-PARTY-NOTICES.md). Hearthpulse changes: the
> Tailwind and Mermaid CDN scripts are removed; the scaffold uses inline CSS and the diagrams are hand-built boxes and
> inline SVG, so the report loads nothing from the network.

The architectural review is rendered as a single self-contained HTML file in the OS temp directory. No scripts, no
external stylesheets or fonts: a small inline `<style>` block, hand-built `<div>` boxes and inline SVG.

## Scaffold

```html
<!doctype html>
<html lang="en">
  <head>
    <meta charset="utf-8" />
    <title>Architecture review for {{repo name}}</title>
    <style>
      body { margin: 0; background: #fafaf9; color: #0f172a; font: 15px/1.5 system-ui, sans-serif; }
      main { max-width: 64rem; margin: 0 auto; padding: 3rem 1.5rem; }
      article { border: 1px solid #e2e8f0; border-radius: 8px; background: #fff; padding: 1.25rem; margin: 2.5rem 0; }
      .files { font: 13px ui-monospace, monospace; }
      .pair { display: grid; grid-template-columns: 1fr 1fr; gap: 1rem; }
      .diagram { position: relative; height: 320px; border: 1px solid #e2e8f0; border-radius: 8px; padding: .75rem; }
      .label { font-size: 11px; text-transform: uppercase; letter-spacing: .08em; }
      .module { border: 1px solid #334155; border-radius: 4px; padding: .25rem .5rem; }
      .deep { background: linear-gradient(135deg, #0f172a, #1e293b); color: #f8fafc; border-width: 3px; }
      .seam { stroke-dasharray: 4 4; }
      .leak { stroke: #dc2626; }
      .badge { display: inline-block; border-radius: 999px; padding: 0 .6rem; font-size: 12px; }
      .strong { background: #d1fae5; } .explore { background: #fef3c7; } .speculative { background: #e2e8f0; }
      .adr { background: #fffbeb; border-left: 4px solid #f59e0b; padding: .25rem .75rem; }
    </style>
  </head>
  <body>
    <main>
      <header>...</header>
      <section id="candidates">...</section>
      <section id="top-recommendation">...</section>
    </main>
  </body>
</html>
```

## Header

Repo name, date, and a compact legend: solid box = module, dashed line = seam, red arrow = leakage, thick dark box = deep module. No introduction paragraph. Straight into the candidates.

## Candidate card

The diagrams carry the weight. Prose is sparse, plain, and uses the glossary terms (from the `/codebase-design` skill) without ceremony.

Each candidate is one `<article>`:

- **Title**: short, names the deepening (e.g. "Collapse the Order intake pipeline").
- **Badge row**: recommendation strength (`Strong` = `.strong`, `Worth exploring` = `.explore`, `Speculative` = `.speculative`), plus a tag for the dependency category (`in-process`, `local-substitutable`, `ports & adapters`, `mock`).
- **Files**: monospaced list (`.files`).
- **Before / After diagram**: the centrepiece. Two columns side by side (`.pair`). See patterns below.
- **Problem**: one sentence. What hurts.
- **Solution**: one sentence. What changes.
- **Wins**: bullets, ≤6 words each. e.g. "Tests hit one interface", "Pricing logic stops leaking", "Delete 4 shallow wrappers".
- **ADR callout** (if applicable): one line in an `.adr` box.

No paragraphs of explanation. If the diagram needs a paragraph to be understood, redraw the diagram.

## Diagram patterns

Pick the pattern that fits the candidate. Mix them. Don't make every diagram look the same. Variety is part of the point.

### Boxes and arrows (the workhorse for dependencies / call flow)

Modules as `.module` `<div>`s with labels, arrows as an inline SVG `<line>` or `<path>` over a `position: relative`
container. Colour leakage edges with `.leak`, draw seams with `.seam`, and make the "after" a single `.deep` box with
the now-internal modules greyed out inside it. For "before: 6 round-trips; after: 1", draw two lifelines and the
calls between them as horizontal arrows.

### Cross-section (good for layered shallowness)

Stack horizontal bands (`height: 3rem; border-left: 4px solid`) to show layers a call passes through. Before: 6 thin layers each doing nothing. After: 1 thick band labelled with the consolidated responsibility.

### Mass diagram (good for "interface as wide as implementation")

Two rectangles per module: one for interface surface area, one for implementation. Before: interface rectangle is nearly as tall as the implementation rectangle (shallow). After: interface rectangle is short, implementation rectangle is tall (deep).

### Call-graph collapse

Before: a tree of function calls rendered as nested boxes. After: the same tree collapsed into one box, with the now-internal calls shown faded inside it.

## Style guidance

- Lean editorial, not corporate-dashboard. Generous whitespace. A serif for headings is optional.
- Colour sparingly: one accent (emerald or indigo) plus red for leakage and amber for warnings.
- Keep diagrams ~320px tall so before/after sits comfortably side by side without scrolling.
- Use `.label` for module labels inside diagrams, so they read as schematic, not as UI.
- The report is static: no scripts at all, nothing fetched from the network.

## Top recommendation section

One larger card. Candidate name, one sentence on why, anchor link to its card. That's it.

## Tone

Plain English, concise, but the architectural nouns and verbs come straight from the `/codebase-design` skill. Concision is not an excuse to drift.

**Use exactly:** module, interface, implementation, depth, deep, shallow, seam, adapter, leverage, locality.

**Never substitute:** component, service, unit (for module) · API, signature (for interface) · boundary (for seam) · layer, wrapper (for module, when you mean module).

**Phrasings that fit the style:**

- "Order intake module is shallow: interface nearly matches the implementation."
- "Pricing leaks across the seam."
- "Deepen: one interface, one place to test."
- "Two adapters justify the seam: HTTP in prod, in-memory in tests."

**Wins bullets** name the gain in glossary terms: *"locality: bugs concentrate in one module"*, *"leverage: one interface, N call sites"*, *"interface shrinks; implementation absorbs the wrappers"*. Don't write *"easier to maintain"* or *"cleaner code"*, because those terms aren't in the glossary and don't earn their place.

No hedging, no throat-clearing, no "it's worth noting that…". If a sentence could be a bullet, make it a bullet. If a bullet could be cut, cut it. If a term isn't in the `/codebase-design` glossary, reach for one that is before inventing a new one.
