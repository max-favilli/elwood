/**
 * Decides, for a pipe operation that has run many times, whether each run was given the same
 * collection or a different one. `orders | where o => (o.lines | any …)` scans each order's
 * own lines: many runs, but linear. `orders | where o => (allLines | any …)` scans the same
 * list for every order. Only the second is worth a warning, and only the script can tell
 * them apart, so this reads the script. It runs when a warning is about to be reported,
 * never during evaluation. Mirrors the .NET ScanAnalysis.
 */

import type { ElwoodExpression, ScriptNode, PipeOperation, PipelineExpression, LetBindingNode, MatchArm } from './ast.js';

/** What an expression is evaluated against, as far as one enclosing row is concerned. */
interface RowScope { rowNames: Set<string>; dollarIsRow: boolean; currentIsRow: boolean }

type Frame =
  | { kind: 'lambda'; parameters: string[] }                 // a lambda: its parameters are the row
  | { kind: 'implicit' }                                     // a pipe argument without a lambda: $ is the row
  | { kind: 'let'; bindings: LetBindingNode[]; count: number } // the first `count` bindings are in scope
  | { kind: 'detached' };                                    // evaluated against something other than the row

/** same: where its parent is; row: once per pipeline item; detached: against another value. */
type Role = 'same' | 'row' | 'detached';
type Child = [ElwoodExpression, Role];

/**
 * True when the input of `site` is computed from the row of the nearest enclosing lambda (or
 * implicit $ context), and so can differ on every run. False when it cannot, or when the
 * site is not found under `root`.
 */
export function inputVariesPerRow(root: ScriptNode | ElwoodExpression, site: PipeOperation): boolean {
  const frames: Frame[] = [];
  let varies = false;

  // Returns true once the site has been found; `varies` is then set.
  function visit(expr: ElwoodExpression): boolean {
    switch (expr.type) {
      case 'Pipeline': {
        const at = expr.operations.indexOf(site);
        if (at >= 0) {
          varies = inputVaries(expr, at);
          return true;
        }
        if (visit(expr.source)) return true;
        for (const op of expr.operations)
          for (const [child, role] of opChildren(op))
            if (visitWith(child, role)) return true;
        return false;
      }
      case 'Lambda': return within({ kind: 'lambda', parameters: expr.parameters }, expr.body);
      case 'Memo': return visit(expr.lambda);
      case 'LetIn':
        for (let i = 0; i < expr.bindings.length; i++)
          if (within({ kind: 'let', bindings: expr.bindings, count: i }, expr.bindings[i].value)) return true;
        return within({ kind: 'let', bindings: expr.bindings, count: expr.bindings.length }, expr.body);
      default:
        for (const [child, role] of children(expr))
          if (visitWith(child, role)) return true;
        return false;
    }
  }

  function visitWith(child: ElwoodExpression, role: Role): boolean {
    if (role === 'row' && child.type !== 'Lambda') return within({ kind: 'implicit' }, child);
    if (role === 'detached') return within({ kind: 'detached' }, child);
    return visit(child);
  }

  function within(frame: Frame, expr: ElwoodExpression): boolean {
    frames.push(frame);
    const found = visit(expr);
    frames.pop();
    return found;
  }

  function inputVaries(pipe: PipelineExpression, siteIndex: number): boolean {
    // The nearest enclosing lambda or implicit context supplies the row.
    let rowAt = -1;
    for (let i = frames.length - 1; i >= 0; i--) {
      if (frames[i].kind === 'lambda' || frames[i].kind === 'implicit') { rowAt = i; break; }
    }
    if (rowAt < 0) return false;

    const row = frames[rowAt];
    const named = row.kind === 'lambda' && row.parameters.length > 0;
    let scope: RowScope = {
      rowNames: new Set(named ? (row as { parameters: string[] }).parameters : []),
      dollarIsRow: !named,
      currentIsRow: true,
    };

    // Names bound between the row and the site carry the row with them when their value
    // was computed from it.
    for (let i = rowAt + 1; i < frames.length; i++) {
      const frame = frames[i];
      if (frame.kind === 'let') scope = bind(frame.bindings, frame.count, scope);
      else if (frame.kind === 'detached') scope = { ...scope, currentIsRow: false };
    }

    if (depends(pipe.source, scope)) return true;
    for (let i = 0; i < siteIndex; i++)
      if (opDepends(pipe.operations[i], scope)) return true;
    return false;
  }

  if (root.type === 'Script') {
    for (const binding of root.bindings)
      if (visit(binding.value)) return varies;
    return root.returnExpression !== null && visit(root.returnExpression) && varies;
  }
  return visit(root) && varies;
}

function bind(bindings: LetBindingNode[], count: number, scope: RowScope): RowScope {
  const names = new Set(scope.rowNames);
  for (let i = 0; i < count; i++) {
    // A binding shadows an outer name whether or not it depends on the row.
    if (depends(bindings[i].value, { ...scope, rowNames: names })) names.add(bindings[i].name);
    else names.delete(bindings[i].name);
  }
  return { ...scope, rowNames: names };
}

function depends(expr: ElwoodExpression, scope: RowScope): boolean {
  switch (expr.type) {
    case 'Identifier': return scope.rowNames.has(expr.name);
    case 'Path': return expr.isRooted ? scope.dollarIsRow : scope.currentIsRow;
    case 'FunctionCall':
      return scope.rowNames.has(expr.functionName) || expr.arguments.some(a => depends(a, scope));
    case 'Lambda': {
      // The lambda's own parameters hide outer names, and its item hides the row.
      const names = new Set(scope.rowNames);
      for (const p of expr.parameters) names.delete(p);
      return depends(expr.body, {
        rowNames: names,
        dollarIsRow: scope.dollarIsRow && expr.parameters.length > 0,
        currentIsRow: false,
      });
    }
    case 'Memo': return depends(expr.lambda, scope);
    case 'LetIn': {
      if (depends(expr.body, bind(expr.bindings, expr.bindings.length, scope))) return true;
      for (let i = 0; i < expr.bindings.length; i++)
        if (depends(expr.bindings[i].value, bind(expr.bindings, i, scope))) return true;
      return false;
    }
    case 'Pipeline':
      return depends(expr.source, scope) || expr.operations.some(op => opDepends(op, scope));
    default:
      return children(expr).some(([child, role]) => dependsWith(child, role, scope));
  }
}

function opDepends(op: PipeOperation, scope: RowScope): boolean {
  return opChildren(op).some(([child, role]) => dependsWith(child, role, scope));
}

function dependsWith(child: ElwoodExpression, role: Role, scope: RowScope): boolean {
  // An implicit pipe argument has its own $ and item; only names reach it from outside.
  if (role === 'row' && child.type !== 'Lambda')
    return depends(child, { ...scope, dollarIsRow: false, currentIsRow: false });
  if (role === 'detached') return depends(child, { ...scope, currentIsRow: false });
  return depends(child, scope);
}

function children(expr: ElwoodExpression): Child[] {
  switch (expr.type) {
    case 'Binary': return [[expr.left, 'same'], [expr.right, 'same']];
    case 'Unary': return [[expr.operand, 'same']];
    case 'If': return [[expr.condition, 'same'], [expr.thenBranch, 'same'], [expr.elseBranch, 'same']];
    case 'Object': return expr.properties.flatMap(p =>
      p.computedKey ? [[p.computedKey, 'same'], [p.value, 'same']] as Child[] : [[p.value, 'same']] as Child[]);
    case 'Array': return expr.items.map(i => [i.value, 'same'] as Child);
    case 'InterpolatedString':
      return expr.parts.flatMap(p => p.type === 'Expression' ? [[p.expression, 'same']] as Child[] : []);
    case 'Match': return [[expr.input, 'same'], ...arms(expr.arms)];
    case 'MemberAccess': return [[expr.target, 'same']];
    case 'MethodCall': return [[expr.target, 'same'], ...expr.arguments.map(a => [a, 'same'] as Child)];
    case 'FunctionCall': return expr.arguments.map(a => [a, 'same'] as Child);
    case 'Index': return expr.index ? [[expr.target, 'same'], [expr.index, 'same']] : [[expr.target, 'same']];
    default: return [];
  }
}

function opChildren(op: PipeOperation): Child[] {
  switch (op.type) {
    case 'Where': return [[op.predicate, 'row']];
    case 'Select': return [[op.projection, 'row']];
    case 'SelectMany': return [[op.projection, 'row']];
    case 'GroupBy': return [[op.keySelector, 'row']];
    case 'IndexBy': return [[op.keySelector, 'row']];
    case 'TakeWhile': return [[op.predicate, 'row']];
    case 'Reduce':
      return op.initialValue ? [[op.accumulator, 'row'], [op.initialValue, 'detached']] : [[op.accumulator, 'row']];
    case 'OrderBy': return op.keys.map(k => [k.key, 'row'] as Child);
    case 'Aggregate': return op.predicate ? [[op.predicate, 'row']] : [];
    case 'Quantifier': return op.predicate ? [[op.predicate, 'row']] : [];
    case 'Join': return [[op.source, 'detached'], [op.leftKey, 'row'], [op.rightKey, 'row']];
    case 'Slice': return [[op.count, 'detached']];
    case 'Batch': return [[op.size, 'detached']];
    case 'Concat': return op.separator ? [[op.separator, 'detached']] : [];
    case 'MatchOp': return arms(op.arms);
    default: return [];
  }
}

function arms(matchArms: MatchArm[]): Child[] {
  return matchArms.flatMap(arm =>
    arm.pattern ? [[arm.pattern, 'detached'], [arm.result, 'detached']] as Child[] : [[arm.result, 'detached']] as Child[]);
}
