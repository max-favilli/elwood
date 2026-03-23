import type {
  ElwoodExpression, ScriptNode, PipeOperation, PathSegment,
  MatchArm, InterpolationPart, JoinMode, LambdaExpression,
} from './ast.js';
import { parseExpression } from './parser.js';
import { Scope } from './scope.js';

// ── Helpers ──

function isArray(v: unknown): v is unknown[] { return Array.isArray(v); }
function isObject(v: unknown): v is Record<string, unknown> {
  return v !== null && typeof v === 'object' && !Array.isArray(v);
}

function toArray(v: unknown): unknown[] {
  if (isArray(v)) return v;
  return [v];
}

function isTruthy(v: unknown): boolean {
  if (v === null || v === undefined) return false;
  if (typeof v === 'boolean') return v;
  if (typeof v === 'number') return v !== 0;
  if (typeof v === 'string') return v !== '';
  if (isArray(v)) return v.length > 0;
  return true;
}

function valuesEqual(a: unknown, b: unknown): boolean {
  if (a === b) return true;
  if (a === null || b === null) return a === b;
  if (typeof a !== typeof b) return false;
  if (typeof a === 'number' && typeof b === 'number') return Math.abs(a - b) < 1e-10;
  if (isArray(a) && isArray(b)) {
    if (a.length !== b.length) return false;
    return a.every((v, i) => valuesEqual(v, b[i]));
  }
  if (isObject(a) && isObject(b)) {
    const ka = Object.keys(a), kb = Object.keys(b);
    if (ka.length !== kb.length) return false;
    return ka.every(k => valuesEqual(a[k], b[k]));
  }
  return false;
}

function serialize(v: unknown): string {
  if (v === null || v === undefined) return 'null';
  if (typeof v === 'string') return JSON.stringify(v);
  if (typeof v === 'number' || typeof v === 'boolean') return String(v);
  return JSON.stringify(v);
}

function valueToString(v: unknown): string {
  if (v === null || v === undefined) return '';
  if (typeof v === 'string') return v;
  if (typeof v === 'number') return String(v);
  if (typeof v === 'boolean') return v ? 'true' : 'false';
  return JSON.stringify(v);
}

function getProperty(obj: unknown, name: string): unknown {
  if (isObject(obj)) return (obj as any)[name] ?? null;
  // Auto-map over arrays
  if (isArray(obj)) return obj.map(item => getProperty(item, name)).filter(v => v !== null);
  return null;
}

function levenshtein(a: string, b: string): number {
  const m = a.length, n = b.length;
  const d: number[][] = Array.from({ length: m + 1 }, () => Array(n + 1).fill(0));
  for (let i = 0; i <= m; i++) d[i][0] = i;
  for (let j = 0; j <= n; j++) d[0][j] = j;
  for (let i = 1; i <= m; i++)
    for (let j = 1; j <= n; j++)
      d[i][j] = Math.min(d[i-1][j]+1, d[i][j-1]+1, d[i-1][j-1]+(a[i-1]===b[j-1]?0:1));
  return d[m][n];
}

function suggestProperty(attempted: string, obj: unknown): string | undefined {
  if (!isObject(obj)) return undefined;
  const names = Object.keys(obj);
  if (names.length === 0) return undefined;
  const closest = names
    .map(n => ({ n, d: levenshtein(attempted.toLowerCase(), n.toLowerCase()) }))
    .filter(x => x.d <= 3)
    .sort((a, b) => a.d - b.d)[0];
  if (closest) return `Did you mean '${closest.n}'? Available: ${names.slice(0, 10).join(', ')}`;
  return `Available properties: ${names.slice(0, 10).join(', ')}`;
}

// ── Memoized Function ──

class MemoizedFunction {
  private cache = new Map<string, unknown>();
  constructor(
    private lambda: LambdaExpression,
    private closure: Scope,
    private evalFn: (expr: ElwoodExpression, current: unknown, scope: Scope) => unknown,
  ) {}

  invoke(args: unknown[], _current: unknown): unknown {
    const key = args.map(serialize).join('|');
    if (this.cache.has(key)) return this.cache.get(key)!;
    const childScope = this.closure.child();
    for (let i = 0; i < this.lambda.parameters.length && i < args.length; i++)
      childScope.set(this.lambda.parameters[i], args[i]);
    const root = this.closure.get('$root') ?? _current;
    const result = this.evalFn(this.lambda.body, root, childScope);
    this.cache.set(key, result);
    return result;
  }
}

// ── Main Evaluator ──

export function evaluateExpression(expr: ElwoodExpression, input: unknown): unknown {
  const scope = new Scope();
  scope.set('$root', input);
  return evaluate(expr, input, scope);
}

export function evaluateScript(script: ScriptNode, input: unknown): unknown {
  const scope = new Scope();
  scope.set('$root', input);
  for (const binding of script.bindings) {
    scope.set(binding.name, evaluate(binding.value, input, scope));
  }
  if (script.returnExpression) return evaluate(script.returnExpression, input, scope);
  return null;
}

function evaluate(expr: ElwoodExpression, current: unknown, scope: Scope): unknown {
  switch (expr.type) {
    case 'Literal': return expr.value;
    case 'Path': return evalPath(expr, current, scope);
    case 'Identifier': return evalIdentifier(expr, scope);
    case 'Binary': return evalBinary(expr, current, scope);
    case 'Unary': return evalUnary(expr, current, scope);
    case 'If': return isTruthy(evaluate(expr.condition, current, scope))
      ? evaluate(expr.thenBranch, current, scope)
      : evaluate(expr.elseBranch, current, scope);
    case 'Object': return evalObject(expr, current, scope);
    case 'Array': return expr.items.map(i => evaluate(i, current, scope));
    case 'Pipeline': return evalPipeline(expr, current, scope);
    case 'MemberAccess': return evalMemberAccess(expr, current, scope);
    case 'MethodCall': return evalMethodCall(expr, current, scope);
    case 'FunctionCall': return evalFunctionCall(expr, current, scope);
    case 'Index': return evalIndex(expr, current, scope);
    case 'InterpolatedString': return evalInterpolation(expr, current, scope);
    case 'Match': return evalMatch(expr, current, scope);
    case 'Memo': return new MemoizedFunction(expr.lambda, scope, evaluate);
    case 'Lambda': throw new Error('Lambda cannot be evaluated directly');
  }
}

// ── Path evaluation ──

function evalPath(expr: import('./ast.js').PathExpression, current: unknown, scope: Scope): unknown {
  let value = expr.isRooted ? (scope.get('$root') ?? current) : current;
  for (const seg of expr.segments) {
    switch (seg.type) {
      case 'Property':
        if (isArray(value)) {
          value = value.map(item => isObject(item) ? (item as any)[seg.name] : null).filter(v => v !== null);
        } else if (isObject(value)) {
          const prop = (value as any)[seg.name];
          if (prop === undefined) {
            const suggestion = suggestProperty(seg.name, value);
            throw new Error(`Property '${seg.name}' not found on Object.${suggestion ? ' ' + suggestion : ''}`);
          }
          value = prop;
        } else {
          return null;
        }
        break;
      case 'Index':
        if (seg.index === null) value = toArray(value); // [*]
        else value = toArray(value)[seg.index] ?? null;
        break;
      case 'Slice': {
        const arr = toArray(value);
        let s = seg.start ?? 0, e = seg.end ?? arr.length;
        if (s < 0) s = Math.max(0, arr.length + s);
        if (e < 0) e = Math.max(0, arr.length + e);
        value = arr.slice(s, e);
        break;
      }
      case 'RecursiveDescent': {
        const results: unknown[] = [];
        collectRecursive(value, seg.name, results);
        value = results;
        break;
      }
    }
  }
  return value;
}

function collectRecursive(value: unknown, name: string, results: unknown[]): void {
  if (isObject(value)) {
    if (name in value) results.push((value as any)[name]);
    for (const k of Object.keys(value)) collectRecursive((value as any)[k], name, results);
  } else if (isArray(value)) {
    for (const item of value) collectRecursive(item, name, results);
  }
}

function evalIdentifier(expr: import('./ast.js').IdentifierExpression, scope: Scope): unknown {
  const val = scope.get(expr.name);
  if (val === undefined) throw new Error(`Undefined variable '${expr.name}'.`);
  return val;
}

// ── Binary / Unary ──

function evalBinary(expr: import('./ast.js').BinaryExpression, current: unknown, scope: Scope): unknown {
  const left = evaluate(expr.left, current, scope);
  const right = evaluate(expr.right, current, scope);
  switch (expr.operator) {
    case 'Add':
      if (typeof left === 'string' || typeof right === 'string') return valueToString(left) + valueToString(right);
      return (left as number) + (right as number);
    case 'Subtract': return (left as number) - (right as number);
    case 'Multiply': return (left as number) * (right as number);
    case 'Divide': return (right as number) !== 0 ? (left as number) / (right as number) : 0;
    case 'Equal': return valuesEqual(left, right);
    case 'NotEqual': return !valuesEqual(left, right);
    case 'LessThan':
      if (typeof left === 'string' && typeof right === 'string') return left < right;
      return (left as number) < (right as number);
    case 'LessThanOrEqual':
      if (typeof left === 'string' && typeof right === 'string') return left <= right;
      return (left as number) <= (right as number);
    case 'GreaterThan':
      if (typeof left === 'string' && typeof right === 'string') return left > right;
      return (left as number) > (right as number);
    case 'GreaterThanOrEqual':
      if (typeof left === 'string' && typeof right === 'string') return left >= right;
      return (left as number) >= (right as number);
    case 'And': return isTruthy(left) && isTruthy(right);
    case 'Or': return isTruthy(left) || isTruthy(right);
  }
}

function evalUnary(expr: import('./ast.js').UnaryExpression, current: unknown, scope: Scope): unknown {
  const operand = evaluate(expr.operand, current, scope);
  return expr.operator === 'Not' ? !isTruthy(operand) : -(operand as number);
}

// ── Object ──

function evalObject(expr: import('./ast.js').ObjectExpression, current: unknown, scope: Scope): unknown {
  const result: Record<string, unknown> = {};
  for (const p of expr.properties) {
    if (p.isSpread) {
      const spread = evaluate(p.value, current, scope);
      if (isObject(spread)) Object.assign(result, spread);
    } else if (p.computedKey) {
      const key = valueToString(evaluate(p.computedKey, current, scope));
      result[key] = evaluate(p.value, current, scope);
    } else {
      result[p.key] = evaluate(p.value, current, scope);
    }
  }
  return result;
}

// ── Pipeline ──

function evalPipeline(expr: import('./ast.js').PipelineExpression, current: unknown, scope: Scope): unknown {
  let value = evaluate(expr.source, current, scope);
  for (const op of expr.operations) {
    value = evalPipeOp(op, value, scope);
  }
  return value;
}

function evalWithLambdaOrImplicit(expr: ElwoodExpression, item: unknown, scope: Scope): unknown {
  if (expr.type === 'Lambda') {
    const child = scope.child();
    if (expr.parameters.length >= 1) child.set(expr.parameters[0], item);
    child.set('$root', item);
    return evaluate(expr.body, item, child);
  }
  const child = scope.child();
  child.set('$root', item);
  return evaluate(expr, item, child);
}

function evalPipeOp(op: PipeOperation, input: unknown, scope: Scope): unknown {
  const items = toArray(input);
  switch (op.type) {
    case 'Where': return items.filter(item => isTruthy(evalWithLambdaOrImplicit(op.predicate, item, scope)));
    case 'Select': return items.map(item => evalWithLambdaOrImplicit(op.projection, item, scope));
    case 'SelectMany': return items.flatMap(item => {
      const r = evalWithLambdaOrImplicit(op.projection, item, scope);
      return isArray(r) ? r : [r];
    });
    case 'Distinct': {
      const seen = new Set<string>();
      return items.filter(item => { const k = serialize(item); if (seen.has(k)) return false; seen.add(k); return true; });
    }
    case 'Aggregate': return evalAggregate(op, items, scope);
    case 'Slice':
      return op.kind === 'take' ? items.slice(0, evaluate(op.count, input, scope) as number)
        : items.slice(evaluate(op.count, input, scope) as number);
    case 'TakeWhile': {
      const result: unknown[] = [];
      for (const item of items) {
        if (!isTruthy(evalWithLambdaOrImplicit(op.predicate, item, scope))) break;
        result.push(item);
      }
      return result;
    }
    case 'OrderBy': return evalOrderBy(op, items, scope);
    case 'GroupBy': return evalGroupBy(op, items, scope);
    case 'Batch': {
      const size = evaluate(op.size, input, scope) as number;
      const batches: unknown[][] = [];
      for (let i = 0; i < items.length; i += size) batches.push(items.slice(i, i + size));
      return batches;
    }
    case 'Concat': {
      const sep = op.separator ? valueToString(evaluate(op.separator, input, scope)) : '|';
      return items.map(valueToString).join(sep);
    }
    case 'Reduce': return evalReduce(op, items, scope);
    case 'Join': return evalJoin(op, items, scope);
    case 'Quantifier':
      return op.kind === 'all'
        ? items.every(item => isTruthy(evalWithLambdaOrImplicit(op.predicate, item, scope)))
        : items.some(item => isTruthy(evalWithLambdaOrImplicit(op.predicate, item, scope)));
    case 'MatchOp': return evalMatchArms(op.arms, input, scope);
  }
}

function evalAggregate(op: import('./ast.js').AggregateOperation, items: unknown[], scope: Scope): unknown {
  if (op.name === 'first') {
    if (op.predicate) {
      return items.find(item => isTruthy(evalWithLambdaOrImplicit(op.predicate!, item, scope))) ?? null;
    }
    return items[0] ?? null;
  }
  if (op.name === 'last' && op.predicate) {
    return [...items].reverse().find(item => isTruthy(evalWithLambdaOrImplicit(op.predicate!, item, scope))) ?? null;
  }
  switch (op.name) {
    case 'count': return items.length;
    case 'last': return items[items.length - 1] ?? null;
    case 'sum': return items.reduce((a, b) => (a as number) + (b as number), 0);
    case 'min': return Math.min(...items.map(Number));
    case 'max': return Math.max(...items.map(Number));
    case 'index': return items.map((_, i) => i);
    default: throw new Error(`Unknown aggregate: ${op.name}`);
  }
}

function evalOrderBy(op: import('./ast.js').OrderByOperation, items: unknown[], scope: Scope): unknown[] {
  return [...items].sort((a, b) => {
    for (const { key, ascending } of op.keys) {
      const ka = evalWithLambdaOrImplicit(key, a, scope);
      const kb = evalWithLambdaOrImplicit(key, b, scope);
      let cmp = 0;
      if (typeof ka === 'string' && typeof kb === 'string') cmp = ka.localeCompare(kb);
      else if (typeof ka === 'number' && typeof kb === 'number') cmp = ka - kb;
      else cmp = String(ka).localeCompare(String(kb));
      if (cmp !== 0) return ascending ? cmp : -cmp;
    }
    return 0;
  });
}

function evalGroupBy(op: import('./ast.js').GroupByOperation, items: unknown[], scope: Scope): unknown[] {
  const groups = new Map<string, { key: unknown; items: unknown[] }>();
  for (const item of items) {
    const key = evalWithLambdaOrImplicit(op.keySelector, item, scope);
    const keyStr = serialize(key);
    if (!groups.has(keyStr)) groups.set(keyStr, { key, items: [] });
    groups.get(keyStr)!.items.push(item);
  }
  return [...groups.values()];
}

function evalReduce(op: import('./ast.js').ReduceOperation, items: unknown[], scope: Scope): unknown {
  if (items.length === 0) return op.initialValue ? evaluate(op.initialValue, null, scope) : null;
  const lambda = op.accumulator;
  if (lambda.type !== 'Lambda' || lambda.parameters.length < 2) throw new Error('reduce requires (acc, item) => expr');
  let acc: unknown;
  let startIdx: number;
  if (op.initialValue) { acc = evaluate(op.initialValue, null, scope); startIdx = 0; }
  else { acc = items[0]; startIdx = 1; }
  for (let i = startIdx; i < items.length; i++) {
    const child = scope.child();
    child.set(lambda.parameters[0], acc);
    child.set(lambda.parameters[1], items[i]);
    acc = evaluate(lambda.body, items[i], child);
  }
  return acc;
}

function evalJoin(op: import('./ast.js').JoinOperation, leftItems: unknown[], scope: Scope): unknown[] {
  const root = scope.get('$root');
  const rightItems = toArray(evaluate(op.source, root, scope));
  const rightLookup = new Map<string, unknown[]>();
  for (const r of rightItems) {
    const key = serialize(evalWithLambdaOrImplicit(op.rightKey, r, scope));
    if (!rightLookup.has(key)) rightLookup.set(key, []);
    rightLookup.get(key)!.push(r);
  }
  const matchedRight = new Set<string>();
  const results: unknown[] = [];
  for (const l of leftItems) {
    const key = serialize(evalWithLambdaOrImplicit(op.leftKey, l, scope));
    const matches = rightLookup.get(key);
    if (matches) {
      matchedRight.add(key);
      for (const r of matches) results.push(mergeJoin(l, r, op.intoAlias));
    } else if (op.mode === 'left' || op.mode === 'full') {
      results.push(mergeJoin(l, null, op.intoAlias));
    }
  }
  if (op.mode === 'right' || op.mode === 'full') {
    for (const r of rightItems) {
      const key = serialize(evalWithLambdaOrImplicit(op.rightKey, r, scope));
      if (!matchedRight.has(key)) results.push(mergeJoin(null, r, op.intoAlias));
    }
  }
  return results;
}

function mergeJoin(left: unknown, right: unknown, alias?: string): unknown {
  const result: Record<string, unknown> = {};
  if (isObject(left)) Object.assign(result, left);
  if (alias) { result[alias] = right; }
  else if (isObject(right)) {
    for (const [k, v] of Object.entries(right)) if (!(k in result)) result[k] = v;
  }
  return result;
}

// ── Match ──

function evalMatch(expr: import('./ast.js').MatchExpression, current: unknown, scope: Scope): unknown {
  const input = evaluate(expr.input, current, scope);
  return evalMatchArms(expr.arms, input, scope);
}

function evalMatchArms(arms: MatchArm[], input: unknown, scope: Scope): unknown {
  for (const arm of arms) {
    if (arm.pattern === null) return evaluate(arm.result, input, scope);
    const pattern = evaluate(arm.pattern, input, scope);
    if (valuesEqual(input, pattern)) return evaluate(arm.result, input, scope);
  }
  return null;
}

// ── Member access / Method call / Function call / Index ──

function evalMemberAccess(expr: import('./ast.js').MemberAccessExpression, current: unknown, scope: Scope): unknown {
  const target = evaluate(expr.target, current, scope);
  if (isObject(target)) return (target as any)[expr.memberName] ?? null;
  return null;
}

function evalMethodCall(expr: import('./ast.js').MethodCallExpression, current: unknown, scope: Scope): unknown {
  const target = evaluate(expr.target, current, scope);
  const args = expr.arguments.map(a => evaluate(a, current, scope));
  return callBuiltin(expr.methodName, target, args, scope);
}

function evalFunctionCall(expr: import('./ast.js').FunctionCallExpression, current: unknown, scope: Scope): unknown {
  // Check for memoized functions
  const funcVal = scope.get(expr.functionName);
  if (funcVal instanceof MemoizedFunction) {
    const args = expr.arguments.map(a => evaluate(a, current, scope));
    return funcVal.invoke(args, current);
  }

  // iterate(seed, lambda) — needs raw lambda AST
  if (expr.functionName === 'iterate' && expr.arguments.length >= 2) {
    return evalIterate(expr, current, scope);
  }

  const args = expr.arguments.map(a => evaluate(a, current, scope));
  return callBuiltin(expr.functionName, current, args, scope);
}

function evalIterate(expr: import('./ast.js').FunctionCallExpression, current: unknown, scope: Scope): unknown[] {
  const seed = evaluate(expr.arguments[0], current, scope);
  const lambdaExpr = expr.arguments[1];
  if (lambdaExpr.type !== 'Lambda') throw new Error("iterate requires a lambda as second argument: iterate(seed, x => expr)");
  const lambda = lambdaExpr;

  // TS limitation: without generators, iterate eagerly generates items.
  // The pipeline's take/takeWhile will slice the result.
  // Safety limit prevents runaway memory usage.
  const maxIterations = 10_000;
  const results: unknown[] = [];
  let val = seed;

  for (let i = 0; i < maxIterations; i++) {
    results.push(val);
    const childScope = scope.child();
    childScope.set(lambda.parameters[0], val);
    val = evaluate(lambda.body, val, childScope);
  }

  return results;
}

function evalIndex(expr: import('./ast.js').IndexExpression, current: unknown, scope: Scope): unknown {
  const target = evaluate(expr.target, current, scope);
  if (expr.index === null) return toArray(target);
  const idx = evaluate(expr.index, current, scope);

  // String index on object → property access (e.g., obj["@id"])
  if (typeof idx === 'string' && isObject(target)) return (target as any)[idx] ?? null;

  return toArray(target)[idx as number] ?? null;
}

function evalInterpolation(expr: import('./ast.js').InterpolatedStringExpression, current: unknown, scope: Scope): unknown {
  return expr.parts.map(p =>
    p.type === 'Text' ? p.text : valueToString(evaluate(p.expression, current, scope))
  ).join('');
}

// ── Built-in Methods ──

function callBuiltin(name: string, target: unknown, args: unknown[], _scope?: Scope): unknown {
  const str = () => typeof target === 'string' ? target : valueToString(target);
  const num = () => typeof target === 'number' ? target : Number(target) || 0;
  const arr = () => toArray(target);

  switch (name) {
    // String
    case 'toLower': {
      if (args.length === 1 && typeof args[0] === 'number') {
        const s = str(), pos = (args[0] as number) - 1;
        return pos >= 0 && pos < s.length ? s.slice(0, pos) + s[pos].toLowerCase() + s.slice(pos + 1) : s;
      }
      return str().toLowerCase();
    }
    case 'toUpper': {
      if (args.length === 1 && typeof args[0] === 'number') {
        const s = str(), pos = (args[0] as number) - 1;
        return pos >= 0 && pos < s.length ? s.slice(0, pos) + s[pos].toUpperCase() + s.slice(pos + 1) : s;
      }
      return str().toUpperCase();
    }
    case 'trim': return args.length > 0 ? trimChars(str(), valueToString(args[0])) : str().trim();
    case 'trimStart': return args.length > 0 ? trimStartChars(str(), valueToString(args[0])) : str().trimStart();
    case 'trimEnd': return args.length > 0 ? trimEndChars(str(), valueToString(args[0])) : str().trimEnd();
    case 'left': { const s = str(), n = args.length > 0 ? Number(args[0]) : 1; return s.slice(0, Math.min(n, s.length)); }
    case 'right': { const s = str(), n = args.length > 0 ? Number(args[0]) : 1; return s.slice(-Math.min(n, s.length)); }
    case 'padLeft': return str().padStart(Number(args[0]), args.length > 1 ? valueToString(args[1]) : ' ');
    case 'padRight': return str().padEnd(Number(args[0]), args.length > 1 ? valueToString(args[1]) : ' ');
    case 'contains': return str().toLowerCase().includes(valueToString(args[0]).toLowerCase());
    case 'startsWith': return str().toLowerCase().startsWith(valueToString(args[0]).toLowerCase());
    case 'endsWith': return str().toLowerCase().endsWith(valueToString(args[0]).toLowerCase());
    case 'replace': {
      const s = str(), search = valueToString(args[0]), repl = args.length > 1 ? valueToString(args[1]) : '';
      const caseInsensitive = args.length > 2 && isTruthy(args[2]);
      if (caseInsensitive) return s.replace(new RegExp(escapeRegex(search), 'gi'), repl);
      return s.split(search).join(repl);
    }
    case 'substring': {
      const s = str(), start = Number(args[0]);
      return args.length > 1 ? s.substring(start, start + Number(args[1])) : s.substring(start);
    }
    case 'split': return str().split(valueToString(args[0]));
    case 'length':
      return isArray(target) ? target.length : str().length;
    case 'toCharArray': return [...str()];
    case 'regex': return [...str().matchAll(new RegExp(valueToString(args[0]), 'g'))].map(m => m[0]);
    case 'urlDecode': return decodeURIComponent(str());
    case 'urlEncode': return encodeURIComponent(str());
    case 'sanitize': return sanitize(str());
    case 'concat': return concatMethod(target, args);

    // Membership
    case 'in': {
      const candidates = args.flatMap(a => isArray(a) ? a : [a]);
      return candidates.some(c => valuesEqual(target, c));
    }

    // Object manipulation
    case 'clone': return JSON.parse(JSON.stringify(target));
    case 'keep': {
      const names = new Set(args.map(a => valueToString(a)));
      if (isObject(target)) return Object.fromEntries(Object.entries(target).filter(([k]) => names.has(k)));
      if (isArray(target)) return target.map(item => isObject(item) ? Object.fromEntries(Object.entries(item).filter(([k]) => names.has(k))) : item);
      return target;
    }
    case 'remove': {
      const names = new Set(args.map(a => valueToString(a)));
      if (isObject(target)) return Object.fromEntries(Object.entries(target).filter(([k]) => !names.has(k)));
      if (isArray(target)) return target.map(item => isObject(item) ? Object.fromEntries(Object.entries(item).filter(([k]) => !names.has(k))) : item);
      return target;
    }

    // Collection
    case 'count': return isArray(target) ? target.length : 1;
    case 'first': return isArray(target) ? target[0] ?? null : target;
    case 'last': return isArray(target) ? target[target.length - 1] ?? null : target;
    case 'sum': return arr().reduce((a, b) => (a as number) + Number(b), 0);
    case 'min': return Math.min(...arr().map(Number));
    case 'max': return Math.max(...arr().map(Number));
    case 'index': return isArray(target) ? target.map((_, i) => i) : 0;
    case 'take': return arr().slice(0, Number(args[0]));
    case 'skip': return arr().slice(Number(args[0]));

    // Null checks
    case 'isNull': { const empty = target === null || target === undefined; return args.length > 0 ? (empty ? args[0] : target) : empty; }
    case 'isEmpty': case 'isNullOrEmpty': {
      const empty = target === null || target === undefined || target === '' || (isArray(target) && target.length === 0);
      return args.length > 0 ? (empty ? args[0] : target) : empty;
    }
    case 'isNullOrWhiteSpace': {
      const empty = target === null || target === undefined ||
        (typeof target === 'string' && target.trim() === '') ||
        (isArray(target) && target.length === 0);
      return args.length > 0 ? (empty ? args[0] : target) : empty;
    }

    // Type conversion
    case 'not': return !isTruthy(target);
    case 'boolean': return args.length > 0 ? isTruthy(args[0]) : isTruthy(target);
    case 'toString': {
      if (args.length > 0 && typeof target === 'number') {
        // Basic format support
        return String(target);
      }
      return valueToString(target);
    }
    case 'toNumber': return typeof target === 'number' ? target : (parseFloat(str()) || 0);
    case 'convertTo': return convertTo(target, args);
    case 'parseJson': {
      const s = str();
      if (!s) return null;
      try { return JSON.parse(s); } catch { return null; }
    }

    // Math
    case 'round': return evalRound(num(), args);
    case 'floor': return Math.floor(num());
    case 'ceiling': return Math.ceil(num());
    case 'truncate': return Math.trunc(num());
    case 'abs': return Math.abs(num());

    // DateTime
    case 'add': return evalDateAdd(target, args);
    case 'dateFormat': case 'tryDateFormat': return evalDateFormat(target, args);
    case 'toUnixTimeSeconds': return evalToUnixTime(target, args);
    case 'now': return evalNow(args);
    case 'utcNow': case 'utcnow': return evalNow(args);

    // Generators
    case 'range': return Array.from({ length: Number(args[1]) }, (_, i) => i + Number(args[0]));
    case 'newGuid': case 'newguid': return crypto.randomUUID();

    // Format I/O
    case 'fromCsv': return evalFromCsv(str(), args);
    case 'toCsv': return evalToCsv(target, args);
    case 'fromXml': return evalFromXml(str(), args);
    case 'toXml': return evalToXml(target, args);
    case 'fromText': return evalFromText(str(), args);
    case 'toText': return evalToText(target, args);

    // Crypto
    case 'hash': return evalHash(str(), args);
    case 'rsaSign': return evalRsaSign(target, args);

    default: throw new Error(`Unknown method '${name}'.`);
  }
}

// ── Helper functions for built-ins ──

function escapeRegex(s: string): string { return s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'); }

function trimChars(s: string, chars: string): string {
  const re = new RegExp(`^[${escapeRegex(chars)}]+|[${escapeRegex(chars)}]+$`, 'g');
  return s.replace(re, '');
}
function trimStartChars(s: string, chars: string): string {
  const re = new RegExp(`^[${escapeRegex(chars)}]+`);
  return s.replace(re, '');
}
function trimEndChars(s: string, chars: string): string {
  const re = new RegExp(`[${escapeRegex(chars)}]+$`);
  return s.replace(re, '');
}

function sanitize(s: string): string {
  if (!s) return s;
  const charMap: Record<string, string> = {
    'ß': 'ss', 'Α': 'A', 'Β': 'B', 'Γ': 'G', 'Δ': 'D', 'Ε': 'E', 'Ζ': 'Z', 'Η': 'H', 'Θ': 'Th',
    'Ι': 'I', 'Κ': 'K', 'Λ': 'L', 'Μ': 'M', 'Ν': 'N', 'Ξ': 'X', 'Ο': 'O', 'Π': 'P',
    'Ρ': 'R', 'Σ': 'S', 'Τ': 'T', 'Υ': 'Y', 'Φ': 'Ph', 'Χ': 'Ch', 'Ψ': 'Ps', 'Ω': 'O',
    'α': 'a', 'β': 'b', 'γ': 'g', 'δ': 'd', 'ε': 'e', 'ζ': 'z', 'η': 'h', 'θ': 'th',
    'ι': 'i', 'κ': 'k', 'λ': 'l', 'μ': 'm', 'ν': 'n', 'ξ': 'x', 'ο': 'o', 'π': 'p',
    'ρ': 'r', 'σ': 's', 'τ': 't', 'υ': 'y', 'φ': 'ph', 'χ': 'ch', 'ψ': 'ps', 'ω': 'o',
  };
  const normalized = s.normalize('NFD');
  let result = '';
  for (const c of normalized) {
    if (charMap[c]) result += charMap[c];
    else if (c.charCodeAt(0) > 0x02ff) continue; // skip combining diacritical marks
    else result += c;
  }
  return result.normalize('NFC');
}

function concatMethod(target: unknown, args: unknown[]): string {
  const sep = args.length > 0 ? valueToString(args[0]) : '|';
  const items: string[] = toArray(target).map(valueToString);
  for (let i = 1; i < args.length; i++) {
    const a = args[i];
    if (isArray(a)) items.push(...a.map(valueToString));
    else items.push(valueToString(a));
  }
  return items.join(sep);
}

function convertTo(target: unknown, args: unknown[]): unknown {
  const typeName = (valueToString(args[0]) ?? '').toLowerCase();
  const s = valueToString(target);
  switch (typeName) {
    case 'int32': case 'int': case 'integer': { const n = parseFloat(s); return isNaN(n) ? 0 : Math.trunc(n); }
    case 'int64': case 'long': { const n = parseFloat(s); return isNaN(n) ? 0 : Math.trunc(n); }
    case 'double': case 'float': case 'decimal': { const n = parseFloat(s); return isNaN(n) ? 0 : n; }
    case 'boolean': case 'bool': {
      const n = parseFloat(s);
      if (!isNaN(n)) return n !== 0;
      if (s.toLowerCase() === 'true') return true;
      if (s.toLowerCase() === 'false') return false;
      return s.trim() !== '';
    }
    case 'string': return valueToString(target);
    default: return target;
  }
}

function evalRound(value: number, args: unknown[]): number {
  let decimals = 0;
  let mode = 'awayfromzero';
  for (const arg of args) {
    if (typeof arg === 'string') mode = arg.toLowerCase();
    else decimals = Number(arg);
  }
  const factor = Math.pow(10, decimals);
  if (mode === 'toeven') {
    // Banker's rounding
    const shifted = value * factor;
    const rounded = Math.round(shifted);
    if (Math.abs(shifted - Math.floor(shifted) - 0.5) < 1e-10) {
      return (Math.floor(shifted) % 2 === 0 ? Math.floor(shifted) : Math.ceil(shifted)) / factor;
    }
    return rounded / factor;
  }
  // Away from zero (default)
  return Math.round(value * factor + Number.EPSILON) / factor;
}

function evalDateAdd(target: unknown, args: unknown[]): string {
  const dateStr = valueToString(target);
  const date = new Date(dateStr);
  if (isNaN(date.getTime()) && typeof target === 'number') {
    return String((target as number) + Number(args[0]));
  }
  const tsStr = valueToString(args[0]);
  const ms = parseTimeSpan(tsStr);
  date.setTime(date.getTime() + ms);
  return date.toISOString().replace('.000Z', 'Z');
}

function parseTimeSpan(s: string): number {
  // Format: [days.]hours:minutes:seconds
  const parts = s.split(':');
  let days = 0, hours = 0, minutes = 0, seconds = 0;
  if (parts.length >= 3) {
    const hourPart = parts[0];
    if (hourPart.includes('.')) {
      const [d, h] = hourPart.split('.');
      days = parseInt(d); hours = parseInt(h);
    } else {
      hours = parseInt(hourPart);
    }
    minutes = parseInt(parts[1]);
    seconds = parseFloat(parts[2]);
  }
  return ((days * 24 + hours) * 3600 + minutes * 60 + seconds) * 1000;
}

function evalDateFormat(target: unknown, args: unknown[]): string {
  const dateStr = valueToString(target);
  let date: Date;
  if (args.length >= 2) {
    // Two args: inputFormat, outputFormat — just parse and format
    date = new Date(dateStr);
  } else {
    date = new Date(dateStr);
  }
  if (isNaN(date.getTime())) return dateStr;
  const fmt = args.length >= 2 ? valueToString(args[1]) : valueToString(args[0]);
  return formatDate(date, fmt);
}

function evalToUnixTime(target: unknown, args: unknown[]): string {
  let date: Date;
  if (args.length >= 1) date = new Date(valueToString(args[0]));
  else if (typeof target === 'string' && target) date = new Date(target);
  else date = new Date();
  return String(Math.floor(date.getTime() / 1000));
}

function evalNow(args: unknown[]): string {
  const fmt = args.length > 0 ? valueToString(args[0]) : 'yyyy-MM-ddTHH:mm:ssZ';
  const date = new Date();
  if (args.length >= 2) {
    // Timezone — not easily doable in pure JS without Intl, use UTC
    return formatDate(date, fmt);
  }
  return formatDate(date, fmt);
}

function formatDate(d: Date, fmt: string): string {
  const pad = (n: number, w = 2) => String(n).padStart(w, '0');
  const months = ['January','February','March','April','May','June','July','August','September','October','November','December'];
  return fmt
    .replace('yyyy', String(d.getUTCFullYear()))
    .replace('MMMM', months[d.getUTCMonth()])
    .replace('MMM', months[d.getUTCMonth()].slice(0, 3))
    .replace('MM', pad(d.getUTCMonth() + 1))
    .replace('dd', pad(d.getUTCDate()))
    .replace('HH', pad(d.getUTCHours()))
    .replace('mm', pad(d.getUTCMinutes()))
    .replace('ss', pad(d.getUTCSeconds()))
    .replace('Z', 'Z');
}

// ── Crypto (uses node:crypto — documented browser limitation) ──

let _crypto: typeof import('node:crypto') | null = null;
function getNodeCrypto(): typeof import('node:crypto') {
  if (!_crypto) {
    try {
      // Dynamic import for Node.js; will fail in browsers
      _crypto = require('node:crypto');
    } catch {
      throw new Error('Crypto functions (hash, rsaSign) require Node.js. Not available in browser.');
    }
  }
  return _crypto!;
}

function evalHash(input: string, args: unknown[]): string {
  const len = args.length > 0 ? Number(args[0]) : 32;
  const crypto = getNodeCrypto();
  const fullHash = crypto.createHash('md5').update(input, 'utf8').digest('hex');
  return fullHash.slice(0, Math.min(len, fullHash.length));
}

function evalRsaSign(target: unknown, args: unknown[]): string {
  const crypto = getNodeCrypto();
  const data = args.length > 0 ? valueToString(args[0]) : valueToString(target);
  const keyPem = args.length > 1 ? valueToString(args[1]) : valueToString(args[0]);

  const keyBody = keyPem
    .replace('-----BEGIN RSA PRIVATE KEY-----', '')
    .replace('-----END RSA PRIVATE KEY-----', '')
    .replace(/\n/g, '')
    .replace(/\r/g, '');

  const keyDer = Buffer.from(keyBody, 'base64');
  const privateKey = crypto.createPrivateKey({ key: keyDer, format: 'der', type: 'pkcs1' });
  const signature = crypto.sign('sha1', Buffer.from(data, 'ascii'), {
    key: privateKey,
    padding: crypto.constants.RSA_PKCS1_PADDING,
  });

  // Legacy compatibility: reverse the signature bytes
  const reversed = Buffer.from(signature).reverse();
  return reversed.toString('base64');
}

// ── Format I/O ──

function getOpt(args: unknown[], key: string, defaultVal: string): string {
  if (args.length > 0 && isObject(args[0])) {
    const v = (args[0] as any)[key];
    if (v !== undefined && v !== null) return String(v);
  }
  return defaultVal;
}

function getOptBool(args: unknown[], key: string, defaultVal: boolean): boolean {
  if (args.length > 0 && isObject(args[0])) {
    const v = (args[0] as any)[key];
    if (v !== undefined && v !== null) return isTruthy(v);
  }
  return defaultVal;
}

function getOptNumber(args: unknown[], key: string, defaultVal: number): number {
  if (args.length > 0 && isObject(args[0])) {
    const v = (args[0] as any)[key];
    if (v !== undefined && v !== null) return Number(v);
  }
  return defaultVal;
}

function getAlphabeticColumnName(index: number): string {
  let name = '';
  let i = index + 1;
  while (i > 0) { i--; name = String.fromCharCode(65 + i % 26) + name; i = Math.floor(i / 26); }
  return name;
}

function evalFromCsv(csv: string, args: unknown[]): unknown {
  const delimiter = getOpt(args, 'delimiter', ',');
  const hasHeaders = getOptBool(args, 'headers', true);
  const quote = getOpt(args, 'quote', '"').charAt(0);
  const skipRows = getOptNumber(args, 'skipRows', 0);
  const parseJsonOpt = getOptBool(args, 'parseJson', false);

  let lines = parseCsvLines(csv, delimiter.charAt(0), quote);

  if (skipRows > 0 && skipRows < lines.length)
    lines = lines.slice(skipRows);

  if (lines.length === 0) return [];

  function cellValue(val: string): unknown {
    if (parseJsonOpt && val) {
      const trimmed = val.trim();
      if ((trimmed.startsWith('{') && trimmed.endsWith('}')) ||
          (trimmed.startsWith('[') && trimmed.endsWith(']'))) {
        try { return JSON.parse(trimmed); } catch { /* keep as string */ }
      }
    }
    return val;
  }

  if (hasHeaders && lines.length >= 1) {
    const headers = lines[0];
    return lines.slice(1).map(row => {
      const obj: Record<string, unknown> = {};
      headers.forEach((h, j) => { obj[h] = cellValue(j < row.length ? row[j] : ''); });
      return obj;
    });
  }

  // No headers: return array of objects with auto-generated column names (A, B, C, ... Z, AA, AB, ...)
  const maxCols = Math.max(...lines.map(r => r.length));
  const colNames = Array.from({ length: maxCols }, (_, i) => getAlphabeticColumnName(i));
  return lines.map(row => {
    const obj: Record<string, unknown> = {};
    colNames.forEach((col, j) => { obj[col] = cellValue(j < row.length ? row[j] : ''); });
    return obj;
  });
}

function parseCsvLines(csv: string, delimiter: string, quote: string): string[][] {
  const lines: string[][] = [];
  let fields: string[] = [];
  let field = '';
  let inQuotes = false;
  let i = 0;

  while (i < csv.length) {
    const c = csv[i];
    if (inQuotes) {
      if (c === quote && i + 1 < csv.length && csv[i + 1] === quote) {
        field += quote; i += 2;
      } else if (c === quote) {
        inQuotes = false; i++;
      } else {
        field += c; i++;
      }
    } else if (c === quote) {
      inQuotes = true; i++;
    } else if (c === delimiter) {
      fields.push(field); field = ''; i++;
    } else if (c === '\n' || (c === '\r' && i + 1 < csv.length && csv[i + 1] === '\n')) {
      fields.push(field); field = '';
      if (fields.some(f => f.length > 0) || fields.length > 1) lines.push(fields);
      fields = [];
      i += c === '\r' ? 2 : 1;
    } else {
      field += c; i++;
    }
  }

  fields.push(field);
  if (fields.some(f => f.length > 0) || fields.length > 1) lines.push(fields);
  return lines;
}

function csvEscape(value: string, delimiter: string, quote: string, alwaysQuote = false): string {
  if (alwaysQuote || value.includes(delimiter) || value.includes(quote) || value.includes('\n') || value.includes('\r'))
    return `${quote}${value.replace(new RegExp(escapeRegex(quote), 'g'), quote + quote)}${quote}`;
  return value;
}

function evalToCsv(target: unknown, args: unknown[]): string {
  const delimiter = getOpt(args, 'delimiter', ',');
  const includeHeaders = getOptBool(args, 'headers', true);
  const quote = getOpt(args, 'quote', '"').charAt(0);
  const alwaysQuote = getOptBool(args, 'alwaysQuote', false);
  const items = toArray(target);
  if (items.length === 0) return '';

  const allKeys: string[] = [];
  for (const item of items) {
    if (isObject(item)) {
      for (const key of Object.keys(item)) {
        if (!allKeys.includes(key)) allKeys.push(key);
      }
    }
  }

  const rows: string[] = [];
  if (includeHeaders && allKeys.length > 0) {
    rows.push(allKeys.map(k => csvEscape(k, delimiter, quote, alwaysQuote)).join(delimiter));
  }

  for (const item of items) {
    if (isObject(item)) {
      rows.push(allKeys.map(k => csvEscape(valueToString((item as any)[k] ?? ''), delimiter, quote, alwaysQuote)).join(delimiter));
    } else if (isArray(item)) {
      rows.push(item.map(v => csvEscape(valueToString(v), delimiter, quote, alwaysQuote)).join(delimiter));
    }
  }

  return rows.join('\n');
}

// ── XML ──

interface XmlNode {
  type: 'element';
  name: string;
  attributes: Record<string, string>;
  children: (XmlNode | string)[];
}

function parseXml(xml: string): XmlNode | null {
  let pos = 0;

  function skipWhitespace() { while (pos < xml.length && /\s/.test(xml[pos])) pos++; }

  function parseText(): string {
    let text = '';
    while (pos < xml.length && xml[pos] !== '<') {
      if (xml[pos] === '&') {
        const semi = xml.indexOf(';', pos);
        if (semi > pos) {
          const entity = xml.substring(pos + 1, semi);
          const map: Record<string, string> = { lt: '<', gt: '>', amp: '&', quot: '"', apos: "'" };
          text += entity.startsWith('#x') ? String.fromCharCode(parseInt(entity.slice(2), 16))
                : entity.startsWith('#') ? String.fromCharCode(parseInt(entity.slice(1)))
                : map[entity] ?? `&${entity};`;
          pos = semi + 1;
        } else { text += xml[pos++]; }
      } else { text += xml[pos++]; }
    }
    return text;
  }

  function parseName(): string {
    const start = pos;
    while (pos < xml.length && /[a-zA-Z0-9_:\-.]/.test(xml[pos])) pos++;
    return xml.substring(start, pos);
  }

  function parseAttributes(): Record<string, string> {
    const attrs: Record<string, string> = {};
    while (pos < xml.length) {
      skipWhitespace();
      if (pos >= xml.length || xml[pos] === '>' || xml[pos] === '/' || xml[pos] === '?') break;
      const name = parseName();
      if (!name) break;
      skipWhitespace();
      if (xml[pos] !== '=') { attrs[name] = ''; continue; }
      pos++; // skip =
      skipWhitespace();
      const q = xml[pos];
      if (q !== '"' && q !== "'") { attrs[name] = ''; continue; }
      pos++; // skip opening quote
      let val = '';
      while (pos < xml.length && xml[pos] !== q) {
        if (xml[pos] === '&') {
          const semi = xml.indexOf(';', pos);
          if (semi > pos) {
            const entity = xml.substring(pos + 1, semi);
            const map: Record<string, string> = { lt: '<', gt: '>', amp: '&', quot: '"', apos: "'" };
            val += entity.startsWith('#x') ? String.fromCharCode(parseInt(entity.slice(2), 16))
                 : entity.startsWith('#') ? String.fromCharCode(parseInt(entity.slice(1)))
                 : map[entity] ?? `&${entity};`;
            pos = semi + 1;
          } else { val += xml[pos++]; }
        } else { val += xml[pos++]; }
      }
      pos++; // skip closing quote
      attrs[name] = val;
    }
    return attrs;
  }

  function parseElement(): XmlNode | string | null {
    skipWhitespace();
    if (pos >= xml.length) return null;

    if (xml[pos] !== '<') return parseText();

    // Skip comments, CDATA, processing instructions, DOCTYPE
    if (xml.startsWith('<!--', pos)) { pos = xml.indexOf('-->', pos); if (pos < 0) return null; pos += 3; return parseElement(); }
    if (xml.startsWith('<![CDATA[', pos)) { const end = xml.indexOf(']]>', pos); if (end < 0) return null; const text = xml.substring(pos + 9, end); pos = end + 3; return text; }
    if (xml.startsWith('<?', pos)) { pos = xml.indexOf('?>', pos); if (pos < 0) return null; pos += 2; return parseElement(); }
    if (xml.startsWith('<!', pos)) { pos = xml.indexOf('>', pos); if (pos < 0) return null; pos += 1; return parseElement(); }
    if (xml.startsWith('</', pos)) return null; // closing tag — handled by caller

    pos++; // skip <
    const name = parseName();
    const attributes = parseAttributes();
    skipWhitespace();

    if (xml[pos] === '/' && xml[pos + 1] === '>') {
      pos += 2; // self-closing
      return { type: 'element', name, attributes, children: [] };
    }

    if (xml[pos] === '>') {
      pos++; // skip >
      const children: (XmlNode | string)[] = [];
      while (pos < xml.length) {
        if (xml.startsWith('</', pos)) {
          pos += 2;
          parseName(); // skip closing tag name
          skipWhitespace();
          if (xml[pos] === '>') pos++;
          break;
        }
        const child = parseElement();
        if (child === null) break;
        if (typeof child === 'string') { if (child.trim()) children.push(child); }
        else children.push(child);
      }
      return { type: 'element', name, attributes, children };
    }

    return null;
  }

  const result = parseElement();
  return result && typeof result !== 'string' ? result : null;
}

function xmlNodeToValue(node: XmlNode, attrPrefix: string, stripNs: boolean): unknown {
  const localName = stripNs ? node.name.replace(/^.*:/, '') : node.name;
  const obj: Record<string, unknown> = {};

  // Attributes
  for (const [key, val] of Object.entries(node.attributes)) {
    if (stripNs && (key.startsWith('xmlns:') || key === 'xmlns')) continue;
    const attrName = stripNs ? key.replace(/^.*:/, '') : key;
    obj[attrPrefix + attrName] = val;
  }

  // Child elements
  const elementChildren = node.children.filter(c => typeof c !== 'string') as XmlNode[];
  if (elementChildren.length > 0) {
    const groups = new Map<string, XmlNode[]>();
    for (const child of elementChildren) {
      const childName = stripNs ? child.name.replace(/^.*:/, '') : child.name;
      const list = groups.get(childName) ?? [];
      list.push(child);
      groups.set(childName, list);
    }
    for (const [groupName, items] of groups) {
      if (items.length === 1) {
        obj[groupName] = xmlChildToValue(items[0], attrPrefix, stripNs);
      } else {
        obj[groupName] = items.map(i => xmlChildToValue(i, attrPrefix, stripNs));
      }
    }

    // Mixed content: text alongside elements
    const textParts = node.children.filter(c => typeof c === 'string').map(c => (c as string).trim()).filter(Boolean);
    if (textParts.length > 0) obj['#text'] = textParts.join(' ');
  } else {
    // Leaf element
    const text = node.children.filter(c => typeof c === 'string').join('');
    const hasAttrs = Object.keys(obj).length > 0;
    if (hasAttrs) {
      obj['#text'] = text;
    } else {
      // Simple text-only element → just the string, wrapped by caller
      return { [localName]: text };
    }
  }

  return { [localName]: obj };
}

function xmlChildToValue(node: XmlNode, attrPrefix: string, stripNs: boolean): unknown {
  const hasAttrs = Object.entries(node.attributes).some(([k]) => !(stripNs && (k.startsWith('xmlns:') || k === 'xmlns')));
  const hasChildren = node.children.some(c => typeof c !== 'string');

  if (!hasAttrs && !hasChildren) {
    return node.children.filter(c => typeof c === 'string').join('');
  }

  const obj: Record<string, unknown> = {};

  for (const [key, val] of Object.entries(node.attributes)) {
    if (stripNs && (key.startsWith('xmlns:') || key === 'xmlns')) continue;
    const attrName = stripNs ? key.replace(/^.*:/, '') : key;
    obj[attrPrefix + attrName] = val;
  }

  const elementChildren = node.children.filter(c => typeof c !== 'string') as XmlNode[];
  if (elementChildren.length > 0) {
    const groups = new Map<string, XmlNode[]>();
    for (const child of elementChildren) {
      const childName = stripNs ? child.name.replace(/^.*:/, '') : child.name;
      const list = groups.get(childName) ?? [];
      list.push(child);
      groups.set(childName, list);
    }
    for (const [groupName, items] of groups) {
      if (items.length === 1) obj[groupName] = xmlChildToValue(items[0], attrPrefix, stripNs);
      else obj[groupName] = items.map(i => xmlChildToValue(i, attrPrefix, stripNs));
    }

    const textParts = node.children.filter(c => typeof c === 'string').map(c => (c as string).trim()).filter(Boolean);
    if (textParts.length > 0) obj['#text'] = textParts.join(' ');
  } else {
    const text = node.children.filter(c => typeof c === 'string').join('');
    if (text) obj['#text'] = text;
  }

  return obj;
}

function evalFromXml(xml: string, args: unknown[]): unknown {
  const attrPrefix = getOpt(args, 'attributePrefix', '@');
  const stripNs = getOptBool(args, 'stripNamespaces', true);
  try {
    const root = parseXml(xml);
    if (!root) return null;
    return xmlNodeToValue(root, attrPrefix, stripNs);
  } catch { return null; }
}

function xmlEscape(s: string): string {
  return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}

function valueToXmlElement(name: string, value: unknown, attrPrefix: string, indent: string): string {
  if (isObject(value)) {
    const obj = value as Record<string, unknown>;
    const attrs: string[] = [];
    const children: string[] = [];
    let textContent = '';

    for (const [key, val] of Object.entries(obj)) {
      if (key.startsWith(attrPrefix)) {
        const attrName = key.slice(attrPrefix.length);
        if (attrName) attrs.push(` ${attrName}="${xmlEscape(valueToString(val))}"`);
      } else if (key === '#text') {
        textContent = xmlEscape(valueToString(val));
      } else if (isArray(val)) {
        for (const item of val as unknown[]) children.push(valueToXmlElement(key, item, attrPrefix, indent + '  '));
      } else if (isObject(val)) {
        children.push(valueToXmlElement(key, val, attrPrefix, indent + '  '));
      } else {
        children.push(`${indent}  <${key}>${xmlEscape(valueToString(val))}</${key}>`);
      }
    }

    const attrStr = attrs.join('');
    if (children.length === 0 && !textContent) return `${indent}<${name}${attrStr} />`;
    if (children.length === 0) return `${indent}<${name}${attrStr}>${textContent}</${name}>`;
    return `${indent}<${name}${attrStr}>\n${children.join('\n')}\n${indent}</${name}>`;
  }

  return `${indent}<${name}>${xmlEscape(valueToString(value))}</${name}>`;
}

function evalToXml(target: unknown, args: unknown[]): string {
  const attrPrefix = getOpt(args, 'attributePrefix', '@');
  const rootElement = getOpt(args, 'rootElement', '');
  const declaration = getOptBool(args, 'declaration', true);

  if (!isObject(target)) return '';
  const obj = target as Record<string, unknown>;
  const keys = Object.keys(obj);

  let xmlBody: string;
  if (rootElement) {
    xmlBody = valueToXmlElement(rootElement, target, attrPrefix, '');
  } else if (keys.length === 1 && !keys[0].startsWith(attrPrefix)) {
    xmlBody = valueToXmlElement(keys[0], obj[keys[0]], attrPrefix, '');
  } else {
    xmlBody = valueToXmlElement('root', target, attrPrefix, '');
  }

  return declaration ? `<?xml version="1.0" encoding="utf-8"?>\n${xmlBody}` : xmlBody;
}

function evalFromText(text: string, args: unknown[]): string[] {
  const delimiter = getOpt(args, 'delimiter', '\n');
  return text.split(delimiter).map(l => l.replace(/\r$/, ''));
}

function evalToText(target: unknown, args: unknown[]): string {
  const delimiter = getOpt(args, 'delimiter', '\n');
  if (isArray(target)) return target.map(valueToString).join(delimiter);
  return valueToString(target);
}
