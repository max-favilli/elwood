# Playground Implementation Prompt

Copy everything below this line and paste it into the other Claude terminal:

---

Read the following documents before starting:

1. `docs/playground-spec.md` — full specification (tech stack, components, features, design tokens)
2. `playground/mockup.html` — open this in a browser to see the exact look and feel we want
3. `playground/logo-concepts/concept11-all-black.svg` — the logo (`{elwood}`)
4. `playground/logo-concepts/concept12-favicon-pipe.svg` — the favicon (`{|}`)

You are implementing the Elwood Playground as part of Phase 1c. This is a browser-based interactive environment for writing and testing Elwood expressions, hosted on GitHub Pages.

## Tech stack (decided, do not change)

- **Vite** — build tool
- **React** — UI framework (NOT Next.js)
- **TypeScript** — language
- **Tailwind CSS** — styling
- **Monaco Editor** via `@monaco-editor/react` — code editors
- **lz-string** — URL sharing compression
- **@elwood-lang/core** — Elwood engine (workspace dependency from `../ts/`)

## What to build

1. **Initialize the project** in `playground/` using `npm create vite@latest . -- --template react-ts`, then add Tailwind and Monaco dependencies.

2. **Implement the UI** matching the mockup (`playground/mockup.html`):
   - Dark theme, VS Code-inspired aesthetic
   - Toolbar: `{elwood}` logo, Examples button, Run button, Share button, Copy Output, Docs/GitHub links
   - Three-panel layout: Expression (top-left), Input JSON (bottom-left), Output (right)
   - Resizable split between expression and input panels
   - Error panel below output (conditional, shows when evaluation fails)
   - Status bar at bottom (eval time, input/output size, version)

3. **Monaco Elwood language definition** — create a custom language with:
   - Syntax highlighting (keywords, pipe operators, built-in methods, operators, strings, comments, `$` root, interpolation)
   - Autocomplete (triggered on `.` for methods, `|` for pipe operators, identifier start for keywords)
   - Hover documentation (function signatures + descriptions, generated from `docs/syntax-reference.md`)

4. **Live evaluation** — call `evaluate()` from `@elwood-lang/core` on every keystroke (debounced 300ms). Show result in output panel, or error in error panel.

5. **Example gallery** — slide-out sidebar with categorized examples:
   - Build script (`scripts/build-examples.ts`) that reads `spec/test-cases/*/` and generates `src/data/gallery.json`
   - Categories defined in a `categories.json` mapping
   - Search/filter functionality
   - Benchmark examples marked as CLI-only with explanatory message

6. **File loading** — Load File button (native file picker) + drag-and-drop on input panel. Warn if > 5MB.

7. **Sharing** — automatic based on size:
   - < 10KB: encode expression + input in URL hash via lz-string
   - >= 10KB: save to anonymous GitHub Gist
   - Share modal shows the URL with a Copy button
   - On page load, check hash for `#data=...`, `#gist=...`, or `#example=...`

8. **Design tokens** — use the Tailwind custom theme from the spec (dark colors matching the mockup).

## Important constraints

- **Never use the word "Eagle" in any file.** See `.private/eagle-migration.md` for context.
- The playground is `playground/` at the repo root, NOT inside `ts/` or `dotnet/`.
- `@elwood-lang/core` is a workspace dependency pointing to `../ts/`.
- Follow the component structure in the spec: `src/components/`, `src/hooks/`, `src/editor/`.
- Dark mode only for v1. System font for UI, monospace for editors and logo.
- Always ask before committing or creating PRs.

## Implementation order

1. Project setup (Vite + React + Tailwind + Monaco)
2. Layout and panels (match the mockup)
3. Monaco Elwood language definition
4. Live evaluation wiring
5. Example gallery + build script
6. File loading
7. Sharing (URL + Gist)
8. Status bar + polish

After each major step, tell me what was done and show me how to preview it (`npm run dev`). Work through the steps sequentially.
