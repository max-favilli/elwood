# Playground Implementation Prompt

Copy everything below this line and paste it into the other Claude terminal:

---

Read the following documents before starting:

1. `docs/playground-spec.md` — full specification (tech stack, components, layout, features, design tokens)
2. `playground/mockup-v2.html` — **open this in a browser** to see the exact look and feel. This is the approved design. Match it closely.
3. `playground/logo-concepts/concept11-all-black.svg` — the logo (`{elwood}`)
4. `playground/logo-concepts/concept12-favicon-pipe.svg` — the favicon (`{|}`)

You are implementing the Elwood Playground as part of Phase 1c. This is a browser-based interactive environment for writing and testing Elwood expressions, hosted on GitHub Pages.

## Tech stack (decided, do not change)

- **Vite** — build tool
- **React** — UI framework (NOT Next.js — single page, fully client-side)
- **TypeScript** — language
- **Tailwind CSS** — styling
- **Monaco Editor** via `@monaco-editor/react` — code editors
- **lz-string** — URL sharing compression
- **@elwood-lang/core** — Elwood engine (workspace dependency from `../ts/`)

## Layout (decided, match the mockup)

The layout is **expression on top (compact), input and output side by side below**:

```
┌─────────────────────────────────────────────────────────────────┐
│ {elwood}  [Examples]  [▶ Run]  |  [Share]  [Copy]    Docs  GitHub │
├─────────────────────────────────────────────────────────────────┤
│ Expression (compact, ~2-3 lines default)             [Clear]    │
│ ┌─────────────────────────────────────────────────────────────┐ │
│ │ $.users | where(u => u.age > 25) | select(.name)            │ │
│ └─────────────────────────────────────────────────────────────┘ │
│ ═══════════════════ draggable resize handle ═════════════════   │
├──────────────────────────────┬──────────────────────────────────┤
│ Input JSON                   │ Output                 ✓ 1.8ms  │
│ ┌──────────────────────────┐ │ ┌────────────────────────────┐  │
│ │ { ... }                  │ │ │ [ ... ]                    │  │
│ │                          │ │ │                            │  │
│ │ [Load File] [Format]     │ │ │                            │  │
│ └──────────────────────────┘ │ └────────────────────────────┘  │
│                              │ ┌ Error (conditional) ───────┐  │
│                              │ │ Line 1: Unknown 'wher'     │  │
│                              │ │ Did you mean 'where'?      │  │
│                              │ └────────────────────────────┘  │
├──────────────────────────────┴──────────────────────────────────┤
│ ✓ Ready   Input: 412 B   Output: 298 B               v0.1.0   │
└─────────────────────────────────────────────────────────────────┘
```

**Why this layout:** Elwood expressions are typically 1-5 lines; input/output JSON can be hundreds of lines. The expression panel is compact at top with a draggable resize handle so users can expand it for multi-line scripts. Input and output get maximum vertical space side by side.

## React component structure

```
<App>
  <Toolbar logo onExamples onRun onShare onCopy />
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
        <PanelHeader title="Output" evalTime="1.8ms" />
        <MonacoEditor language="json" readOnly />
        <ErrorPanel error={error} />
      </OutputPanel>
    </DataPanels>
  </main>
  <StatusBar />
  <GallerySidebar />
  <ShareModal />
</App>
```

## What to build (in order)

### 1. Project setup
Initialize in `playground/` using `npm create vite@latest . -- --template react-ts`. Add Tailwind CSS, Monaco Editor (`@monaco-editor/react`), and lz-string. Link `@elwood-lang/core` as a workspace dependency from `../ts/`.

### 2. Layout and panels
Match the mockup (`playground/mockup-v2.html`) exactly:
- Dark theme with the design tokens from the spec (see `playground-spec.md` "Design Tokens" section)
- Toolbar with `{elwood}` logo (monospace), Examples, Run (primary/blue), Share, Copy, Docs, GitHub links
- Expression panel (top, full width, default ~88px height, resizable)
- Draggable resize handle between expression and data panels (min 36px, max 60% viewport)
- Input JSON panel (bottom-left) with Load File + Format buttons, drag-and-drop support
- Output panel (bottom-right) with eval time in header
- Error panel (conditional, below output, red/orange styling)
- Status bar (blue, bottom)

### 3. Monaco Elwood language definition
Create a custom `elwood` language for Monaco with:
- **Syntax highlighting** (Monarch tokenizer): keywords (`let`, `return`, `if`, `then`, `else`, `true`, `false`, `null`, `in`, `not`, `and`, `or`, `memo`), pipe operators (`where`, `select`, `orderBy`, etc.), built-in methods (`toLower`, `replace`, `hash`, etc.), operators (`==`, `!=`, `>=`, `<=`, `=>`, `|`, `...`), `$` root reference, `..` recursive descent, string interpolation (backtick + `{expr}`), comments (`//`), numbers
- **Autocomplete**: triggered on `.` (methods), `|` (pipe operators), identifier start (keywords). Include snippet placeholders (e.g., `where($1 => $2)`)
- **Hover documentation**: function signatures + descriptions. Generate from `docs/syntax-reference.md`.

### 4. Live evaluation
Wire up `evaluate()` from `@elwood-lang/core`:
- Debounce 300ms after last keystroke
- Show result in output panel (pretty-printed JSON)
- Show errors in error panel with line/column and "Did you mean?" suggestions
- Update status bar with eval time, input/output sizes
- Timeout: cancel if evaluation takes > 5 seconds

### 5. Example gallery
Build a slide-out sidebar (from left) with categorized examples:
- Build script (`scripts/build-examples.ts`) reads `spec/test-cases/*/` and generates `src/data/gallery.json`
- Categories manually curated in a mapping file
- Search/filter as you type
- Clicking an example loads its expression and input, adjusts expression panel height
- Benchmark examples marked with `[CLI]` badge, show explanatory message instead of loading

### 6. File loading
- Load File button: native file picker, FileReader API, file stays in browser
- Drag-and-drop on input panel: dashed blue border on drag-over
- Large file warning (> 5MB)

### 7. Sharing
Automatic based on payload size:
- < 10KB: encode in URL hash via lz-string compression
- >= 10KB: save to anonymous GitHub Gist
- Share modal with URL + Copy button
- On page load: check hash for `#data=...`, `#gist=...`, or `#example=...`

### 8. Polish
- Favicon (`{|}` from `playground/logo-concepts/concept12-favicon-pipe.svg`)
- Keyboard shortcut: Ctrl+Enter / Cmd+Enter to run
- Format button for input JSON
- Copy button for output

## Important constraints

- **Never use the word "Eagle" in any file.** Use generic terms.
- The playground is `playground/` at the repo root, NOT inside `ts/` or `dotnet/`.
- `@elwood-lang/core` is a workspace dependency pointing to `../ts/`.
- Follow the component structure: `src/components/`, `src/hooks/`, `src/editor/`.
- Dark mode only for v1. Monospace font for editors and logo, system font for UI.
- Always ask before committing or creating PRs.

## Design tokens (Tailwind custom theme)

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

## After each step
Tell me what was done and how to preview it (`npm run dev`). Wait for my go-ahead before proceeding to the next step.
