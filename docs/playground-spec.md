# Elwood Playground — Specification

## Overview

A browser-based interactive environment for writing, testing, and sharing Elwood expressions. Runs entirely client-side using the `@elwood-lang/core` TypeScript package. Hosted on GitHub Pages — no server, no backend, no cost.

**Phase 1c:** JSON-only playground with full language support
**Phase 2+:** Expand to support multi-format input/output (CSV, XML, XLSX, Text)

**Visual mockup:** See `playground/mockup.html` — open in a browser to preview the layout and interactions.

---

## Tech Stack

| Concern | Choice | Why |
|---|---|---|
| Build tool | **Vite** | Industry standard, fast, native TS support |
| UI framework | **React** (with Vite, NOT Next.js) | Single-page client-side app, no SSR needed |
| Editor | **Monaco Editor** via `@monaco-editor/react` | VS Code engine, syntax highlighting, autocomplete |
| Styling | **Tailwind CSS** | Utility-first, fast iteration, dark mode built-in |
| URL sharing | **lz-string** | Compression for encoding playground state in URLs |
| Engine | **@elwood-lang/core** | Local workspace dependency from `../ts/` |

### Why not Next.js?
Next.js is designed for server-rendered multi-page applications. The playground is a single page, fully client-side. Next.js would add routing, SSR, and API routes — none of which are needed. If a documentation site is added later, the React playground can be embedded into Next.js at that point.

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

Everything runs in the browser. No data leaves the user's machine (except when explicitly sharing via Gist).

---

## UI Layout

Dark theme, VS Code-inspired developer tool aesthetic. Three-panel layout.

### Toolbar (top)

```
┌────────────────────────────────────────────────────────────────────┐
│ {elwood}   [Examples]  [▶ Run]  |  [Share]  [Copy Output]   Docs  GitHub │
└────────────────────────────────────────────────────────────────────┘
```

- **`{elwood}`** — logo, all black, monospace (JetBrains Mono / Cascadia Code / Fira Code / Consolas)
- **Examples** — opens the example gallery sidebar
- **Run** — primary button, evaluates the expression (also auto-evaluates on keystroke with debounce)
- **Share** — generates a shareable link (modal with URL)
- **Copy Output** — copies the output JSON to clipboard
- **Docs** / **GitHub** — links (right-aligned)

### Main panels

```
┌────────────────────────────┬───────────────────────────────────────┐
│  Expression                │  Output                              │
│  ┌──────────────────────┐  │  ┌─────────────────────────────────┐ │
│  │ Monaco Editor        │  │  │ Monaco Editor (read-only)       │ │
│  │ Elwood language mode │  │  │ JSON syntax highlighting        │ │
│  │ syntax highlighting  │  │  │                                 │ │
│  │ autocomplete         │  │  │ Result updates live as you type │ │
│  │ hover docs           │  │  │                                 │ │
│  └──────────────────────┘  │  └─────────────────────────────────┘ │
│ ─ ─ ─ resize handle ─ ─ ─ │                                      │
│  Input JSON                │  ┌─────────────────────────────────┐ │
│  ┌──────────────────────┐  │  │ Error panel (when applicable)   │ │
│  │ Monaco Editor        │  │  │ Line 3, Col 12: Unknown         │ │
│  │ JSON mode            │  │  │ function 'wher'. Did you mean   │ │
│  │                      │  │  │ 'where'?                        │ │
│  │ [Load File] [Format] │  │  └─────────────────────────────────┘ │
│  └──────────────────────┘  │                                      │
└────────────────────────────┴───────────────────────────────────────┘
```

### Panel details

**Expression panel (top-left):**
- Monaco Editor with custom Elwood language definition (see below)
- Syntax highlighting for keywords, operators, strings, pipes, lambdas
- Autocomplete for all built-in functions and keywords
- Hover documentation for functions (signature + description)
- Supports both single expressions and multi-line scripts (`let` / `return`)
- Line numbers, bracket matching, error squiggles
- "Clear" button in panel header

**Input panel (bottom-left):**
- Monaco Editor in JSON mode
- "Load File" button — opens native file picker, reads via FileReader API (stays local)
- "Format" button — pretty-prints the JSON
- Drag-and-drop support — drop a `.json` file onto the panel
- Default content: `{}` (empty object)
- Warning banner if loaded file > 5MB: "Large file. For best performance with large data, use the Elwood CLI."
- Vertical resize handle between expression and input panels

**Output panel (right):**
- Monaco Editor in JSON mode, read-only
- Pretty-printed JSON result
- Updates live as user types (debounced, ~300ms after last keystroke)
- "Run" button in toolbar for manual evaluation

**Error panel (bottom-right, conditional):**
- Appears below the output when evaluation fails
- Shows Elwood's error messages with line/column
- "Did you mean?" suggestions displayed prominently
- Styled with red/orange accent border so errors are immediately visible
- Disappears when expression is fixed

### Status bar (bottom)

```
┌────────────────────────────────────────────────────────────────────┐
│ ✓ Evaluated in 1.8ms    Input: 412 B    Output: 298 B     v0.1.0 │
└────────────────────────────────────────────────────────────────────┘
```

- Evaluation time in milliseconds
- Input/output size (bytes/KB/MB)
- Success/error status indicator
- Elwood version

---

## React Component Structure

```
<App>
  <Toolbar logo="..." onExamples onRun onShare onCopy />
  <MainLayout>
    <LeftPanels>
      <ExpressionPanel>
        <PanelHeader title="Expression" actions={[Clear]} />
        <MonacoEditor language="elwood" />
      </ExpressionPanel>
      <ResizeHandle />
      <InputPanel>
        <PanelHeader title="Input JSON" actions={[LoadFile, Format]} />
        <MonacoEditor language="json" />
      </InputPanel>
    </LeftPanels>
    <RightPanel>
      <OutputPanel>
        <PanelHeader title="Output" evalTime="1.8ms" />
        <MonacoEditor language="json" readOnly />
      </OutputPanel>
      <ErrorPanel error={error} />   {/* conditional */}
    </RightPanel>
  </MainLayout>
  <StatusBar status evalTime inputSize outputSize version />
  <GallerySidebar open={...} examples={...} onSelect />
  <ShareModal open={...} url={...} />
</App>
```

### State (minimal)

```typescript
interface PlaygroundState {
  expression: string;          // Elwood expression/script
  input: string;               // Input JSON string
  output: string;              // Evaluated result (JSON string)
  error: EvaluationError | null;
  evalTimeMs: number;
  galleryOpen: boolean;
  shareModalOpen: boolean;
}
```

Use `useState` or `useReducer` — no state library needed. State is trivially simple.

---

## Example Gallery

All spec test cases available as preloaded examples. Generated at build time from `spec/test-cases/`.

### Gallery UI — slide-out sidebar (left)

```
┌─ Examples ──────────────────────────────────┐
│ [x]                                  Close  │
│ ┌─────────────────────────────────────────┐ │
│ │ 🔍 Search...                            │ │
│ └─────────────────────────────────────────┘ │
│                                             │
│ BASICS                                      │
│   Property Access                           │
│   Arithmetic                                │
│   Boolean Logic                             │
│   String Interpolation                      │
│                                             │
│ FILTERING & SELECTION                       │
│   Where Filter                              │
│   Select / Projection                       │
│   SelectMany (Flatten)                      │
│   Complex Where                             │
│                                             │
│ AGGREGATION                                 │
│   Count                                     │
│   Sum                                       │
│   Min / Max                                 │
│   GroupBy + Count                            │
│   Distinct                                  │
│                                             │
│ ORDERING & PAGINATION                       │
│   OrderBy                                   │
│   OrderBy (Multi-Key)                       │
│   Range / Skip / Take                       │
│   Batch                                     │
│                                             │
│ STRING OPERATIONS                           │
│   String Methods                            │
│   Case & Trim                               │
│   Regex                                     │
│   URL Encode / Decode                       │
│                                             │
│ DATA TRANSFORMATION                         │
│   Join                                      │
│   Join Modes (Left/Right/Full)              │
│   Reduce                                    │
│   Spread Operator                           │
│   Clone / Keep / Remove                     │
│                                             │
│ CONTROL FLOW                                │
│   If / Then / Else                          │
│   Pattern Match                             │
│   Let Bindings (Scripts)                    │
│   Memo Functions                            │
│                                             │
│ DATETIME & CRYPTO                           │
│   Date Format                               │
│   Date Arithmetic                           │
│   Hash (MD5/SHA)                            │
│   RSA Sign                                  │
│                                             │
│ BENCHMARKS (CLI ONLY)                       │
│   Large Array Pipeline (100K)    [CLI]      │
│   Short-Circuit (take 1 of 100K) [CLI]      │
└─────────────────────────────────────────────┘
```

- Clicking an example loads its expression and input into the editors
- Searchable — filters examples by name as you type
- Benchmark examples show a message explaining why they can't run in the browser, with CLI command to try
- Categories are manually curated in a `categories.json` file for a polished experience

### Build-time generation

```bash
npm run build-examples
# Reads spec/test-cases/*/
# Generates playground/src/data/gallery.json
# Contains: { categories: [{ name, examples: [{ id, name, script, input, expected, explanation, isBenchmark }] }] }
```

---

## Monaco Elwood Language Definition

### Syntax highlighting (Monarch tokenizer)

Define a custom `elwood` language for Monaco with token rules for:

- **Keywords:** `let`, `return`, `if`, `then`, `else`, `true`, `false`, `null`, `in`, `not`, `and`, `or`, `memo`
- **Pipe operators:** `where`, `select`, `selectMany`, `orderBy`, `orderByDesc`, `groupBy`, `distinct`, `take`, `skip`, `batch`, `join`, `concat`, `index`, `reduce`, `count`, `sum`, `min`, `max`, `first`, `last`, `any`, `all`, `match`, `reverse`
- **Built-in methods:** `toLower`, `toUpper`, `trim`, `replace`, `split`, `substring`, `startsWith`, `endsWith`, `contains`, `length`, `indexOf`, `round`, `floor`, `ceiling`, `abs`, `now`, `utcNow`, `dateFormat`, `dateAdd`, `hash`, `hmac`, `isNull`, `coalesce`, `keys`, `values`, `typeOf`, `convertTo`, etc.
- **Operators:** `==`, `!=`, `>=`, `<=`, `>`, `<`, `&&`, `||`, `!`, `+`, `-`, `*`, `/`, `%`, `=>`, `|`, `...`
- **Special:** `$` (root reference), `..` (recursive descent)
- **Strings:** double-quoted, single-quoted, backtick interpolation with `{expr}`
- **Comments:** `//` single-line
- **Numbers:** integer and decimal

### Autocomplete (CompletionItemProvider)

Triggers on:
- `.` after a value — suggest methods (`.toLower()`, `.replace()`, etc.)
- `|` in a pipe — suggest pipe operators (`where`, `select`, etc.)
- Start of identifier — suggest keywords and functions

Each completion item includes:
- Label (function name)
- Kind (function, keyword, operator)
- Documentation (signature + description)
- Insert text with snippet placeholders (e.g., `where($1 => $2)`)

### Hover documentation (HoverProvider)

When hovering over a function name, show:
- **Signature:** `.replace(search, replacement)` → `string`
- **Description:** "Replaces all occurrences of `search` with `replacement`"
- **Example:** `"hello world".replace("world", "elwood")` → `"hello elwood"`

Generated at build time from `docs/syntax-reference.md`.

---

## Sharing

### Strategy: automatic based on payload size

```
Total size = expression + input JSON

If size < 10 KB:
  → Encode in URL hash (lz-string compressed)
  → Instant, no external dependency
  → Link: playground/#data=eJxLzs8ry0xR...

If size >= 10 KB:
  → Save to GitHub Gist (anonymous)
  → Short, stable link
  → Link: playground/#gist=a1b2c3d4
```

### URL encoding (small payloads)

Uses [lz-string](https://github.com/pieroxy/lz-string) — typically 60-80% compression. A 10KB payload becomes ~3KB in the URL.

### Gist sharing (large payloads)

Creates an anonymous GitHub Gist via API containing `script.elwood` and `input.json`. Anonymous gists have rate limits (60/hour per IP). Optional GitHub login for higher limits can be added later.

### Share modal UI

```
┌─ Share Playground ──────────────────────────────┐
│                                                  │
│ ┌──────────────────────────────────────┐ [Copy] │
│ │ https://user.github.io/elwood/#d... │         │
│ └──────────────────────────────────────┘         │
│                                                  │
│ This link contains your expression and input.    │
│ Anyone with the link can open it.                │
│                                                  │
│                                        [Close]   │
└──────────────────────────────────────────────────┘
```

### Loading shared links

On page load, check URL hash:
1. `#data=...` → decode lz-string, populate editors
2. `#gist=...` → fetch gist via GitHub API, populate editors
3. `#example=...` → load from gallery
4. No hash → show default state (empty expression, `{}` input)

---

## File Loading

### Load File button
Opens native file picker (`<input type="file" accept=".json">`). Reads file content via FileReader API into the input editor. File never leaves the browser.

### Drag and drop
Input panel accepts dropped `.json` files. Visual feedback: dashed blue border on drag-over.

### Large file warning
If file size > 5MB, show a warning banner: "Large file detected. For best performance with large data, use the Elwood CLI."

---

## Phase 2+ Expansion — Multi-Format Support

When Phase 2 format converters are complete, the playground expands. This is Phase 4 work.

### Input format selector
Dropdown in input panel header: JSON (default), CSV, XML, Text. Loading a file auto-detects format from extension.

### Input: dual-tab view
When a non-JSON format is loaded, show two tabs:
- **Raw** — the original data (CSV/XML/Text)
- **As JSON** — the converted JSON that Elwood will process

### Output: dual-tab view
When output format differs from JSON, show two tabs:
- **JSON** — the transformation result
- **[Format]** — the converted output (CSV/XML/Text)

This makes the conversion pipeline transparent to the user.

---

## Project Structure

```
playground/
├── index.html
├── src/
│   ├── App.tsx                    # root component
│   ├── main.tsx                   # React entry point
│   ├── components/
│   │   ├── Toolbar.tsx
│   │   ├── ExpressionPanel.tsx
│   │   ├── InputPanel.tsx
│   │   ├── OutputPanel.tsx
│   │   ├── ErrorPanel.tsx
│   │   ├── StatusBar.tsx
│   │   ├── GallerySidebar.tsx
│   │   ├── ShareModal.tsx
│   │   └── PanelHeader.tsx
│   ├── hooks/
│   │   ├── useEvaluator.ts        # debounced evaluation via @elwood-lang/core
│   │   ├── useSharing.ts          # URL encoding + Gist logic
│   │   └── useFileLoader.ts       # file picker + drag-and-drop
│   ├── editor/
│   │   ├── elwood-language.ts     # Monarch tokenizer + theme
│   │   ├── elwood-completions.ts  # autocomplete provider
│   │   └── elwood-hover.ts        # hover documentation provider
│   ├── data/
│   │   └── gallery.json           # generated from spec/test-cases/
│   └── index.css                  # Tailwind imports + custom properties
├── scripts/
│   └── build-examples.ts          # reads spec/test-cases/, generates gallery.json
├── public/
│   └── favicon.svg                # {|} logo
├── package.json
├── tsconfig.json
├── tailwind.config.ts
├── postcss.config.js
└── vite.config.ts
```

### Dependencies

**Runtime:**
- `react`, `react-dom`
- `@monaco-editor/react`
- `@elwood-lang/core` (workspace: `../ts/`)
- `lz-string`

**Dev:**
- `vite`, `@vitejs/plugin-react`
- `typescript`
- `tailwindcss`, `postcss`, `autoprefixer`

### Scripts

```json
{
  "dev": "vite",
  "build-examples": "tsx scripts/build-examples.ts",
  "build": "npm run build-examples && vite build",
  "preview": "vite preview"
}
```

### Build output

`playground/dist/` — static files ready for GitHub Pages.

---

## GitHub Actions Deployment

```yaml
# .github/workflows/playground.yml
name: Deploy Playground
on:
  push:
    branches: [main]
    paths:
      - 'playground/**'
      - 'ts/**'
      - 'spec/test-cases/**'

jobs:
  deploy:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-node@v4
        with: { node-version: '20' }
      - run: cd ts && npm ci && npm run build
      - run: cd playground && npm ci && npm run build
      - uses: peaceiris/actions-gh-pages@v3
        with:
          github_token: ${{ secrets.GITHUB_TOKEN }}
          publish_dir: playground/dist
```

Deploys automatically when playground code, TS engine, or test cases change.

---

## Performance Guardrails

- **Debounce evaluation:** 300ms after last keystroke before evaluating
- **Evaluation timeout:** Cancel if evaluation takes > 5 seconds, show warning
- **Input size warning:** Banner at > 5MB, suggest CLI
- **Benchmark examples:** Shown in gallery but marked as CLI-only with explanation
- **Web Worker (future):** Run evaluation in a Web Worker to keep UI responsive during heavy computations

---

## Design Tokens (Tailwind custom theme)

Dark theme colors matching the mockup:

```javascript
// tailwind.config.ts (extend theme)
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

Font stack: `'JetBrains Mono', 'Cascadia Code', 'Fira Code', Consolas, monospace` for editors and logo. System font stack for UI elements.

---

## Logo

- **Full logo:** `{elwood}` — all black, monospace font
- **Favicon:** `{|}` — all black, monospace font
- **Source files:** `playground/logo-concepts/concept11-all-black.svg` (logo), `concept12-favicon-pipe.svg` (favicon)

---

## Accessibility

- Keyboard navigation between panels (Tab / Shift+Tab)
- Monaco handles accessibility within editors (screen readers, high contrast)
- Status bar announces evaluation results to screen readers
- Dark mode only for v1 (most developer tools are dark-only)

---

## Open Questions

1. **Mobile support** — Monaco doesn't work well on mobile. Show a read-only mode with a "use desktop for editing" message? Or skip mobile entirely for v1?
2. **XLSX in browser** — Phase 2 format support includes XLSX. Needs SheetJS/xlsx (~300KB). Worth bundling, or skip XLSX in the playground and support only CSV/XML/Text?
