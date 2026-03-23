/**
 * Build script: reads spec/test-cases/ and generates src/data/gallery.json
 * Run with: npx tsx scripts/build-examples.ts
 */

import { readFileSync, readdirSync, existsSync, writeFileSync, mkdirSync } from 'node:fs';
import { join, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const __filename = fileURLToPath(import.meta.url);
const __dirname = dirname(__filename);

const specDir = resolve(__dirname, '../../spec/test-cases');
const outDir = resolve(__dirname, '../src/data');
const outFile = join(outDir, 'gallery.json');

interface Example {
  id: string;
  title: string;
  category: string;
  script: string;
  input: string | null;
  inputFormat: 'json' | 'csv' | 'txt' | 'xml';
  expected: string | null;
  description: string;
  explanation: string;
  isBenchmark: boolean;
}

// Category mapping — manually curated
const CATEGORIES: Record<string, string> = {
  '01-property-access': 'Basics',
  '02-where-filter': 'Filtering',
  '03-select-object': 'Projection',
  '04-orderby': 'Sorting',
  '05-groupby-count': 'Grouping',
  '06-distinct': 'Filtering',
  '07-pattern-match': 'Pattern Matching',
  '08-arithmetic': 'Basics',
  '09-if-then-else': 'Conditionals',
  '10-string-methods': 'Strings',
  '11-interpolation': 'Strings',
  '12-script-let-return': 'Scripts',
  '13-batch': 'Collection',
  '14-boolean-logic': 'Filtering',
  '15-all-quantifier': 'Quantifiers',
  '16-all-quantifier-false': 'Quantifiers',
  '17-any-quantifier': 'Quantifiers',
  '18-select-batch': 'Collection',
  '19-distinct-count': 'Aggregation',
  '20-memo-function': 'Performance',
  '21-count': 'Aggregation',
  '22-distinct-concat': 'Strings',
  '23-first-char': 'Strings',
  '24-first-last-predicate': 'Filtering',
  '25-in-membership': 'Filtering',
  '26-index': 'Collection',
  '27-length': 'Aggregation',
  '28-select-vs-traditional-select': 'Projection',
  '29-selectmany': 'Projection',
  '30-range-skip': 'Collection',
  '31-orderby-multikey': 'Sorting',
  '32-urldecode-split-take': 'Strings',
  '33-urlencode': 'Strings',
  '34-where-complex': 'Filtering',
  '35-hash': 'Crypto',
  '36-rsa-sign': 'Crypto',
  '37-datetime-add': 'DateTime',
  '38-dateformat': 'DateTime',
  '39-datetime-functions': 'DateTime',
  '40-unix-timestamp-arg': 'DateTime',
  '41-trydateformat': 'DateTime',
  '42-boolean-expression': 'Conditionals',
  '43-if-then-else-complex': 'Conditionals',
  '44-not': 'Type Conversion',
  '45-ceiling': 'Numeric',
  '46-floor': 'Numeric',
  '47-round-modes': 'Numeric',
  '48-sum': 'Aggregation',
  '49-truncate': 'Numeric',
  '50-min-max': 'Aggregation',
  '51-spread-operator': 'Objects',
  '52-clone-keep-remove': 'Objects',
  '53-slice': 'Collection',
  '54-concat-extended': 'Strings',
  '55-string-search': 'Strings',
  '56-graphql-string': 'Strings',
  '57-left-right': 'Strings',
  '58-let-replaces-set-get': 'Scripts',
  '59-case-trim-extended': 'Strings',
  '60-convertto': 'Type Conversion',
  '61-regex': 'Strings',
  '62-sanitize-nullchecks': 'Null Checks',
  '63-reduce': 'Aggregation',
  '64-join': 'Joins',
  '65-join-modes': 'Joins',
  '66-computed-keys': 'Objects',
  '67-benchmark-lazy-shortcircuit': 'Performance',
  '68-benchmark-50mb-complex': 'Performance',
  '69-iterate-basic': 'Collection',
  '70-iterate-state': 'Collection',
  '71-takewhile': 'Collection',
  '72-iterate-fibonacci': 'Collection',
  '73-fromcsv': 'Format I/O',
  '74-tocsv': 'Format I/O',
  '75-fromtext': 'Format I/O',
  '76-totext': 'Format I/O',
  '77-fromcsv-no-headers': 'Format I/O',
  '78-tocsv-always-quote': 'Format I/O',
  '79-parsejson': 'Type Conversion',
  '80-fromcsv-parsejson': 'Format I/O',
  '81-fromcsv-file': 'Format I/O',
  '82-fromtext-file': 'Format I/O',
  '83-fromxml': 'Format I/O',
  '84-toxml': 'Format I/O',
  '85-fromxml-file': 'Format I/O',
  '86-bracket-property-access': 'Basics',
};

function titleFromId(id: string): string {
  // "01-property-access" → "Property Access"
  return id.replace(/^\d+-/, '').split('-').map(w => w.charAt(0).toUpperCase() + w.slice(1)).join(' ');
}

function extractFirstParagraph(markdown: string): string {
  // Get first paragraph after ## Explanation or ## Expression
  const lines = markdown.split('\n');
  let capture = false;
  let result = '';
  for (const line of lines) {
    if (line.startsWith('## Explanation') || line.startsWith('## Expression')) {
      capture = true;
      continue;
    }
    if (capture) {
      if (line.startsWith('#') || line.startsWith('```')) break;
      if (line.trim() === '' && result) break;
      if (line.startsWith('- ') || line.startsWith('| ')) {
        result += line.replace(/^- /, '').replace(/\*\*/g, '') + ' ';
      }
    }
  }
  return result.trim().slice(0, 200) || titleFromId('');
}

const dirs = readdirSync(specDir, { withFileTypes: true })
  .filter(d => d.isDirectory())
  .map(d => d.name)
  .sort();

const examples: Example[] = [];

for (const dir of dirs) {
  const scriptPath = join(specDir, dir, 'script.elwood');
  const expectedPath = join(specDir, dir, 'expected.json');
  const explanationPath = join(specDir, dir, 'explanation.md');

  if (!existsSync(scriptPath)) continue;

  // Find input file — support json, csv, txt, xml
  const inputExtensions = ['.json', '.csv', '.txt', '.xml'];
  const inputExt = inputExtensions.find(ext => existsSync(join(specDir, dir, `input${ext}`)));
  const inputPath = inputExt ? join(specDir, dir, `input${inputExt}`) : null;

  const script = readFileSync(scriptPath, 'utf-8');
  const input = inputPath ? readFileSync(inputPath, 'utf-8') : null;
  const inputFormat = (inputExt?.slice(1) ?? 'json') as 'json' | 'csv' | 'txt' | 'xml';
  const expected = existsSync(expectedPath) ? readFileSync(expectedPath, 'utf-8') : null;
  const explanation = existsSync(explanationPath) ? readFileSync(explanationPath, 'utf-8') : '';
  const isBenchmark = inputPath === null || !existsSync(expectedPath);

  examples.push({
    id: dir,
    title: titleFromId(dir),
    category: CATEGORIES[dir] || 'Other',
    script,
    input,
    inputFormat,
    expected,
    description: extractFirstParagraph(explanation),
    explanation,
    isBenchmark,
  });
}

mkdirSync(outDir, { recursive: true });
writeFileSync(outFile, JSON.stringify(examples, null, 2));
console.log(`Generated ${examples.length} examples in ${outFile}`);
