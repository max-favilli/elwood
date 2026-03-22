# Elwood Playground — Specification

## Overview

A browser-based interactive environment for writing, testing, and sharing Elwood expressions. Runs entirely client-side using the `@elwood-lang/core` TypeScript package. Hosted on GitHub Pages — no server, no backend, no cost.

**Live:** https://max-favilli.github.io/elwood/

**Phase 1c:** JSON-only playground with full language support ✅
**Phase 2+:** Expand to support multi-format input/output (CSV, XML, XLSX, Text)

---

## Tech Stack

| Concern | Choice | Why |
|---|---|---|
| Build tool | **Vite** | Industry standard, fast, native TS support |
| UI framework | **React** (with Vite, NOT Next.js) | Single-page client-side app, no SSR needed |
| Editor | **Monaco Editor** via `@monaco-editor/react` | VS Code engine, syntax highlighting, autocomplete |
| Styling | **Tailwind CSS** | Utility-first, fast iteration, dark mode built-in |
| URL sharing | **lz-string** (static import) | Compression for encoding playground state in URLs |
| Markdown | **react-markdown** | Renders explanation files in example gallery |
| Engine | **@elwood-lang/core** | Workspace dependency from `../ts/` |

---

## Architecture

```
GitHub Pages (static hosting, free)
    │
    └── dist/                    (output of: cd playground && npm run build)
        ├── index.html
        ├── assets/
        │   ├── app-[hash].js    (React app + Elwood engine, bundled by Vite)
        │   └── app-[hash].css   (Tailwind output)
        └── favicon.svg          ({|} logo)
```

Everything runs in the browser. No data leaves the user's machine (except when explicitly sharing via URL hash).

---

## UI Layout

Dark theme, VS Code-inspired developer tool aesthetic. Expression on top (compact), input and output side by side below.

### Overall structure

```
┌─────────────────────────────────────────────────────────────────────┐
│ {elwood} | Examples ▶Run Share | 📄Docs 🐙GitHub                   │
├─────────────────────────────────────────────────────────────────────┤
│ Expression                                              [Clear]     │
│ ┌─────────────────────────────────────────────────────────────────┐ │
│ │ $.users[*] | where u => u.active | select u => u.name          │ │
│ └─────────────────────────────────────────────────────────────────┘ │
│ ═══════════════════ drag to resize ═══════════════════════════════  │
├──────────────────────────────┬──────────────────────────────────────┤
│ Input JSON                   │ Output                ✓ 1.8ms [Copy]│
│ ┌──────────────────────────┐ │ ┌────────────────────────────────┐  │
│ │ {                        │ │ │ [                              │  │
│ │   "users": [             │ │ │   "Alice",                     │  │
│ │     { "name": "Alice",   │ │ │   "Charlie",                   │  │
│ │       "age": 30 },       │ │ │   "Diana"                      │  │
│ │     ...                  │ │ │ ]                              │  │
│ │   ]                      │ │ │                                │  │
│ │ }                        │ │ │                                │  │
│ │                          │ │ │                                │  │
│ │ [Load File] [Format]     │ │ │                                │  │
│ └──────────────────────────┘ │ └────────────────────────────────┘  │
│                              │ ┌ Error (conditional) ────────────┐ │
│                              │ │ Line 1, Col 38: Unknown 'wher' │ │
│                              │ │ Did you mean 'where'?          │ │
│                              │ └────────────────────────────────┘  │
├──────────────────────────────┴──────────────────────────────────────┤
│ ✓ Ready   Input: 412 B   Output: 298 B                   v0.1.0   │
└─────────────────────────────────────────────────────────────────────┘
```

### Toolbar

```
{elwood}  |  Examples  |  ▶ Run  🔗 Share  |                📄 Docs  🐙 GitHub
```

- **`{elwood}`** — logo, monospace font
- **Examples** — opens the example gallery sidebar (slides in from left)
- **Run** — primary button (blue), evaluates the expression. Also auto-evaluates on keystroke with 300ms debounce.
- **Share** — generates a shareable URL, opens share modal with copy button (with share icon)
- **Docs** — links to syntax reference (with document icon), right-aligned
- **GitHub** — links to repository (with GitHub icon), right-aligned

### Expression panel (top, full width)

- Monaco Editor with custom Elwood language definition (syntax highlighting, autocomplete)
- Supports both single expressions and multi-line scripts (`let` / `return`)
- **Default height: ~3 lines** (88px) — compact for one-liners
- **Resizable:** draggable handle below the panel, highlights blue on hover
- Auto-expands when loading multi-line examples from gallery
- "Clear" button in panel header
- Keyboard shortcut: Ctrl+Enter / Cmd+Enter to run

### Input panel (bottom-left)

- Monaco Editor in JSON mode
- **"Load File"** button — opens native file picker (`.json`), reads via FileReader API (stays local)
- **"Format"** button — pretty-prints the JSON
- **Drag-and-drop** — drop a `.json` file onto the panel, visual feedback with blue ring
- Default content: sample users JSON
- Warning if loaded file > 5MB

### Output panel (bottom-right)

- Monaco Editor in JSON mode, read-only
- Pretty-printed JSON result, updates live (300ms debounce)
- **Evaluation time** shown in panel header (e.g., "✓ 1.8ms")
- **"Copy"** button in panel header — copies output to clipboard

### Error panel (below output, conditional)

- Appears when evaluation fails
- Shows error messages with line/column and "Did you mean?" suggestions
- Styled with orange text on dark red background
- Disappears when expression is fixed

### Status bar (bottom)

- Blue background (#007acc), white text
- Status indicator (✓ Ready or ✕ Error)
- Input/output byte sizes
- Evaluation time
- Elwood version (right-aligned)

---

## React Component Structure

```
<App>
  <Toolbar logo onExamples onRun onShare />
  <main>
    <ExpressionPanel>
      <PanelHeader title="Expression" actions={[Clear]} />
      <MonacoEditor language="elwood" />
    </ExpressionPanel>
    <ResizeHandle />
    <DataPanels>
      <InputPanel>
        <PanelHeader title="Input JSON" actions={[LoadFile, Format]} />
        <MonacoEditor language="json" />
      </InputPanel>
      <OutputPanel>
        <PanelHeader title="Output" evalTime="1.8ms" actions={[Copy]} />
        <MonacoEditor language="json" readOnly />
        <ErrorPanel error={error} />
      </OutputPanel>
    </DataPanels>
  </main>
  <StatusBar />
  <GallerySidebar />
  <ShareModal />
  <ExplanationModal />
</App>
```

---

## Example Gallery

All 68 spec test cases available as preloaded examples, generated at build time from `spec/test-cases/`.

### Gallery UI — slide-out sidebar (left)

- Slides in from left on clicking "Examples"
- Backdrop overlay to close
- **Search** — filters by name, category, or ID as you type
- **Grouped by category** — 17 categories (Basics, Filtering, Strings, Joins, etc.)
- **Script preview** — first meaningful line shown below each title (skips lone `{` lines)
- **"ℹ Info" button** — opens full-screen explanation modal with rendered markdown
- **Benchmark examples** marked with `[CLI]` badge (not loadable in browser)
- Clicking an example loads expression + input and auto-runs evaluation
- Auto-expands expression panel for multi-line scripts

### Explanation Modal

- Full-screen modal (inset 32px from edges)
- Renders explanation markdown with proper styling (headings, code blocks, tables, lists)
- "Load Example" button to load into editors
- Scrollable for long explanations

### Build-time generation

```bash
npm run build:examples
# Reads spec/test-cases/*/
# Generates playground/src/data/gallery.json
# Contains: id, title, category, script, input, expected, description, explanation, isBenchmark
```

Category mapping is manually curated in `scripts/build-examples.ts`.

---

## Monaco Elwood Language Definition

Custom `elwood` language registered with Monaco (`src/editor/elwood-language.ts`).

### Syntax highlighting (Monarch tokenizer)

- **Keywords:** `let`, `return`, `if`, `then`, `else`, `true`, `false`, `null`, `memo`, `match`, `from`, `asc`, `desc`
- **Pipe operators:** highlighted after `|` — `where`, `select`, `orderBy`, `groupBy`, `join`, etc.
- **Built-in methods:** `toLower`, `replace`, `hash`, `dateFormat`, etc.
- **Operators:** `==`, `!=`, `>=`, `<=`, `=>`, `&&`, `||`, `...`
- **Special:** `$` / `$.` / `$..` path navigation
- **Strings:** double-quoted, single-quoted, backtick interpolation with `{expr}`
- **Comments:** `//` single-line, `/* */` multi-line

### Autocomplete (CompletionItemProvider)

Triggers on:
- `|` + space → pipe operators with snippet placeholders (e.g., `where ${1:x} => ${2:condition}`)
- `.` → 40+ method completions with documentation
- Identifier start → keywords (`let`, `if`, `memo`, `range`, `now`, etc.)

### Language configuration

- Bracket matching, auto-closing pairs, comment toggling

---

## Sharing

### URL encoding (lz-string)

Uses static import of `lz-string` for compression. Expression + input JSON are compressed and encoded in the URL hash:

```
https://max-favilli.github.io/elwood/#data=eJxLzs8ry0xR...
```

### Share modal

- Modal with URL input field + Copy button
- Fallback to `document.execCommand('copy')` for non-HTTPS contexts
- "✓ Copied" feedback on copy

### Loading shared links

On page load, check URL hash for `#data=...` → decompress and populate editors.

---

## File Loading

- **Load File button** — native file picker (`<input type="file" accept=".json">`)
- **Drag-and-drop** — input panel accepts dropped files, blue ring visual feedback
- **Large file warning** — alert if > 5MB
- File never leaves the browser

---

## Project Structure

```
playground/
├── index.html
├── src/
│   ├── App.tsx                    # root component (all state + layout)
│   ├── main.tsx                   # React entry point
│   ├── index.css                  # Tailwind imports
│   ├── components/
│   │   ├── Toolbar.tsx            # logo, nav, actions, icons
│   │   ├── PanelHeader.tsx        # reusable panel header + buttons
│   │   ├── ErrorPanel.tsx         # error display
│   │   ├── StatusBar.tsx          # bottom status bar
│   │   ├── GallerySidebar.tsx     # example gallery slide-out
│   │   ├── ExplanationModal.tsx   # full-screen markdown explanation
│   │   └── ShareModal.tsx         # share URL modal
│   ├── hooks/
│   │   └── useElwood.ts           # debounced evaluation via @elwood-lang/core
│   ├── editor/
│   │   └── elwood-language.ts     # Monarch tokenizer + autocomplete + language config
│   └── data/
│       └── gallery.json           # generated from spec/test-cases/
├── scripts/
│   └── build-examples.ts          # reads spec/test-cases/, generates gallery.json
├── public/
│   └── favicon.svg                # {|} logo
├── logo-concepts/                 # logo SVG source files
├── mockup-v2.html                 # approved design mockup
├── package.json
├── tsconfig.json
├── tailwind.config.js
├── postcss.config.js
└── vite.config.ts                 # base: '/elwood/' for GitHub Pages
```

### Dependencies

**Runtime:**
- `react`, `react-dom`
- `@monaco-editor/react`
- `@elwood-lang/core` (workspace: `../ts/`)
- `lz-string`
- `react-markdown`

**Dev:**
- `vite`, `@vitejs/plugin-react`
- `typescript`
- `tailwindcss`, `postcss`, `autoprefixer`
- `tsx` (for build scripts)

### Scripts

```json
{
  "dev": "vite",
  "build:examples": "tsx scripts/build-examples.ts",
  "build": "tsx scripts/build-examples.ts && tsc -b && vite build",
  "preview": "vite preview"
}
```

---

## GitHub Actions Deployment

```yaml
# .github/workflows/deploy-playground.yml
name: Deploy Playground
on:
  push:
    branches: [main]
    paths: ['playground/**', 'ts/**', 'spec/**']
  workflow_dispatch:
```

Builds TS engine → builds playground → deploys to GitHub Pages via `actions/deploy-pages@v4`.

Triggers on changes to playground code, TS engine, or spec test cases. Manual trigger also available.

---

## Design Tokens (Tailwind custom theme)

```javascript
colors: {
  'bg-primary': '#1e1e1e',
  'bg-secondary': '#252526',
  'bg-tertiary': '#2d2d30',
  'bg-hover': '#3e3e42',
  'bg-active': '#094771',
  'border': '#3e3e42',
  'text-primary': '#cccccc',
  'text-secondary': '#969696',
  'text-muted': '#5a5a5a',
  'accent': '#007acc',
  'error': '#f48747',
  'error-bg': '#3a1d1d',
  'success': '#89d185',
}
```

Font: `'JetBrains Mono', Consolas, 'Courier New', monospace` for editors and logo. System font stack for UI.

---

## Logo

- **Full logo:** `{elwood}` — monospace font, rendered as text in toolbar
- **Favicon:** `{|}` — SVG at `public/favicon.svg`
- **Source files:** `logo-concepts/concept11-all-black.svg` (logo), `concept12-favicon-pipe.svg` (favicon)

---

## Phase 2+ Expansion — Multi-Format Support

When Phase 2 format converters are complete, the playground expands:

### Input format selector
Dropdown in input panel header: JSON (default), CSV, XML, Text.

### Dual-tab views
- Input: **Raw** tab (original format) + **As JSON** tab (converted)
- Output: **JSON** tab (result) + **[Format]** tab (converted output)

---

## Open Questions

1. **Mobile support** — Monaco doesn't work well on mobile. Show a read-only mode or skip mobile for v1?
2. **XLSX in browser** — Phase 2 needs SheetJS (~300KB). Worth bundling or skip XLSX in playground?
3. **Gist sharing** — Currently only URL-hash sharing. Add Gist for large payloads later?
