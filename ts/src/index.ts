/**
 * Elwood — a functional JSON transformation DSL.
 *
 * This is the TypeScript implementation, behaviorally identical
 * to the .NET reference engine.
 */

import { parseExpression, parseScript, ParseError } from './parser.js';
import { evaluateExpression, evaluateScript } from './evaluator.js';
import type { Diagnostic } from './lexer.js';

export { TokenKind } from './token.js';
export type { Token, SourceSpan } from './token.js';
export type { ElwoodExpression, ScriptNode } from './ast.js';
export type { Diagnostic } from './lexer.js';
export { registerMethod } from './extensions.js';
export type { ElwoodMethodHandler } from './extensions.js';

export interface ElwoodResult {
  value: unknown;
  success: boolean;
  diagnostics: ElwoodDiagnostic[];
}

export interface ElwoodDiagnostic {
  severity: 'error' | 'warning' | 'info';
  message: string;
  line?: number;
  column?: number;
  suggestion?: string;
}

/**
 * Evaluate a single Elwood expression against input data.
 * Optional bindings inject named variables (e.g. $source, $root) into scope.
 */
export function evaluate(expression: string, input: unknown, bindings?: Record<string, unknown>): ElwoodResult {
  try {
    const { ast, diagnostics } = parseExpression(expression);
    if (diagnostics.some(d => d.severity === 'error')) {
      return { value: null, success: false, diagnostics: diagnostics.map(toDiag) };
    }
    const value = evaluateExpression(ast, input, bindings);
    return { value, success: true, diagnostics: diagnostics.map(toDiag) };
  } catch (err: any) {
    const diag = err instanceof ParseError
      ? toDiag(err.diagnostic)
      : { severity: 'error' as const, message: err.message };
    return { value: null, success: false, diagnostics: [diag] };
  }
}

/**
 * Execute an Elwood script (with let bindings and return) against input data.
 * Optional bindings inject named variables (e.g. $source, $root) into scope.
 */
export function execute(script: string, input: unknown, bindings?: Record<string, unknown>): ElwoodResult {
  try {
    const { ast, diagnostics } = parseScript(script);
    if (diagnostics.some(d => d.severity === 'error')) {
      return { value: null, success: false, diagnostics: diagnostics.map(toDiag) };
    }
    const value = evaluateScript(ast, input, bindings);
    return { value, success: true, diagnostics: diagnostics.map(toDiag) };
  } catch (err: any) {
    const diag = err instanceof ParseError
      ? toDiag(err.diagnostic)
      : { severity: 'error' as const, message: err.message };
    return { value: null, success: false, diagnostics: [diag] };
  }
}

function toDiag(d: Diagnostic): ElwoodDiagnostic {
  return {
    severity: d.severity,
    message: d.message,
    line: d.span?.line,
    column: d.span?.column,
  };
}
