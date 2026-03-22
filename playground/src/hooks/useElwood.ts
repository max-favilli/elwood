import { useState, useCallback, useRef, useEffect } from 'react';

// Dynamic import to handle the workspace dependency
let elwoodModule: typeof import('@elwood-lang/core') | null = null;

async function getElwood() {
  if (!elwoodModule) {
    elwoodModule = await import('@elwood-lang/core');
  }
  return elwoodModule;
}

export interface EvalResult {
  output: string;
  error: string | null;
  timeMs: number;
  success: boolean;
}

export function useElwood(debounceMs = 300) {
  const [result, setResult] = useState<EvalResult>({
    output: '',
    error: null,
    timeMs: 0,
    success: true,
  });
  const [isRunning, setIsRunning] = useState(false);
  const timerRef = useRef<ReturnType<typeof setTimeout>>();

  const run = useCallback(async (expression: string, inputJson: string) => {
    if (!expression.trim()) {
      setResult({ output: '', error: null, timeMs: 0, success: true });
      return;
    }

    let input: unknown;
    try {
      input = JSON.parse(inputJson);
    } catch (e: any) {
      setResult({ output: '', error: `Invalid input JSON: ${e.message}`, timeMs: 0, success: false });
      return;
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
        setResult({ output: formatted, error: null, timeMs, success: true });
      } else {
        const errMsg = evalResult.diagnostics
          .filter((d: any) => d.severity === 'error')
          .map((d: any) => {
            let msg = '';
            if (d.line) msg += `Line ${d.line}, Col ${d.column}: `;
            msg += d.message;
            if (d.suggestion) msg += `\n${d.suggestion}`;
            return msg;
          })
          .join('\n');
        setResult({ output: '', error: errMsg || 'Unknown error', timeMs, success: false });
      }
    } catch (e: any) {
      setResult({ output: '', error: e.message, timeMs: 0, success: false });
    } finally {
      setIsRunning(false);
    }
  }, []);

  const debouncedRun = useCallback((expression: string, inputJson: string) => {
    if (timerRef.current) clearTimeout(timerRef.current);
    timerRef.current = setTimeout(() => run(expression, inputJson), debounceMs);
  }, [run, debounceMs]);

  // Cleanup
  useEffect(() => () => { if (timerRef.current) clearTimeout(timerRef.current); }, []);

  return { result, isRunning, run, debouncedRun };
}
