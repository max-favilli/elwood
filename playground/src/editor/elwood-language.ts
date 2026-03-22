import type { languages } from 'monaco-editor';

export const ELWOOD_LANGUAGE_ID = 'elwood';

export const monarchTokensProvider: languages.IMonarchLanguage = {
  defaultToken: '',
  ignoreCase: false,

  keywords: [
    'let', 'return', 'if', 'then', 'else', 'true', 'false', 'null',
    'memo', 'match', 'from', 'asc', 'desc', 'on', 'equals', 'into',
  ],

  pipeOperators: [
    'where', 'select', 'selectMany', 'orderBy', 'groupBy', 'distinct',
    'take', 'skip', 'batch', 'join', 'concat', 'reduce', 'index',
    'count', 'sum', 'min', 'max', 'first', 'last', 'any', 'all',
  ],

  builtinMethods: [
    'toLower', 'toUpper', 'trim', 'trimStart', 'trimEnd',
    'left', 'right', 'padLeft', 'padRight',
    'contains', 'startsWith', 'endsWith', 'replace', 'substring', 'split',
    'length', 'toCharArray', 'regex', 'urlDecode', 'urlEncode', 'sanitize',
    'round', 'floor', 'ceiling', 'truncate', 'abs',
    'toString', 'toNumber', 'convertTo', 'boolean', 'not',
    'isNull', 'isEmpty', 'isNullOrEmpty', 'isNullOrWhiteSpace',
    'clone', 'keep', 'remove',
    'hash', 'rsaSign', 'newGuid',
    'now', 'utcNow', 'dateFormat', 'tryDateFormat', 'add', 'toUnixTimeSeconds',
    'in', 'count', 'first', 'last', 'sum', 'min', 'max', 'index',
    'take', 'skip', 'concat',
    'range', 'newGuid',
  ],

  operators: [
    '==', '!=', '>=', '<=', '=>', '&&', '||', '...', '..', '|',
    '+', '-', '*', '/', '>', '<', '!', '=',
  ],

  symbols: /[=><!~?:&|+\-*\/\^%\.]+/,

  tokenizer: {
    root: [
      // Comments
      [/\/\/.*$/, 'comment'],
      [/\/\*/, 'comment', '@comment'],

      // Interpolated strings (backtick)
      [/`/, 'string.interpolated', '@interpolatedString'],

      // Strings
      [/"/, 'string', '@string_double'],
      [/'/, 'string', '@string_single'],

      // Numbers
      [/\d+(\.\d+)?/, 'number'],

      // Dollar path root
      [/\$\.\./, 'variable.path'],  // $..
      [/\$\./, 'variable.path'],    // $.
      [/\$/, 'variable.path'],      // $

      // Pipe operator
      [/\|(?!\|)/, { token: 'delimiter.pipe', next: '@pipeOperator' }],

      // Identifiers & keywords
      [/[a-zA-Z_]\w*/, {
        cases: {
          '@keywords': 'keyword',
          '@builtinMethods': 'support.function',
          '@default': 'identifier',
        }
      }],

      // Spread
      [/\.\.\./, 'operator'],

      // Operators
      [/=>/, 'operator.arrow'],
      [/==|!=|>=|<=|&&|\|\|/, 'operator'],
      [/[+\-*\/><!=]/, 'operator'],

      // Brackets
      [/[{}()\[\]]/, 'delimiter.bracket'],
      [/[,;:]/, 'delimiter'],

      // Wildcard in brackets
      [/\*/, 'operator.wildcard'],
    ],

    pipeOperator: [
      [/\s+/, 'white'],
      [/[a-zA-Z_]\w*/, {
        cases: {
          '@pipeOperators': { token: 'keyword.pipe', next: '@pop' },
          '@keywords': { token: 'keyword', next: '@pop' },
          '@default': { token: 'identifier', next: '@pop' },
        }
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
  comments: {
    lineComment: '//',
    blockComment: ['/*', '*/'],
  },
  brackets: [
    ['{', '}'],
    ['[', ']'],
    ['(', ')'],
  ],
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

// ── Autocomplete ──

const PIPE_OPERATOR_COMPLETIONS: languages.CompletionItem[] = [
  { label: 'where', kind: 1, insertText: 'where ${1:x} => ${2:condition}', insertTextRules: 4, documentation: 'Filter items by predicate' },
  { label: 'select', kind: 1, insertText: 'select ${1:x} => ${2:projection}', insertTextRules: 4, documentation: 'Transform each item' },
  { label: 'selectMany', kind: 1, insertText: 'selectMany ${1:x} => ${2:x.items}', insertTextRules: 4, documentation: 'Flatten nested arrays' },
  { label: 'orderBy', kind: 1, insertText: 'orderBy ${1:x} => ${2:x.field} ${3|asc,desc|}', insertTextRules: 4, documentation: 'Sort by key' },
  { label: 'groupBy', kind: 1, insertText: 'groupBy ${1:x} => ${2:x.field}', insertTextRules: 4, documentation: 'Group by key → { key, items }' },
  { label: 'distinct', kind: 1, insertText: 'distinct', documentation: 'Remove duplicates' },
  { label: 'take', kind: 1, insertText: 'take ${1:n}', insertTextRules: 4, documentation: 'First n items' },
  { label: 'skip', kind: 1, insertText: 'skip ${1:n}', insertTextRules: 4, documentation: 'Skip n items' },
  { label: 'batch', kind: 1, insertText: 'batch ${1:n}', insertTextRules: 4, documentation: 'Chunk into groups of n' },
  { label: 'join', kind: 1, insertText: 'join ${1:source} on ${2:l} => ${3:l.id} equals ${4:r} => ${5:r.id}', insertTextRules: 4, documentation: 'Join two arrays by key' },
  { label: 'concat', kind: 1, insertText: 'concat', documentation: 'Join array into string (default separator |)' },
  { label: 'reduce', kind: 1, insertText: 'reduce (${1:acc}, ${2:x}) => ${3:acc + x}', insertTextRules: 4, documentation: 'General-purpose fold' },
  { label: 'count', kind: 1, insertText: 'count', documentation: 'Number of items' },
  { label: 'sum', kind: 1, insertText: 'sum', documentation: 'Sum of numeric values' },
  { label: 'min', kind: 1, insertText: 'min', documentation: 'Minimum value' },
  { label: 'max', kind: 1, insertText: 'max', documentation: 'Maximum value' },
  { label: 'first', kind: 1, insertText: 'first', documentation: 'First item (optional predicate)' },
  { label: 'last', kind: 1, insertText: 'last', documentation: 'Last item (optional predicate)' },
  { label: 'any', kind: 1, insertText: 'any ${1:x} => ${2:condition}', insertTextRules: 4, documentation: 'True if any item matches' },
  { label: 'all', kind: 1, insertText: 'all ${1:x} => ${2:condition}', insertTextRules: 4, documentation: 'True if all items match' },
  { label: 'match', kind: 1, insertText: 'match ${1:"value"} => ${2:result}, _ => ${3:default}', insertTextRules: 4, documentation: 'Pattern matching' },
  { label: 'index', kind: 1, insertText: 'index', documentation: 'Replace items with 0-based indices' },
].map(c => ({ ...c, range: undefined as any }));

const METHOD_COMPLETIONS: languages.CompletionItem[] = [
  // String
  { label: 'toLower', kind: 2, insertText: 'toLower()', documentation: 'Lowercase (or toLower(n) for position)' },
  { label: 'toUpper', kind: 2, insertText: 'toUpper()', documentation: 'Uppercase (or toUpper(n) for position)' },
  { label: 'trim', kind: 2, insertText: 'trim()', documentation: 'Trim whitespace (or trim(chars))' },
  { label: 'replace', kind: 2, insertText: 'replace(${1:"search"}, ${2:"replacement"})', insertTextRules: 4, documentation: 'Replace occurrences' },
  { label: 'split', kind: 2, insertText: 'split(${1:","})', insertTextRules: 4, documentation: 'Split into array' },
  { label: 'substring', kind: 2, insertText: 'substring(${1:start}, ${2:length})', insertTextRules: 4, documentation: 'Extract substring' },
  { label: 'contains', kind: 2, insertText: 'contains(${1:"text"})', insertTextRules: 4, documentation: 'Contains substring (case-insensitive)' },
  { label: 'startsWith', kind: 2, insertText: 'startsWith(${1:"prefix"})', insertTextRules: 4, documentation: 'Starts with prefix' },
  { label: 'endsWith', kind: 2, insertText: 'endsWith(${1:"suffix"})', insertTextRules: 4, documentation: 'Ends with suffix' },
  { label: 'left', kind: 2, insertText: 'left(${1:n})', insertTextRules: 4, documentation: 'First n characters' },
  { label: 'right', kind: 2, insertText: 'right(${1:n})', insertTextRules: 4, documentation: 'Last n characters' },
  { label: 'length', kind: 2, insertText: 'length()', documentation: 'String or array length' },
  { label: 'toCharArray', kind: 2, insertText: 'toCharArray()', documentation: 'Convert to character array' },
  { label: 'regex', kind: 2, insertText: 'regex(${1:"pattern"})', insertTextRules: 4, documentation: 'Extract regex matches' },
  { label: 'urlDecode', kind: 2, insertText: 'urlDecode()', documentation: 'URL-decode %XX sequences' },
  { label: 'urlEncode', kind: 2, insertText: 'urlEncode()', documentation: 'URL-encode special characters' },
  // Numeric
  { label: 'round', kind: 2, insertText: 'round()', documentation: 'Round (optional decimals, mode)' },
  { label: 'floor', kind: 2, insertText: 'floor()', documentation: 'Round down' },
  { label: 'ceiling', kind: 2, insertText: 'ceiling()', documentation: 'Round up' },
  { label: 'truncate', kind: 2, insertText: 'truncate()', documentation: 'Remove decimal part' },
  { label: 'abs', kind: 2, insertText: 'abs()', documentation: 'Absolute value' },
  // Conversion
  { label: 'toString', kind: 2, insertText: 'toString()', documentation: 'Convert to string (optional format)' },
  { label: 'toNumber', kind: 2, insertText: 'toNumber()', documentation: 'Parse to number' },
  { label: 'convertTo', kind: 2, insertText: 'convertTo(${1:"Int32"})', insertTextRules: 4, documentation: 'Convert type (Int32, Double, Boolean, String)' },
  // Null checks
  { label: 'isNullOrEmpty', kind: 2, insertText: 'isNullOrEmpty()', documentation: 'True if null/empty (optional fallback arg)' },
  { label: 'isNull', kind: 2, insertText: 'isNull()', documentation: 'True if null' },
  // Object
  { label: 'keep', kind: 2, insertText: 'keep(${1:"prop1"}, ${2:"prop2"})', insertTextRules: 4, documentation: 'Keep only named properties' },
  { label: 'remove', kind: 2, insertText: 'remove(${1:"prop1"})', insertTextRules: 4, documentation: 'Remove named properties' },
  { label: 'clone', kind: 2, insertText: 'clone()', documentation: 'Deep copy' },
  // Membership
  { label: 'in', kind: 2, insertText: 'in(${1:array})', insertTextRules: 4, documentation: 'Check if value exists in array' },
  // DateTime
  { label: 'dateFormat', kind: 2, insertText: 'dateFormat(${1:"yyyy-MM-dd"})', insertTextRules: 4, documentation: 'Format date string' },
  { label: 'add', kind: 2, insertText: 'add(${1:"1.00:00:00"})', insertTextRules: 4, documentation: 'Add timespan to date' },
  { label: 'toUnixTimeSeconds', kind: 2, insertText: 'toUnixTimeSeconds()', documentation: 'Convert to Unix timestamp' },
  // Hash
  { label: 'hash', kind: 2, insertText: 'hash()', documentation: 'MD5 hash (optional length)' },
  { label: 'not', kind: 2, insertText: 'not()', documentation: 'Negate truthiness' },
  { label: 'first', kind: 2, insertText: 'first()', documentation: 'First element of array' },
  { label: 'last', kind: 2, insertText: 'last()', documentation: 'Last element of array' },
  { label: 'count', kind: 2, insertText: 'count()', documentation: 'Number of items' },
].map(c => ({ ...c, range: undefined as any }));

export function registerElwoodLanguage(monaco: typeof import('monaco-editor')) {
  // Register language
  monaco.languages.register({ id: ELWOOD_LANGUAGE_ID });

  // Tokenizer
  monaco.languages.setMonarchTokensProvider(ELWOOD_LANGUAGE_ID, monarchTokensProvider);

  // Language configuration (brackets, comments, etc.)
  monaco.languages.setLanguageConfiguration(ELWOOD_LANGUAGE_ID, languageConfiguration);

  // Completion provider
  monaco.languages.registerCompletionItemProvider(ELWOOD_LANGUAGE_ID, {
    triggerCharacters: ['.', '|', ' '],
    provideCompletionItems: (model, position) => {
      const textUntilPosition = model.getValueInRange({
        startLineNumber: position.lineNumber,
        startColumn: 1,
        endLineNumber: position.lineNumber,
        endColumn: position.column,
      });

      const word = model.getWordUntilPosition(position);
      const range = {
        startLineNumber: position.lineNumber,
        endLineNumber: position.lineNumber,
        startColumn: word.startColumn,
        endColumn: word.endColumn,
      };

      // After | → pipe operators
      if (/\|\s*\w*$/.test(textUntilPosition)) {
        return { suggestions: PIPE_OPERATOR_COMPLETIONS.map(c => ({ ...c, range })) };
      }

      // After . → methods
      if (/\.\w*$/.test(textUntilPosition)) {
        return { suggestions: METHOD_COMPLETIONS.map(c => ({ ...c, range })) };
      }

      // Default: keywords
      const keywordSuggestions: languages.CompletionItem[] = [
        { label: 'let', kind: 14, insertText: 'let ${1:name} = ${2:expression}', insertTextRules: 4, range, documentation: 'Bind a variable' },
        { label: 'return', kind: 14, insertText: 'return ', range, documentation: 'Return expression' },
        { label: 'if', kind: 14, insertText: 'if ${1:condition} then ${2:value} else ${3:other}', insertTextRules: 4, range, documentation: 'Conditional expression' },
        { label: 'memo', kind: 14, insertText: 'memo ${1:x} => ${2:expression}', insertTextRules: 4, range, documentation: 'Memoized function' },
        { label: 'range', kind: 1, insertText: 'range(${1:start}, ${2:count})', insertTextRules: 4, range, documentation: 'Generate numeric sequence' },
        { label: 'now', kind: 1, insertText: 'now(${1:"yyyy-MM-dd"})', insertTextRules: 4, range, documentation: 'Current UTC time' },
        { label: 'newGuid', kind: 1, insertText: 'newGuid()', range, documentation: 'Generate unique GUID' },
      ];

      return { suggestions: keywordSuggestions };
    },
  });
}
