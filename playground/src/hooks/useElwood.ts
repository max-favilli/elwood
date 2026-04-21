import { useState, useCallback, useRef, useEffect } from 'react';

// Dynamic import to handle the workspace dependency
let elwoodModule: typeof import('@elwood-lang/core') | null = null;

async function getElwood() {
  if (!elwoodModule) {
    elwoodModule = await import('@elwood-lang/core');
  }
  return elwoodModule;
}

export interface InlineDiagnostic {
  line: number;
  column: number;
  message: string;
}

export interface EvalResult {
  output: string;
  error: string | null;
  diagnostics: InlineDiagnostic[];
  timeMs: number;
  success: boolean;
}

export function useElwood(debounceMs = 300) {
  const [result, setResult] = useState<EvalResult>({
    output: '',
    error: null,
    diagnostics: [],
    timeMs: 0,
    success: true,
  });
  const [isRunning, setIsRunning] = useState(false);
  const timerRef = useRef<ReturnType<typeof setTimeout>>();

  const run = useCallback(async (expression: string, inputRaw: string, format: string = 'json') => {
    if (!expression.trim()) {
      setResult({ output: '', error: null, diagnostics: [], timeMs: 0, success: true });
      return;
    }

    let input: unknown;
    if (format === 'json') {
      try {
        input = JSON.parse(inputRaw);
      } catch (e: any) {
        setResult({ output: '', error: `Invalid input JSON: ${e.message}`, diagnostics: [], timeMs: 0, success: false });
        return;
      }
    } else {
      // CSV, TXT, XML — pass as raw string
      input = inputRaw;
    }

    setIsRunning(true);
    try {
      const elwood = await getElwood();
      const isScript = expression.trimStart().startsWith('let ') ||
                       expression.includes('\nlet ') ||
                       expression.includes('return ');

      const start = performance.now();
      const evalResult = isScript
        ? elwood.execute(expression, input)
        : elwood.evaluate(expression.trim(), input);
      const timeMs = performance.now() - start;

      if (evalResult.success) {
        const formatted = JSON.stringify(evalResult.value, null, 2) ?? 'null';
        setResult({ output: formatted, error: null, diagnostics: [], timeMs, success: true });
      } else {
        const inlineDiags: InlineDiagnostic[] = [];
        const errMsg = evalResult.diagnostics
          .filter((d: any) => d.severity === 'error')
          .map((d: any) => {
            const msg = d.message + (d.suggestion ? `\n${d.suggestion}` : '');
            if (d.line != null) {
              inlineDiags.push({ line: d.line, column: d.column ?? 1, message: d.message + (d.suggestion ? ` — ${d.suggestion}` : '') });
            } else {
              // Fallback: parse "line N, col M" from message
              const m = msg.match(/(?:line|Line)\s+(\d+)(?:[:,]\s*(?:col(?:umn)?\s*)?(\d+))?[:\s]*(.+)/s);
              if (m) {
                inlineDiags.push({ line: parseInt(m[1]), column: parseInt(m[2] ?? '1'), message: m[3]?.split('\n')[0]?.trim() ?? msg });
              } else {
                // Fallback: find quoted token in source text
                const quoted = msg.match(/'([^']+)'/);
                if (quoted) {
                  const idx = expression.indexOf(quoted[1]);
                  if (idx !== -1) {
                    const before = expression.slice(0, idx);
                    const ln = (before.match(/\n/g) || []).length + 1;
                    const col = idx - before.lastIndexOf('\n');
                    inlineDiags.push({ line: ln, column: col, message: msg.split('\n')[0] });
                  }
                }
              }
            }
            let display = '';
            if (d.line) display += `Line ${d.line}, Col ${d.column}: `;
            display += d.message;
            if (d.suggestion) display += `\n${d.suggestion}`;
            return display;
          })
          .join('\n');
        setResult({ output: '', error: errMsg || 'Unknown error', diagnostics: inlineDiags, timeMs, success: false });
      }
    } catch (e: any) {
      setResult({ output: '', error: e.message, diagnostics: [], timeMs: 0, success: false });
    } finally {
      setIsRunning(false);
    }
  }, []);

  const debouncedRun = useCallback((expression: string, inputRaw: string, format: string = 'json') => {
    if (timerRef.current) clearTimeout(timerRef.current);
    timerRef.current = setTimeout(() => run(expression, inputRaw, format), debounceMs);
  }, [run, debounceMs]);

  // Cleanup
  useEffect(() => () => { if (timerRef.current) clearTimeout(timerRef.current); }, []);

  return { result, isRunning, run, debouncedRun };
}
