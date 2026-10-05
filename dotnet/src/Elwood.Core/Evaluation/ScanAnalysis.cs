using Elwood.Core.Syntax;

namespace Elwood.Core.Evaluation;

/// <summary>
/// Decides, for a pipe operation that has run many times, whether each run was given the same
/// collection or a different one. <c>orders | where o => (o.lines | any …)</c> scans each
/// order's own lines: many runs, but linear. <c>orders | where o => (allLines | any …)</c>
/// scans the same list for every order. Only the second is worth a warning, and only the
/// script can tell them apart, so this reads the script. It runs when a warning is about to
/// be reported, never during evaluation.
/// </summary>
internal static class ScanAnalysis
{
    /// <summary>
    /// True when the input of <paramref name="site"/> is computed from the row of the nearest
    /// enclosing lambda (or implicit <c>$</c> context), and so can differ on every run.
    /// False when it cannot, or when the site is not found under <paramref name="root"/>.
    /// </summary>
    public static bool InputVariesPerRow(ElwoodNode root, PipeOperation site)
    {
        var search = new Search(site);
        switch (root)
        {
            case ScriptNode script:
                foreach (var binding in script.Bindings)
                    if (search.Visit(binding.Value)) return search.Varies;
                return script.ReturnExpression is not null && search.Visit(script.ReturnExpression) && search.Varies;
            case ElwoodExpression expression:
                return search.Visit(expression) && search.Varies;
            default:
                return false;
        }
    }

    // What an expression is evaluated against, as far as one enclosing row is concerned.
    private readonly record struct Scope(HashSet<string> RowNames, bool DollarIsRow, bool CurrentIsRow)
    {
        public Scope With(HashSet<string> names) => this with { RowNames = names };
    }

    private abstract record Frame;
    /// <summary>A lambda: its parameters are the row.</summary>
    private sealed record LambdaFrame(IReadOnlyList<string> Parameters) : Frame;
    /// <summary>A pipe argument written without a lambda: <c>$</c> is the row.</summary>
    private sealed record ImplicitFrame : Frame;
    /// <summary>The first <paramref name="Count"/> bindings of a let block are in scope.</summary>
    private sealed record LetFrame(IReadOnlyList<LetBindingNode> Bindings, int Count) : Frame;
    /// <summary>Evaluated against something other than the row (a match arm, a take count).</summary>
    private sealed record DetachedFrame : Frame;

    private sealed class Search(PipeOperation site)
    {
        private readonly List<Frame> _frames = [];
        public bool Varies { get; private set; }

        /// <summary>Returns true once the site has been found; <see cref="Varies"/> is then set.</summary>
        public bool Visit(ElwoodExpression expr)
        {
            switch (expr)
            {
                case PipelineExpression pipe:
                    for (var i = 0; i < pipe.Operations.Count; i++)
                    {
                        if (!ReferenceEquals(pipe.Operations[i], site)) continue;
                        Varies = InputVaries(pipe, i);
                        return true;
                    }
                    if (Visit(pipe.Source)) return true;
                    foreach (var op in pipe.Operations)
                        foreach (var (child, role) in Children(op))
                            if (VisitWith(child, role)) return true;
                    return false;

                case LambdaExpression lambda:
                    return Within(new LambdaFrame(lambda.Parameters), lambda.Body);

                case MemoExpression memo:
                    return Visit(memo.Lambda);

                case LetInExpression letIn:
                    for (var i = 0; i < letIn.Bindings.Count; i++)
                        if (Within(new LetFrame(letIn.Bindings, i), letIn.Bindings[i].Value)) return true;
                    return Within(new LetFrame(letIn.Bindings, letIn.Bindings.Count), letIn.Body);

                default:
                    foreach (var (child, role) in Children(expr))
                        if (VisitWith(child, role)) return true;
                    return false;
            }
        }

        private bool VisitWith(ElwoodExpression child, Role role) => role switch
        {
            Role.Row when child is not LambdaExpression => Within(new ImplicitFrame(), child),
            Role.Detached => Within(new DetachedFrame(), child),
            _ => Visit(child)
        };

        private bool Within(Frame frame, ElwoodExpression expr)
        {
            _frames.Add(frame);
            var found = Visit(expr);
            _frames.RemoveAt(_frames.Count - 1);
            return found;
        }

        private bool InputVaries(PipelineExpression pipe, int siteIndex)
        {
            // The nearest enclosing lambda or implicit context supplies the row.
            var rowAt = _frames.FindLastIndex(f => f is LambdaFrame or ImplicitFrame);
            if (rowAt < 0) return false;

            var names = new HashSet<string>();
            var dollarIsRow = true;
            if (_frames[rowAt] is LambdaFrame lambda && lambda.Parameters.Count > 0)
            {
                names.UnionWith(lambda.Parameters);
                dollarIsRow = false;
            }

            var scope = new Scope(names, dollarIsRow, CurrentIsRow: true);

            // Names bound between the row and the site carry the row with them when their
            // value was computed from it.
            for (var i = rowAt + 1; i < _frames.Count; i++)
            {
                switch (_frames[i])
                {
                    case LetFrame let:
                        scope = Bind(let.Bindings, let.Count, scope);
                        break;
                    case DetachedFrame:
                        scope = scope with { CurrentIsRow = false };
                        break;
                }
            }

            if (Depends(pipe.Source, scope)) return true;
            for (var i = 0; i < siteIndex; i++)
                if (Depends(pipe.Operations[i], scope)) return true;
            return false;
        }
    }

    private static Scope Bind(IReadOnlyList<LetBindingNode> bindings, int count, Scope scope)
    {
        var names = new HashSet<string>(scope.RowNames);
        for (var i = 0; i < count; i++)
        {
            // A binding shadows an outer name whether or not it depends on the row.
            if (Depends(bindings[i].Value, scope.With(names))) names.Add(bindings[i].Name);
            else names.Remove(bindings[i].Name);
        }
        return scope.With(names);
    }

    private static bool Depends(ElwoodExpression expr, Scope scope)
    {
        switch (expr)
        {
            case IdentifierExpression id:
                return scope.RowNames.Contains(id.Name);

            case PathExpression path:
                return path.IsRooted ? scope.DollarIsRow : scope.CurrentIsRow;

            case FunctionCallExpression call:
                return scope.RowNames.Contains(call.FunctionName) || call.Arguments.Any(a => Depends(a, scope));

            case LambdaExpression lambda:
            {
                // The lambda's own parameters hide outer names, and its item hides the row.
                var names = new HashSet<string>(scope.RowNames);
                names.ExceptWith(lambda.Parameters);
                return Depends(lambda.Body, new Scope(names,
                    scope.DollarIsRow && lambda.Parameters.Count > 0, CurrentIsRow: false));
            }

            case MemoExpression memo:
                return Depends(memo.Lambda, scope);

            case LetInExpression letIn:
                return Depends(letIn.Body, Bind(letIn.Bindings, letIn.Bindings.Count, scope))
                    || DependsInBindings(letIn, scope);

            case PipelineExpression pipe:
                return Depends(pipe.Source, scope) || pipe.Operations.Any(op => Depends(op, scope));

            default:
                foreach (var (child, role) in Children(expr))
                    if (DependsWith(child, role, scope)) return true;
                return false;
        }
    }

    private static bool DependsInBindings(LetInExpression letIn, Scope scope)
    {
        for (var i = 0; i < letIn.Bindings.Count; i++)
            if (Depends(letIn.Bindings[i].Value, Bind(letIn.Bindings, i, scope))) return true;
        return false;
    }

    private static bool Depends(PipeOperation op, Scope scope)
    {
        foreach (var (child, role) in Children(op))
            if (DependsWith(child, role, scope)) return true;
        return false;
    }

    private static bool DependsWith(ElwoodExpression child, Role role, Scope scope) => role switch
    {
        // An implicit pipe argument has its own $ and item; only names reach it from outside.
        Role.Row when child is not LambdaExpression =>
            Depends(child, scope with { DollarIsRow = false, CurrentIsRow = false }),
        Role.Detached => Depends(child, scope with { CurrentIsRow = false }),
        _ => Depends(child, scope)
    };

    private enum Role
    {
        /// <summary>Evaluated where its parent is.</summary>
        Same,
        /// <summary>Evaluated once per item of a pipeline: a lambda, or an implicit <c>$</c> context.</summary>
        Row,
        /// <summary>Evaluated against a value other than the enclosing row.</summary>
        Detached
    }

    private static IEnumerable<(ElwoodExpression Child, Role Role)> Children(ElwoodExpression expr)
    {
        switch (expr)
        {
            case BinaryExpression b:
                yield return (b.Left, Role.Same);
                yield return (b.Right, Role.Same);
                break;
            case UnaryExpression u:
                yield return (u.Operand, Role.Same);
                break;
            case IfExpression i:
                yield return (i.Condition, Role.Same);
                yield return (i.ThenBranch, Role.Same);
                yield return (i.ElseBranch, Role.Same);
                break;
            case ObjectExpression o:
                foreach (var p in o.Properties)
                {
                    if (p.ComputedKey is not null) yield return (p.ComputedKey, Role.Same);
                    yield return (p.Value, Role.Same);
                }
                break;
            case ArrayExpression a:
                foreach (var item in a.Items) yield return (item.Value, Role.Same);
                break;
            case InterpolatedStringExpression s:
                foreach (var part in s.Parts)
                    if (part is ExpressionPart e) yield return (e.Expression, Role.Same);
                break;
            case MatchExpression m:
                yield return (m.Input, Role.Same);
                foreach (var arm in Arms(m.Arms)) yield return arm;
                break;
            case MemberAccessExpression m:
                yield return (m.Target, Role.Same);
                break;
            case MethodCallExpression m:
                yield return (m.Target, Role.Same);
                foreach (var a in m.Arguments) yield return (a, Role.Same);
                break;
            case FunctionCallExpression f:
                foreach (var a in f.Arguments) yield return (a, Role.Same);
                break;
            case IndexExpression x:
                yield return (x.Target, Role.Same);
                if (x.Index is not null) yield return (x.Index, Role.Same);
                break;
        }
    }

    private static IEnumerable<(ElwoodExpression Child, Role Role)> Children(PipeOperation op)
    {
        switch (op)
        {
            case WhereOperation w: yield return (w.Predicate, Role.Row); break;
            case SelectOperation s: yield return (s.Projection, Role.Row); break;
            case SelectManyOperation s: yield return (s.Projection, Role.Row); break;
            case GroupByOperation g: yield return (g.KeySelector, Role.Row); break;
            case IndexByOperation x: yield return (x.KeySelector, Role.Row); break;
            case TakeWhileOperation t: yield return (t.Predicate, Role.Row); break;
            case ReduceOperation r:
                yield return (r.Accumulator, Role.Row);
                if (r.InitialValue is not null) yield return (r.InitialValue, Role.Detached);
                break;
            case OrderByOperation o:
                foreach (var (key, _) in o.Keys) yield return (key, Role.Row);
                break;
            case AggregateOperation a:
                if (a.Predicate is not null) yield return (a.Predicate, Role.Row);
                break;
            case QuantifierOperation q:
                if (q.Predicate is not null) yield return (q.Predicate, Role.Row);
                break;
            case JoinOperation j:
                yield return (j.Source, Role.Detached);
                yield return (j.LeftKey, Role.Row);
                yield return (j.RightKey, Role.Row);
                break;
            case SliceOperation s: yield return (s.Count, Role.Detached); break;
            case BatchOperation b: yield return (b.Size, Role.Detached); break;
            case ConcatOperation c:
                if (c.Separator is not null) yield return (c.Separator, Role.Detached);
                break;
            case MatchOperation m:
                foreach (var arm in Arms(m.Arms)) yield return arm;
                break;
        }
    }

    private static IEnumerable<(ElwoodExpression Child, Role Role)> Arms(IReadOnlyList<MatchArm> arms)
    {
        foreach (var arm in arms)
        {
            if (arm.Pattern is not null) yield return (arm.Pattern, Role.Detached);
            yield return (arm.Result, Role.Detached);
        }
    }
}
