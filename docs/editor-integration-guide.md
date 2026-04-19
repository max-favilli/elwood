# Elwood Editor Integration Guide (Monaco + React/Next.js)

How to add an Elwood script editor with syntax highlighting, autocomplete, and error reporting to a Next.js / React web application using Monaco Editor.

---

## Dependencies

```bash
npm install @monaco-editor/react @elwood-lang/core
```

| Package | Purpose |
|---|---|
| `@monaco-editor/react` | React wrapper for Monaco Editor (VS Code's editor) |
| `@elwood-lang/core` | Elwood language engine (TypeScript). Runs entirely in the browser. Used for real-time validation, execution, and extracting diagnostics. |

---

## Step 1: Elwood Language Definition

Create a file that defines the Elwood language for Monaco's Monarch tokenizer. This gives you syntax highlighting, bracket matching, and auto-closing pairs.

**`src/lib/editor/elwood-language.ts`**

```typescript
import type { languages } from 'monaco-editor';

export const ELWOOD_LANGUAGE_ID = 'elwood';

export const monarchTokensProvider: languages.IMonarchLanguage = {
  defaultToken: '',
  ignoreCase: false,

  // Language keywords
  keywords: [
    'let', 'return', 'if', 'then', 'else', 'true', 'false', 'null',
    'memo', 'match', 'from', 'asc', 'desc', 'on', 'equals', 'into',
  ],

  // Pipe operators — highlighted differently after |
  pipeOperators: [
    'where', 'select', 'selectMany', 'orderBy', 'groupBy', 'distinct',
    'take', 'skip', 'batch', 'join', 'concat', 'reduce', 'index',
    'count', 'sum', 'min', 'max', 'first', 'last', 'any', 'all',
    'takeWhile',
  ],

  // Built-in methods — highlighted as support functions
  builtinMethods: [
    // String
    'toLower', 'toUpper', 'trim', 'trimStart', 'trimEnd',
    'left', 'right', 'padLeft', 'padRight',
    'contains', 'startsWith', 'endsWith', 'replace', 'substring', 'split',
    'length', 'toCharArray', 'regex', 'urlDecode', 'urlEncode', 'sanitize',
    // Numeric
    'round', 'floor', 'ceiling', 'truncate', 'abs',
    // Type conversion
    'toString', 'toNumber', 'convertTo', 'boolean', 'not',
    // Null checks
    'isNull', 'isEmpty', 'isNullOrEmpty', 'isNullOrWhiteSpace',
    // Object/array
    'clone', 'keep', 'remove',
    // Crypto
    'hash', 'rsaSign',
    // DateTime
    'dateFormat', 'tryDateFormat', 'add', 'toUnixTimeSeconds',
    // Membership
    'in',
    // Collection (as methods)
    'count', 'first', 'last', 'sum', 'min', 'max', 'index',
    'take', 'skip', 'concat',
    // Utility functions
    'now', 'utcNow', 'newGuid', 'range', 'iterate',
    // Format I/O
    'fromCsv', 'toCsv', 'fromXml', 'toXml', 'fromText', 'toText',
    'parseJson', 'fromXlsx', 'toXlsx', 'fromParquet', 'toParquet',
  ],

  operators: [
    '==', '!=', '>=', '<=', '=>', '&&', '||', '...',
    '..', '|', '+', '-', '*', '/', '>', '<', '!', '=',
  ],
  symbols: /[=><!~?:&|+\-*\/\^%\.]+/,

  tokenizer: {
    root: [
      // Comments
      [/\/\/.*$/, 'comment'],
      [/\/\*/, 'comment', '@comment'],

      // Strings
      [/`/, 'string.interpolated', '@interpolatedString'],
      [/"/, 'string', '@string_double'],
      [/'/, 'string', '@string_single'],

      // Numbers
      [/\d+(\.\d+)?/, 'number'],

      // JSONPath variables: $.. $.  $name  $
      [/\$\.\./, 'variable.path'],
      [/\$\./, 'variable.path'],
      [/\$\w+/, 'variable.path'],
      [/\$/, 'variable.path'],

      // Pipe — switch to pipeOperator state for the next word
      [/\|(?!\|)/, { token: 'delimiter.pipe', next: '@pipeOperator' }],

      // Identifiers, keywords, and built-in methods
      [/[a-zA-Z_]\w*/, {
        cases: {
          '@keywords': 'keyword',
          '@builtinMethods': 'support.function',
          '@default': 'identifier',
        },
      }],

      // Operators
      [/\.\.\./, 'operator'],
      [/=>/, 'operator.arrow'],
      [/==|!=|>=|<=|&&|\|\|/, 'operator'],
      [/[+\-*\/><!=]/, 'operator'],

      // Delimiters
      [/[{}()\[\]]/, 'delimiter.bracket'],
      [/[,;:]/, 'delimiter'],
      [/\*/, 'operator.wildcard'],
    ],

    // After |, the next identifier is a pipe operator (highlighted in purple)
    pipeOperator: [
      [/\s+/, 'white'],
      [/[a-zA-Z_]\w*/, {
        cases: {
          '@pipeOperators': { token: 'keyword.pipe', next: '@pop' },
          '@keywords': { token: 'keyword', next: '@pop' },
          '@default': { token: 'identifier', next: '@pop' },
        },
      }],
      ['', '', '@pop'],
    ],

    comment: [
      [/[^\/*]+/, 'comment'],
      [/\*\//, 'comment', '@pop'],
      [/[\/*]/, 'comment'],
    ],
    string_double: [
      [/[^\\"]+/, 'string'],
      [/\\./, 'string.escape'],
      [/"/, 'string', '@pop'],
    ],
    string_single: [
      [/[^\\']+/, 'string'],
      [/\\./, 'string.escape'],
      [/'/, 'string', '@pop'],
    ],

    // Backtick strings with {expression} interpolation
    interpolatedString: [
      [/\{/, { token: 'string.interpolated.bracket', next: '@interpolatedExpr' }],
      [/[^`{]+/, 'string.interpolated'],
      [/`/, 'string.interpolated', '@pop'],
    ],
    interpolatedExpr: [
      [/\}/, { token: 'string.interpolated.bracket', next: '@pop' }],
      { include: 'root' },
    ],
  },
};

export const languageConfiguration: languages.LanguageConfiguration = {
  comments: { lineComment: '//', blockComment: ['/*', '*/'] },
  brackets: [['{', '}'], ['[', ']'], ['(', ')']],
  autoClosingPairs: [
    { open: '{', close: '}' },
    { open: '[', close: ']' },
    { open: '(', close: ')' },
    { open: '"', close: '"', notIn: ['string'] },
    { open: "'", close: "'", notIn: ['string'] },
    { open: '`', close: '`', notIn: ['string'] },
  ],
  surroundingPairs: [
    { open: '{', close: '}' },
    { open: '[', close: ']' },
    { open: '(', close: ')' },
    { open: '"', close: '"' },
    { open: "'", close: "'" },
    { open: '`', close: '`' },
  ],
};

export function registerElwoodLanguage(monaco: typeof import('monaco-editor')) {
  monaco.languages.register({ id: ELWOOD_LANGUAGE_ID });
  monaco.languages.setMonarchTokensProvider(ELWOOD_LANGUAGE_ID, monarchTokensProvider);
  monaco.languages.setLanguageConfiguration(ELWOOD_LANGUAGE_ID, languageConfiguration);
}
```

---

## Step 2: Dark Theme

Define an `elwood-dark` theme with colors that distinguish the different token types.

**`src/lib/editor/elwood-theme.ts`**

```typescript
import type { editor } from 'monaco-editor';

export const elwoodDarkTheme: editor.IStandaloneThemeData = {
  base: 'vs-dark',
  inherit: true,
  rules: [
    { token: 'keyword',            foreground: '569cd6' },
    { token: 'keyword.pipe',       foreground: 'c586c0', fontStyle: 'bold' },
    { token: 'string',             foreground: 'ce9178' },
    { token: 'string.interpolated', foreground: 'ce9178' },
    { token: 'number',             foreground: 'b5cea8' },
    { token: 'comment',            foreground: '6a9955' },
    { token: 'variable.path',      foreground: '9cdcfe' },
    { token: 'operator.arrow',     foreground: 'd4d4d4' },
    { token: 'support.function',   foreground: 'dcdcaa' },
    { token: 'delimiter.pipe',     foreground: 'c586c0', fontStyle: 'bold' },
  ],
  colors: {
    'editor.background': '#1e1e1e',
    'editor.foreground': '#d4d4d4',
  },
};
```

The resulting color scheme:

| Token | Color | Example |
|---|---|---|
| Keywords (`let`, `return`, `if`) | Blue | `let x = ...` |
| Pipe operators (`where`, `select`) | Purple, bold | `\| where ...` |
| Strings | Orange | `"hello"` |
| Numbers | Light green | `42` |
| Comments | Muted green | `// comment` |
| JSONPath (`$`, `$.field`) | Cyan | `$.users[*]` |
| Built-in methods | Yellow | `.toLower()` |
| Pipe delimiter `\|` | Purple, bold | `\|` |

---

## Step 3: Editor Component

Wire Monaco into a React component with `beforeMount` to register the language before the editor renders.

```tsx
'use client';

import { useRef, useCallback } from 'react';
import MonacoEditor from '@monaco-editor/react';
import { ELWOOD_LANGUAGE_ID } from '@/lib/editor/elwood-language';
import { elwoodDarkTheme } from '@/lib/editor/elwood-theme';

interface ElwoodEditorProps {
  value: string;
  onChange: (value: string) => void;
  height?: string;
  readOnly?: boolean;
}

export function ElwoodEditor({ value, onChange, height = '400px', readOnly }: ElwoodEditorProps) {
  const monacoRef = useRef<typeof import('monaco-editor') | null>(null);

  const handleBeforeMount = useCallback((monaco: typeof import('monaco-editor')) => {
    monacoRef.current = monaco;

    // Register Elwood language (dynamic import avoids SSR issues)
    import('@/lib/editor/elwood-language').then(({ registerElwoodLanguage }) => {
      registerElwoodLanguage(monaco);
    });

    // Register theme
    monaco.editor.defineTheme('elwood-dark', elwoodDarkTheme);
  }, []);

  return (
    <MonacoEditor
      height={height}
      language={ELWOOD_LANGUAGE_ID}
      theme="elwood-dark"
      value={value}
      onChange={(v) => onChange(v ?? '')}
      beforeMount={handleBeforeMount}
      options={{
        minimap: { enabled: false },
        lineNumbers: 'on',
        wordWrap: 'on',
        fontSize: 13,
        padding: { top: 8 },
        readOnly,
        scrollBeyondLastLine: false,
        automaticLayout: true,
      }}
    />
  );
}
```

This gives you syntax highlighting, bracket matching, and auto-closing out of the box.

---

## Step 4: Autocomplete (Completion Provider)

Register a completion provider that suggests keywords, pipe operators, and built-in methods as the user types.

**`src/lib/editor/elwood-completions.ts`**

```typescript
import type { languages, Position, editor } from 'monaco-editor';
import { ELWOOD_LANGUAGE_ID } from './elwood-language';

const KEYWORDS = [
  'let', 'return', 'if', 'then', 'else', 'true', 'false', 'null',
  'memo', 'match', 'from', 'asc', 'desc',
];

const PIPE_OPERATORS = [
  { label: 'where',     detail: 'Filter items',              insert: 'where ${1:predicate}' },
  { label: 'select',    detail: 'Transform each item',       insert: 'select ${1:projection}' },
  { label: 'selectMany', detail: 'Flatten nested results',   insert: 'selectMany ${1:projection}' },
  { label: 'groupBy',   detail: 'Group by key',              insert: 'groupBy ${1:key}' },
  { label: 'orderBy',   detail: 'Sort items',                insert: 'orderBy ${1:key}' },
  { label: 'distinct',  detail: 'Remove duplicates',         insert: 'distinct' },
  { label: 'take',      detail: 'First n items',             insert: 'take ${1:n}' },
  { label: 'skip',      detail: 'Skip n items',              insert: 'skip ${1:n}' },
  { label: 'batch',     detail: 'Chunk into groups of n',    insert: 'batch ${1:n}' },
  { label: 'join',      detail: 'Join two collections',      insert: 'join ${1:source} on ${2:lKey} equals ${3:rKey}' },
  { label: 'concat',    detail: 'Join array into string',    insert: 'concat ${1:"separator"}' },
  { label: 'reduce',    detail: 'Fold/accumulate',           insert: 'reduce (${1:acc}, ${2:x}) => ${3:expr} from ${4:init}' },
  { label: 'count',     detail: 'Number of items',           insert: 'count' },
  { label: 'sum',       detail: 'Sum of values',             insert: 'sum' },
  { label: 'min',       detail: 'Minimum value',             insert: 'min' },
  { label: 'max',       detail: 'Maximum value',             insert: 'max' },
  { label: 'first',     detail: 'First item',                insert: 'first' },
  { label: 'last',      detail: 'Last item',                 insert: 'last' },
  { label: 'any',       detail: 'True if any match',         insert: 'any ${1:predicate}' },
  { label: 'all',       detail: 'True if all match',         insert: 'all ${1:predicate}' },
  { label: 'index',     detail: 'Array of indices',          insert: 'index' },
  { label: 'takeWhile', detail: 'Take while true',           insert: 'takeWhile ${1:predicate}' },
];

const BUILTIN_METHODS = [
  // String
  { label: 'toLower',    detail: 'Lowercase string' },
  { label: 'toUpper',    detail: 'Uppercase string' },
  { label: 'trim',       detail: 'Trim whitespace' },
  { label: 'trimStart',  detail: 'Trim leading whitespace' },
  { label: 'trimEnd',    detail: 'Trim trailing whitespace' },
  { label: 'split',      detail: 'Split into array',             insert: 'split(${1:"delimiter"})' },
  { label: 'replace',    detail: 'Replace occurrences',          insert: 'replace(${1:"search"}, ${2:"replacement"})' },
  { label: 'substring',  detail: 'Extract substring',            insert: 'substring(${1:start}, ${2:length})' },
  { label: 'contains',   detail: 'Contains substring',           insert: 'contains(${1:"text"})' },
  { label: 'startsWith', detail: 'Starts with prefix',           insert: 'startsWith(${1:"prefix"})' },
  { label: 'endsWith',   detail: 'Ends with suffix',             insert: 'endsWith(${1:"suffix"})' },
  { label: 'left',       detail: 'First n characters',           insert: 'left(${1:n})' },
  { label: 'right',      detail: 'Last n characters',            insert: 'right(${1:n})' },
  { label: 'length',     detail: 'String/array length' },
  { label: 'regex',      detail: 'Extract regex matches',        insert: 'regex(${1:"pattern"})' },
  { label: 'padLeft',    detail: 'Pad from left',                insert: 'padLeft(${1:width}, ${2:"char"})' },
  { label: 'padRight',   detail: 'Pad from right',               insert: 'padRight(${1:width}, ${2:"char"})' },
  // Numeric
  { label: 'round',      detail: 'Round number',                 insert: 'round(${1:decimals})' },
  { label: 'floor',      detail: 'Round down' },
  { label: 'ceiling',    detail: 'Round up' },
  { label: 'abs',        detail: 'Absolute value' },
  { label: 'truncate',   detail: 'Remove decimal part' },
  // Type conversion
  { label: 'toString',   detail: 'Convert to string',            insert: 'toString(${1:format})' },
  { label: 'toNumber',   detail: 'Parse to number' },
  { label: 'parseJson',  detail: 'Parse JSON string' },
  { label: 'boolean',    detail: 'Coerce to boolean' },
  // Null checks
  { label: 'isNull',     detail: 'True if null' },
  { label: 'isEmpty',    detail: 'True if null/empty' },
  { label: 'isNullOrEmpty', detail: 'True if null or empty' },
  // Object
  { label: 'keep',       detail: 'Keep only named properties',   insert: 'keep(${1:prop1}, ${2:prop2})' },
  { label: 'remove',     detail: 'Remove named properties',      insert: 'remove(${1:prop1}, ${2:prop2})' },
  { label: 'clone',      detail: 'Deep copy' },
  // DateTime
  { label: 'dateFormat', detail: 'Format date string',           insert: 'dateFormat(${1:"yyyy-MM-dd"})' },
  { label: 'add',        detail: 'Add duration',                 insert: 'add(${1:"timespan"})' },
  { label: 'toUnixTimeSeconds', detail: 'Convert to Unix epoch' },
  // Hashing
  { label: 'hash',       detail: 'MD5 hash',                     insert: 'hash(${1:length})' },
  // Format I/O
  { label: 'fromCsv',    detail: 'Parse CSV to array' },
  { label: 'toCsv',      detail: 'Array to CSV' },
  { label: 'fromXml',    detail: 'Parse XML to JSON' },
  { label: 'toXml',      detail: 'JSON to XML' },
  { label: 'fromText',   detail: 'Split text into lines' },
  { label: 'toText',     detail: 'Join array into text' },
  // Encoding
  { label: 'urlEncode',  detail: 'URL-encode string' },
  { label: 'urlDecode',  detail: 'URL-decode string' },
  { label: 'sanitize',   detail: 'Transliterate to ASCII' },
];

const UTILITY_FUNCTIONS = [
  { label: 'newGuid',  detail: 'Generate GUID',           insert: 'newGuid()' },
  { label: 'now',      detail: 'Current UTC time',        insert: 'now(${1:"format"})' },
  { label: 'utcNow',   detail: 'Current UTC time',        insert: 'utcNow(${1:"format"})' },
  { label: 'range',    detail: 'Generate numeric range',  insert: 'range(${1:start}, ${2:count})' },
  { label: 'iterate',  detail: 'Generate lazy sequence',  insert: 'iterate(${1:seed}, ${2:fn})' },
];

export function registerElwoodCompletions(monaco: typeof import('monaco-editor')) {
  monaco.languages.registerCompletionItemProvider(ELWOOD_LANGUAGE_ID, {
    triggerCharacters: ['.', '|', ' '],

    provideCompletionItems(
      model: editor.ITextModel,
      position: Position
    ): languages.ProviderResult<languages.CompletionList> {
      const line = model.getLineContent(position.lineNumber);
      const textBefore = line.substring(0, position.column - 1);
      const word = model.getWordUntilPosition(position);
      const range = {
        startLineNumber: position.lineNumber,
        startColumn: word.startColumn,
        endLineNumber: position.lineNumber,
        endColumn: word.endColumn,
      };

      const suggestions: languages.CompletionItem[] = [];

      // After a dot: suggest built-in methods
      if (textBefore.match(/\.\s*\w*$/)) {
        for (const m of BUILTIN_METHODS) {
          suggestions.push({
            label: m.label,
            kind: monaco.languages.CompletionItemKind.Method,
            detail: m.detail,
            insertText: m.insert ?? `${m.label}()`,
            insertTextRules: m.insert
              ? monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet
              : undefined,
            range,
          });
        }
        return { suggestions };
      }

      // After a pipe: suggest pipe operators
      if (textBefore.match(/\|\s*\w*$/)) {
        for (const op of PIPE_OPERATORS) {
          suggestions.push({
            label: op.label,
            kind: monaco.languages.CompletionItemKind.Keyword,
            detail: op.detail,
            insertText: op.insert,
            insertTextRules: monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet,
            range,
          });
        }
        return { suggestions };
      }

      // Default: suggest keywords and utility functions
      for (const kw of KEYWORDS) {
        suggestions.push({
          label: kw,
          kind: monaco.languages.CompletionItemKind.Keyword,
          insertText: kw,
          range,
        });
      }
      for (const fn of UTILITY_FUNCTIONS) {
        suggestions.push({
          label: fn.label,
          kind: monaco.languages.CompletionItemKind.Function,
          detail: fn.detail,
          insertText: fn.insert,
          insertTextRules: monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet,
          range,
        });
      }

      // Snippet: let binding
      suggestions.push({
        label: 'let',
        kind: monaco.languages.CompletionItemKind.Snippet,
        detail: 'Let binding',
        insertText: 'let ${1:name} = ${2:expression}',
        insertTextRules: monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet,
        range,
      });

      // Snippet: memo function
      suggestions.push({
        label: 'memo',
        kind: monaco.languages.CompletionItemKind.Snippet,
        detail: 'Memoized function',
        insertText: 'let ${1:name} = memo ${2:param} => ${3:expression}',
        insertTextRules: monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet,
        range,
      });

      return { suggestions };
    },
  });
}
```

Register it alongside the language in `beforeMount`:

```typescript
import('@/lib/editor/elwood-language').then(({ registerElwoodLanguage }) => {
  registerElwoodLanguage(monaco);
});
import('@/lib/editor/elwood-completions').then(({ registerElwoodCompletions }) => {
  registerElwoodCompletions(monaco);
});
```

---

## Step 5: Real-Time Error Reporting

Use `@elwood-lang/core` to validate the script on every change and display errors as red squiggly underlines in the editor.

**`src/lib/editor/elwood-diagnostics.ts`**

```typescript
import type { editor } from 'monaco-editor';
import { ELWOOD_LANGUAGE_ID } from './elwood-language';

let debounceTimer: ReturnType<typeof setTimeout> | null = null;

/**
 * Validate an Elwood script and set error markers on the editor model.
 * Call this from the editor's onChange handler.
 *
 * @param monaco  - The Monaco namespace
 * @param model   - The editor's text model
 * @param input   - Optional JSON input for full evaluation (pass null for syntax-only)
 */
export function validateElwoodScript(
  monaco: typeof import('monaco-editor'),
  model: editor.ITextModel,
  input?: unknown
) {
  if (debounceTimer) clearTimeout(debounceTimer);

  debounceTimer = setTimeout(async () => {
    const source = model.getValue();
    if (!source.trim()) {
      monaco.editor.setModelMarkers(model, ELWOOD_LANGUAGE_ID, []);
      return;
    }

    try {
      const { evaluate, execute } = await import('@elwood-lang/core');

      const isScript = source.trimStart().startsWith('let ') ||
                       source.includes('\nlet ') ||
                       source.includes('return ');

      // Use empty object as fallback input for syntax validation
      const data = input ?? {};

      const result = isScript
        ? execute(source, data)
        : evaluate(source.trim(), data);

      const markers: editor.IMarkerData[] = [];

      if (!result.success && result.diagnostics) {
        for (const diag of result.diagnostics) {
          markers.push({
            severity: diag.severity === 'error'
              ? monaco.MarkerSeverity.Error
              : diag.severity === 'warning'
              ? monaco.MarkerSeverity.Warning
              : monaco.MarkerSeverity.Info,
            message: diag.suggestion
              ? `${diag.message} ${diag.suggestion}`
              : diag.message,
            startLineNumber: diag.line ?? 1,
            startColumn: diag.column ?? 1,
            endLineNumber: diag.line ?? 1,
            endColumn: (diag.column ?? 1) + 1,
          });
        }
      }

      monaco.editor.setModelMarkers(model, ELWOOD_LANGUAGE_ID, markers);
    } catch {
      // If the engine itself throws, clear markers rather than show false errors
      monaco.editor.setModelMarkers(model, ELWOOD_LANGUAGE_ID, []);
    }
  }, 500); // 500ms debounce
}
```

Wire it into the editor component:

```tsx
import { validateElwoodScript } from '@/lib/editor/elwood-diagnostics';

<MonacoEditor
  onChange={(value) => {
    onChange(value ?? '');
    // Validate on every change
    const model = editorRef.current?.getModel();
    if (monacoRef.current && model) {
      validateElwoodScript(monacoRef.current, model, inputData);
    }
  }}
  onMount={(editor) => {
    editorRef.current = editor;
  }}
  // ... other props
/>
```

The errors appear as red underlines in the editor with hover tooltips showing the error message and suggestion (e.g., "Property 'nme' not found. Did you mean 'name'?").

---

## Step 6: Putting It All Together

Complete editor component with all features:

```tsx
'use client';

import { useRef, useCallback } from 'react';
import MonacoEditor from '@monaco-editor/react';
import type { editor } from 'monaco-editor';
import { ELWOOD_LANGUAGE_ID } from '@/lib/editor/elwood-language';
import { elwoodDarkTheme } from '@/lib/editor/elwood-theme';
import { validateElwoodScript } from '@/lib/editor/elwood-diagnostics';

interface ElwoodEditorProps {
  value: string;
  onChange: (value: string) => void;
  /** JSON input data for validation context (optional) */
  inputData?: unknown;
  height?: string;
  readOnly?: boolean;
  onRun?: () => void;
}

export function ElwoodEditor({
  value, onChange, inputData, height = '400px', readOnly, onRun,
}: ElwoodEditorProps) {
  const monacoRef = useRef<typeof import('monaco-editor') | null>(null);
  const editorRef = useRef<editor.IStandaloneCodeEditor | null>(null);

  const handleBeforeMount = useCallback((monaco: typeof import('monaco-editor')) => {
    monacoRef.current = monaco;

    // Register language, completions, and theme
    import('@/lib/editor/elwood-language').then(({ registerElwoodLanguage }) => {
      registerElwoodLanguage(monaco);
    });
    import('@/lib/editor/elwood-completions').then(({ registerElwoodCompletions }) => {
      registerElwoodCompletions(monaco);
    });
    monaco.editor.defineTheme('elwood-dark', elwoodDarkTheme);

    // Ctrl+Enter to run
    if (onRun) {
      monaco.editor.addEditorAction({
        id: 'elwood-run',
        label: 'Run Elwood Script',
        keybindings: [monaco.KeyMod.CtrlCmd | monaco.KeyCode.Enter],
        run: () => onRun(),
      });
    }
  }, [onRun]);

  const handleMount = useCallback((editor: editor.IStandaloneCodeEditor) => {
    editorRef.current = editor;

    // Initial validation
    const model = editor.getModel();
    if (monacoRef.current && model) {
      validateElwoodScript(monacoRef.current, model, inputData);
    }
  }, [inputData]);

  return (
    <MonacoEditor
      height={height}
      language={ELWOOD_LANGUAGE_ID}
      theme="elwood-dark"
      value={value}
      beforeMount={handleBeforeMount}
      onMount={handleMount}
      onChange={(v) => {
        const newValue = v ?? '';
        onChange(newValue);
        const model = editorRef.current?.getModel();
        if (monacoRef.current && model) {
          validateElwoodScript(monacoRef.current, model, inputData);
        }
      }}
      options={{
        minimap: { enabled: false },
        lineNumbers: 'on',
        wordWrap: 'on',
        fontSize: 13,
        padding: { top: 8 },
        readOnly,
        scrollBeyondLastLine: false,
        automaticLayout: true,
      }}
    />
  );
}
```

Usage:

```tsx
const [script, setScript] = useState('$.items[*] | where i => i.active');
const inputData = { items: [{ name: 'A', active: true }, { name: 'B', active: false }] };

<ElwoodEditor
  value={script}
  onChange={setScript}
  inputData={inputData}
  height="300px"
  onRun={() => handleRunScript()}
/>
```

---

## Executing Scripts in the Browser

`@elwood-lang/core` runs entirely client-side. No backend needed for evaluation.

```typescript
import { evaluate, execute, type ElwoodResult } from '@elwood-lang/core';

// Single expression
const result: ElwoodResult = evaluate('$.users[*] | select u => u.name', inputData);

// Script with let/return
const result: ElwoodResult = execute(`
  let adults = $.users[*] | where u => u.age >= 18
  return { count: adults | count, names: adults | select u => u.name }
`, inputData);

// Auto-detect
const source = script.trim();
const isScript = source.startsWith('let ') || source.includes('\nlet ') || source.includes('return ');
const result = isScript ? execute(source, inputData) : evaluate(source, inputData);

if (result.success) {
  console.log(JSON.stringify(result.value, null, 2));
} else {
  for (const d of result.diagnostics) {
    console.error(`${d.severity}: ${d.message}` + (d.suggestion ? ` (${d.suggestion})` : ''));
  }
}
```

### ElwoodResult Type

```typescript
interface ElwoodResult {
  value: unknown;              // The result (any JSON-compatible value)
  success: boolean;            // true if no errors
  diagnostics: ElwoodDiagnostic[];
}

interface ElwoodDiagnostic {
  severity: 'error' | 'warning' | 'info';
  message: string;
  line?: number;
  column?: number;
  suggestion?: string;         // "Did you mean 'name'?"
}
```

---

## File Structure Summary

```
src/lib/editor/
  elwood-language.ts      # Monarch tokenizer, language config, registration
  elwood-theme.ts         # Dark theme token colors
  elwood-completions.ts   # Autocomplete provider (keywords, pipes, methods)
  elwood-diagnostics.ts   # Real-time error validation via @elwood-lang/core
```

---

## Reference

- [Elwood Syntax Reference](syntax-reference.md) — complete language specification
- [Elwood .NET Integration Guide](dotnet-integration-guide.md) — server-side usage with the .NET engine
- Source: [github.com/max-favilli/elwood](https://github.com/max-favilli/elwood)
- npm: `@elwood-lang/core`
