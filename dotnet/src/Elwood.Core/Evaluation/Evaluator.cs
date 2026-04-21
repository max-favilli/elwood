using System.Xml.Linq;
using Elwood.Core.Abstractions;
using Elwood.Core.Diagnostics;
using Elwood.Core.Syntax;

namespace Elwood.Core.Evaluation;

/// <summary>
/// Tree-walking interpreter for Elwood AST nodes.
/// </summary>
public sealed class Evaluator
{
    private readonly IElwoodValueFactory _factory;
    private readonly Extensions.ElwoodExtensionRegistry? _extensions;
    private readonly List<ElwoodDiagnostic> _diagnostics = [];

    // Pipe iteration context for enriched error messages. Only read when an error occurs.
    private string? _pipeOp;
    private int _pipeIndex;
    private int _pipeTotal;

    public IReadOnlyList<ElwoodDiagnostic> Diagnostics => _diagnostics;

    public Evaluator(IElwoodValueFactory factory, Extensions.ElwoodExtensionRegistry? extensions = null)
    {
        _factory = factory;
        _extensions = extensions;
    }

    public IElwoodValue EvaluateScript(ScriptNode script, IElwoodValue root,
        Dictionary<string, Abstractions.IElwoodValue>? bindings = null)
    {
        var env = new ElwoodEnvironment();
        env.Set("$", root);
        env.Set("$root", root);
        if (bindings is not null)
            foreach (var (key, value) in bindings)
                env.Set(key, value);

        foreach (var binding in script.Bindings)
        {
            var value = Evaluate(binding.Value, root, env);
            env.Set(binding.Name, value);
        }

        if (script.ReturnExpression is not null)
            return Evaluate(script.ReturnExpression, root, env);

        return _factory.CreateNull();
    }

    public IElwoodValue Evaluate(ElwoodExpression expr, IElwoodValue current, ElwoodEnvironment env)
    {
        return expr switch
        {
            LiteralExpression lit => EvaluateLiteral(lit),
            PathExpression path => EvaluatePath(path, current, env),
            IdentifierExpression id => EvaluateIdentifier(id, current, env),
            BinaryExpression bin => EvaluateBinary(bin, current, env),
            UnaryExpression un => EvaluateUnary(un, current, env),
            IfExpression iff => EvaluateIf(iff, current, env),
            ObjectExpression obj => EvaluateObject(obj, current, env),
            ArrayExpression arr => EvaluateArray(arr, current, env),
            PipelineExpression pipe => EvaluatePipeline(pipe, current, env),
            MemberAccessExpression member => EvaluateMemberAccess(member, current, env),
            MethodCallExpression method => EvaluateMethodCall(method, current, env),
            FunctionCallExpression func => EvaluateFunction(func, current, env),
            IndexExpression idx => EvaluateIndex(idx, current, env),
            InterpolatedStringExpression interp => EvaluateInterpolation(interp, current, env),
            MatchExpression match => EvaluateMatch(match, current, env),
            MemoExpression memo => EvaluateMemo(memo, current, env),
            LambdaExpression => throw new ElwoodEvaluationException("Lambda expressions cannot be evaluated directly — they must be used as arguments to pipe operations.", expr.Span),
            _ => throw new ElwoodEvaluationException($"Unknown expression type: {expr.GetType().Name}", expr.Span)
        };
    }

    private IElwoodValue EvaluateLiteral(LiteralExpression lit) => lit.Value switch
    {
        string s => _factory.CreateString(s),
        double d => _factory.CreateNumber(d),
        bool b => _factory.CreateBool(b),
        null => _factory.CreateNull(),
        _ => throw new ElwoodEvaluationException($"Unknown literal type: {lit.Value?.GetType()}", lit.Span)
    };

    private IElwoodValue EvaluatePath(PathExpression path, IElwoodValue current, ElwoodEnvironment env)
    {
        var value = path.IsRooted ? (env.Get("$") ?? current) : current;

        for (int i = 0; i < path.Segments.Count; i++)
        {
            var segment = path.Segments[i];
            value = segment switch
            {
                PropertySegment prop => ResolveProperty(value, prop, path.Segments, i),
                IndexSegment { Index: null } => new LazyArrayValue(value.EnumerateArray(), _factory),
                IndexSegment { Index: int idx } => value.EnumerateArray().ElementAtOrDefault(idx) ??
                    throw new ElwoodEvaluationException($"Index {idx} out of range.", segment.Span),
                SliceSegment slice => EvaluateSliceSegment(value, slice),
                RecursiveDescentSegment rd => RecursiveDescend(value, rd.Name),
                _ => throw new ElwoodEvaluationException($"Unknown path segment type", segment.Span)
            };
        }

        return value;
    }

    private IElwoodValue ResolveProperty(IElwoodValue value, PropertySegment prop, IReadOnlyList<PathSegment> segments, int segIndex)
    {
        if (value.Kind == ElwoodValueKind.Object)
        {
            return value.GetProperty(prop.Name) ??
                throw new ElwoodEvaluationException(
                    $"Property '{prop.Name}' not found on {value.Kind}.",
                    prop.Span,
                    SuggestProperty(prop.Name, value));
        }

        // Auto-map over arrays: $.items[*].name → select each item's .name
        if (value.Kind == ElwoodValueKind.Array)
        {
            var items = value.EnumerateArray().ToList();
            var mapped = items
                .Select(item => item.GetProperty(prop.Name))
                .ToList();
            var filtered = mapped.Where(p => p is not null).Cast<IElwoodValue>().ToList();

            // If every item in the array lacked this property, report a helpful error
            if (filtered.Count == 0 && items.Count > 0 && mapped.All(p => p is null))
            {
                var sample = items.FirstOrDefault(i => i.Kind == ElwoodValueKind.Object);
                throw new ElwoodEvaluationException(
                    $"Property '{prop.Name}' not found on any item in the Array.",
                    prop.Span,
                    sample is not null ? SuggestProperty(prop.Name, sample) : null);
            }

            return _factory.CreateArray(filtered);
        }

        // Accessing a property on Null — if optional chaining, return null instead of throwing
        if (prop.Optional)
            return _factory.CreateNull();

        // Produce enriched error with full path + context
        var fullPath = BuildPathString(segments, segIndex);
        var resolvedPath = segIndex > 0 ? BuildPathString(segments, segIndex - 1) : "$";
        var msg = $"Cannot access property '{prop.Name}' on Null. Expression: {fullPath} — {resolvedPath} is null.";
        if (_pipeOp is not null)
            msg += $" While processing item [{_pipeIndex}] of {_pipeTotal} in | {_pipeOp}.";
        var suggestion = $"Did you mean: if {resolvedPath} != null then {fullPath} else null";
        throw new ElwoodEvaluationException(msg, prop.Span, suggestion);
    }

    private IElwoodValue EvaluateSliceSegment(IElwoodValue value, SliceSegment slice)
    {
        var items = value.EnumerateArray().ToList();
        var start = slice.Start ?? 0;
        var end = slice.End ?? items.Count;

        // Handle negative indices
        if (start < 0) start = Math.Max(0, items.Count + start);
        if (end < 0) end = Math.Max(0, items.Count + end);

        start = Math.Min(start, items.Count);
        end = Math.Min(end, items.Count);

        return _factory.CreateArray(items.Skip(start).Take(end - start));
    }

    private IElwoodValue RecursiveDescend(IElwoodValue value, string name)
    {
        var results = new List<IElwoodValue>();
        CollectRecursive(value, name, results);
        return _factory.CreateArray(results);
    }

    private void CollectRecursive(IElwoodValue value, string name, List<IElwoodValue> results)
    {
        if (value.Kind == ElwoodValueKind.Object)
        {
            var prop = value.GetProperty(name);
            if (prop is not null) results.Add(prop);
            foreach (var pname in value.GetPropertyNames())
            {
                var child = value.GetProperty(pname);
                if (child is not null) CollectRecursive(child, name, results);
            }
        }
        else if (value.Kind == ElwoodValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
                CollectRecursive(item, name, results);
        }
    }

    private IElwoodValue EvaluateIdentifier(IdentifierExpression id, IElwoodValue current, ElwoodEnvironment env)
    {
        return env.Get(id.Name) ??
            throw new ElwoodEvaluationException($"Undefined variable '{id.Name}'.", id.Span);
    }

    private IElwoodValue EvaluateBinary(BinaryExpression bin, IElwoodValue current, ElwoodEnvironment env)
    {
        var left = Evaluate(bin.Left, current, env);
        var right = Evaluate(bin.Right, current, env);

        return bin.Operator switch
        {
            BinaryOperator.Add => ArithmeticOp(left, right, (a, b) => a + b),
            BinaryOperator.Subtract => ArithmeticOp(left, right, (a, b) => a - b),
            BinaryOperator.Multiply => ArithmeticOp(left, right, (a, b) => a * b),
            BinaryOperator.Divide => ArithmeticOp(left, right, (a, b) => b != 0 ? a / b :
                throw new ElwoodEvaluationException("Division by zero.", bin.Span)),
            BinaryOperator.Equal => _factory.CreateBool(ValuesEqual(left, right)),
            BinaryOperator.NotEqual => _factory.CreateBool(!ValuesEqual(left, right)),
            BinaryOperator.LessThan => CompareOp(left, right, (a, b) => a < b),
            BinaryOperator.LessThanOrEqual => CompareOp(left, right, (a, b) => a <= b),
            BinaryOperator.GreaterThan => CompareOp(left, right, (a, b) => a > b),
            BinaryOperator.GreaterThanOrEqual => CompareOp(left, right, (a, b) => a >= b),
            BinaryOperator.And => _factory.CreateBool(IsTruthy(left) && IsTruthy(right)),
            BinaryOperator.Or => _factory.CreateBool(IsTruthy(left) || IsTruthy(right)),
            _ => throw new ElwoodEvaluationException($"Unknown binary operator: {bin.Operator}", bin.Span)
        };
    }

    private IElwoodValue EvaluateUnary(UnaryExpression un, IElwoodValue current, ElwoodEnvironment env)
    {
        var operand = Evaluate(un.Operand, current, env);
        return un.Operator switch
        {
            UnaryOperator.Not => _factory.CreateBool(!IsTruthy(operand)),
            UnaryOperator.Negate => _factory.CreateNumber(-operand.GetNumberValue()),
            _ => throw new ElwoodEvaluationException($"Unknown unary operator: {un.Operator}", un.Span)
        };
    }

    private IElwoodValue EvaluateIf(IfExpression iff, IElwoodValue current, ElwoodEnvironment env)
    {
        var condition = Evaluate(iff.Condition, current, env);
        return IsTruthy(condition)
            ? Evaluate(iff.ThenBranch, current, env)
            : Evaluate(iff.ElseBranch, current, env);
    }

    private IElwoodValue EvaluateObject(ObjectExpression obj, IElwoodValue current, ElwoodEnvironment env)
    {
        var properties = new List<KeyValuePair<string, IElwoodValue>>();
        foreach (var p in obj.Properties)
        {
            if (p.IsSpread)
            {
                // Spread: merge all properties from the evaluated object
                var spreadValue = Evaluate(p.Value, current, env);
                if (spreadValue.Kind == ElwoodValueKind.Object)
                {
                    foreach (var name in spreadValue.GetPropertyNames())
                    {
                        var prop = spreadValue.GetProperty(name);
                        if (prop is not null)
                            properties.Add(new KeyValuePair<string, IElwoodValue>(name, prop));
                    }
                }
            }
            else if (p.ComputedKey is not null)
            {
                // Computed key: [expr]: value
                var keyValue = Evaluate(p.ComputedKey, current, env);
                var keyStr = ValueToString(keyValue);
                properties.Add(new KeyValuePair<string, IElwoodValue>(keyStr, Evaluate(p.Value, current, env)));
            }
            else
            {
                properties.Add(new KeyValuePair<string, IElwoodValue>(p.Key, Evaluate(p.Value, current, env)));
            }
        }
        return _factory.CreateObject(properties);
    }

    private IElwoodValue EvaluateArray(ArrayExpression arr, IElwoodValue current, ElwoodEnvironment env)
    {
        var items = arr.Items.Select(i => Evaluate(i, current, env));
        return _factory.CreateArray(items);
    }

    private IElwoodValue EvaluatePipeline(PipelineExpression pipe, IElwoodValue current, ElwoodEnvironment env)
    {
        var value = Evaluate(pipe.Source, current, env);

        foreach (var op in pipe.Operations)
        {
            value = EvaluatePipeOperation(op, value, current, env);
        }

        // Materialize lazy arrays at pipeline boundaries so callers get concrete JSON
        if (value is LazyArrayValue lazy)
            return lazy.ToConcreteValue();

        return value;
    }

    private IElwoodValue EvaluatePipeOperation(PipeOperation op, IElwoodValue input, IElwoodValue root, ElwoodEnvironment env)
    {
        return op switch
        {
            WhereOperation where => EvaluateWhere(where, input, env),
            SelectOperation select => EvaluateSelect(select, input, env),
            SelectManyOperation selectMany => EvaluateSelectMany(selectMany, input, env),
            DistinctOperation => EvaluateDistinct(input),
            AggregateOperation agg => EvaluateAggregate(agg, input, env),
            SliceOperation slice => EvaluateSlice(slice, input, env),
            TakeWhileOperation tw => EvaluateTakeWhile(tw, input, env),
            OrderByOperation order => EvaluateOrderBy(order, input, env),
            GroupByOperation group => EvaluateGroupBy(group, input, env),
            BatchOperation batch => EvaluateBatch(batch, input, env),
            MatchOperation match => EvaluateMatchOp(match, input, env),
            ConcatOperation concat => EvaluateConcat(concat, input, env),
            ReduceOperation reduce => EvaluateReduce(reduce, input, env),
            JoinOperation join => EvaluateJoin(join, input, env),
            QuantifierOperation q => EvaluateQuantifier(q, input, env),
            _ => throw new ElwoodEvaluationException($"Unsupported pipe operation: {op.GetType().Name}", op.Span)
        };
    }

    // ── Streaming (lazy) operators — return LazyArrayValue, no materialization ──

    private IElwoodValue EvaluateWhere(WhereOperation where, IElwoodValue input, ElwoodEnvironment env)
    {
        // Only get total for concrete arrays — avoid materializing lazy arrays just for error context
        var total = input is not LazyArrayValue && input.Kind == ElwoodValueKind.Array ? input.GetArrayLength() : -1;
        var index = 0;
        var items = input.EnumerateArray()
            .Where(item =>
            {
                _pipeOp = "where"; _pipeIndex = index; _pipeTotal = total;
                var result = EvaluateWithLambdaOrImplicit(where.Predicate, item, env);
                index++;
                _pipeOp = null;
                return IsTruthy(result);
            });
        return new LazyArrayValue(items, _factory);
    }

    private IElwoodValue EvaluateSelect(SelectOperation select, IElwoodValue input, ElwoodEnvironment env)
    {
        var total = input is not LazyArrayValue && input.Kind == ElwoodValueKind.Array ? input.GetArrayLength() : -1;
        var index = 0;
        var items = input.EnumerateArray()
            .Select(item =>
            {
                _pipeOp = "select"; _pipeIndex = index; _pipeTotal = total;
                var result = EvaluateWithLambdaOrImplicit(select.Projection, item, env);
                index++;
                _pipeOp = null;
                return result;
            });
        return new LazyArrayValue(items, _factory);
    }

    private IElwoodValue EvaluateSelectMany(SelectManyOperation selectMany, IElwoodValue input, ElwoodEnvironment env)
    {
        var items = input.EnumerateArray()
            .SelectMany(item =>
            {
                var result = EvaluateWithLambdaOrImplicit(selectMany.Projection, item, env);
                return result.Kind == ElwoodValueKind.Array ? result.EnumerateArray() : [result];
            });
        return new LazyArrayValue(items, _factory);
    }

    private IElwoodValue EvaluateDistinct(IElwoodValue input)
    {
        var seen = new HashSet<string>();
        var items = input.EnumerateArray().Where(item =>
        {
            var key = Serialize(item);
            return seen.Add(key);
        });
        return new LazyArrayValue(items, _factory);
    }

    // ── Materializing operators — need all data ──

    private IElwoodValue EvaluateAggregate(AggregateOperation agg, IElwoodValue input, ElwoodEnvironment env)
    {
        // first/first(pred) can short-circuit — no need to materialize
        if (agg.Name == "first")
        {
            var source = input.EnumerateArray();
            if (agg.Predicate is not null)
                source = source.Where(item => IsTruthy(EvaluateWithLambdaOrImplicit(agg.Predicate, item, env)));
            return source.FirstOrDefault() ?? _factory.CreateNull();
        }

        // Everything else needs materialization
        var items = input.EnumerateArray().ToList();

        // last with optional predicate
        if (agg.Name == "last" && agg.Predicate is not null)
        {
            var filtered = items.Where(item => IsTruthy(EvaluateWithLambdaOrImplicit(agg.Predicate, item, env)));
            return filtered.LastOrDefault() ?? _factory.CreateNull();
        }

        return agg.Name switch
        {
            "count" => _factory.CreateNumber(items.Count),
            "last" => items.LastOrDefault() ?? _factory.CreateNull(),
            "sum" => _factory.CreateNumber(items.Sum(i => i.GetNumberValue())),
            "min" => _factory.CreateNumber(items.Min(i => i.GetNumberValue())),
            "max" => _factory.CreateNumber(items.Max(i => i.GetNumberValue())),
            "index" => new LazyArrayValue(Enumerable.Range(0, items.Count).Select(i => _factory.CreateNumber(i)), _factory),
            _ => throw new ElwoodEvaluationException($"Unknown aggregate: {agg.Name}", agg.Span)
        };
    }

    private IElwoodValue EvaluateSlice(SliceOperation slice, IElwoodValue input, ElwoodEnvironment env)
    {
        var n = (int)Evaluate(slice.Count, input, env).GetNumberValue();
        var items = slice.Kind == "take"
            ? input.EnumerateArray().Take(n)   // Take short-circuits — stops pulling after n
            : input.EnumerateArray().Skip(n);
        return new LazyArrayValue(items, _factory);
    }

    private IElwoodValue EvaluateTakeWhile(TakeWhileOperation tw, IElwoodValue input, ElwoodEnvironment env)
    {
        var items = input.EnumerateArray().TakeWhile(item =>
            IsTruthy(EvaluateWithLambdaOrImplicit(tw.Predicate, item, env)));
        return new LazyArrayValue(items, _factory);
    }

    private IElwoodValue EvaluateOrderBy(OrderByOperation order, IElwoodValue input, ElwoodEnvironment env)
    {
        var items = input.EnumerateArray().ToList();

        IOrderedEnumerable<IElwoodValue>? ordered = null;
        foreach (var (key, ascending) in order.Keys)
        {
            Func<IElwoodValue, object> selector = item =>
            {
                var k = EvaluateWithLambdaOrImplicit(key, item, env);
                return k.Kind switch
                {
                    ElwoodValueKind.Number => (object)k.GetNumberValue(),
                    ElwoodValueKind.String => k.GetStringValue() ?? "",
                    ElwoodValueKind.Boolean => k.GetBooleanValue(),
                    _ => Serialize(k)
                };
            };

            if (ordered is null)
                ordered = ascending ? items.OrderBy(selector) : items.OrderByDescending(selector);
            else
                ordered = ascending ? ordered.ThenBy(selector) : ordered.ThenByDescending(selector);
        }

        return _factory.CreateArray(ordered ?? items.OrderBy(_ => 0));
    }

    private IElwoodValue EvaluateGroupBy(GroupByOperation group, IElwoodValue input, ElwoodEnvironment env)
    {
        var items = input.EnumerateArray().ToList();
        var groups = items.GroupBy(item =>
        {
            var key = EvaluateWithLambdaOrImplicit(group.KeySelector, item, env);
            return Serialize(key);
        });

        var result = groups.Select(g =>
        {
            // Each group is an object with .key and .items
            var firstItem = g.First();
            var keyValue = EvaluateWithLambdaOrImplicit(group.KeySelector, firstItem, env);
            return _factory.CreateObject(new[]
            {
                new KeyValuePair<string, IElwoodValue>("key", keyValue),
                new KeyValuePair<string, IElwoodValue>("items", _factory.CreateArray(g))
            });
        });

        return _factory.CreateArray(result);
    }

    private IElwoodValue EvaluateBatch(BatchOperation batch, IElwoodValue input, ElwoodEnvironment env)
    {
        var size = (int)Evaluate(batch.Size, input, env).GetNumberValue();
        var items = input.EnumerateArray().ToList();
        var batches = new List<IElwoodValue>();

        for (var i = 0; i < items.Count; i += size)
        {
            batches.Add(_factory.CreateArray(items.Skip(i).Take(size)));
        }

        return _factory.CreateArray(batches);
    }

    private IElwoodValue EvaluateMatchOp(MatchOperation match, IElwoodValue input, ElwoodEnvironment env)
    {
        return EvaluateMatchArms(match.Arms, input, env);
    }

    private IElwoodValue EvaluateConcat(ConcatOperation concat, IElwoodValue input, ElwoodEnvironment env)
    {
        var separator = concat.Separator is not null
            ? ValueToString(Evaluate(concat.Separator, input, env))
            : "|";
        var items = input.EnumerateArray().Select(ValueToString);
        return _factory.CreateString(string.Join(separator, items));
    }

    private IElwoodValue EvaluateJoin(JoinOperation join, IElwoodValue input, ElwoodEnvironment env)
    {
        var leftItems = input.EnumerateArray().ToList();
        var root = env.Get("$") ?? input;
        var rightSource = Evaluate(join.Source, root, env);
        var rightItems = rightSource.EnumerateArray().ToList();

        // Build lookups by key for O(n+m) performance
        var rightLookup = new Dictionary<string, List<IElwoodValue>>();
        foreach (var rightItem in rightItems)
        {
            var rightKey = EvaluateWithLambdaOrImplicit(join.RightKey, rightItem, env);
            var keyStr = Serialize(rightKey);
            if (!rightLookup.TryGetValue(keyStr, out var list))
            {
                list = [];
                rightLookup[keyStr] = list;
            }
            list.Add(rightItem);
        }

        var matchedRightKeys = new HashSet<string>();
        var results = new List<IElwoodValue>();
        var nullValue = _factory.CreateNull();

        // Process left items
        foreach (var leftItem in leftItems)
        {
            var leftKey = EvaluateWithLambdaOrImplicit(join.LeftKey, leftItem, env);
            var keyStr = Serialize(leftKey);

            if (rightLookup.TryGetValue(keyStr, out var matches))
            {
                matchedRightKeys.Add(keyStr);
                foreach (var rightItem in matches)
                    results.Add(MergeJoinResult(leftItem, rightItem, join.IntoAlias));
            }
            else if (join.Mode is JoinMode.Left or JoinMode.Full)
            {
                // Left has no match — include with null right
                results.Add(MergeJoinResult(leftItem, nullValue, join.IntoAlias));
            }
            // Inner: unmatched left items are dropped
        }

        // Process unmatched right items (for right/full joins)
        if (join.Mode is JoinMode.Right or JoinMode.Full)
        {
            foreach (var rightItem in rightItems)
            {
                var rightKey = EvaluateWithLambdaOrImplicit(join.RightKey, rightItem, env);
                var keyStr = Serialize(rightKey);
                if (!matchedRightKeys.Contains(keyStr))
                {
                    results.Add(MergeJoinResult(nullValue, rightItem, join.IntoAlias));
                }
            }
        }

        return _factory.CreateArray(results);
    }

    private IElwoodValue MergeJoinResult(IElwoodValue left, IElwoodValue right, string? intoAlias)
    {
        var props = new List<KeyValuePair<string, IElwoodValue>>();

        // Copy left properties (if not null)
        if (left.Kind == ElwoodValueKind.Object)
            foreach (var name in left.GetPropertyNames())
                props.Add(new KeyValuePair<string, IElwoodValue>(name, left.GetProperty(name)!));

        if (intoAlias is not null)
        {
            props.Add(new KeyValuePair<string, IElwoodValue>(intoAlias, right));
        }
        else if (right.Kind == ElwoodValueKind.Object)
        {
            foreach (var name in right.GetPropertyNames())
                if (left.GetProperty(name) is null)
                    props.Add(new KeyValuePair<string, IElwoodValue>(name, right.GetProperty(name)!));
        }

        return _factory.CreateObject(props);
    }

    private IElwoodValue EvaluateReduce(ReduceOperation reduce, IElwoodValue input, ElwoodEnvironment env)
    {
        var items = input.EnumerateArray().ToList();
        if (items.Count == 0)
            return reduce.InitialValue is not null ? Evaluate(reduce.InitialValue, input, env) : _factory.CreateNull();

        if (reduce.Accumulator is not LambdaExpression lambda || lambda.Parameters.Count < 2)
            throw new ElwoodEvaluationException("reduce requires a lambda with two parameters: (acc, item) => expr", reduce.Span);

        IElwoodValue acc;
        int startIndex;

        if (reduce.InitialValue is not null)
        {
            acc = Evaluate(reduce.InitialValue, input, env);
            startIndex = 0;
        }
        else
        {
            acc = items[0];
            startIndex = 1;
        }

        for (var i = startIndex; i < items.Count; i++)
        {
            var childEnv = env.CreateChild();
            childEnv.Set(lambda.Parameters[0], acc);
            childEnv.Set(lambda.Parameters[1], items[i]);
            acc = Evaluate(lambda.Body, items[i], childEnv);
        }

        return acc;
    }

    private IElwoodValue EvaluateQuantifier(QuantifierOperation q, IElwoodValue input, ElwoodEnvironment env)
    {
        var items = input.EnumerateArray();
        var result = q.Kind == "all"
            ? items.All(item => IsTruthy(EvaluateWithLambdaOrImplicit(q.Predicate, item, env)))
            : items.Any(item => IsTruthy(EvaluateWithLambdaOrImplicit(q.Predicate, item, env)));
        return _factory.CreateBool(result);
    }

    private IElwoodValue EvaluateMatch(MatchExpression match, IElwoodValue current, ElwoodEnvironment env)
    {
        var input = Evaluate(match.Input, current, env);
        return EvaluateMatchArms(match.Arms, input, env);
    }

    private IElwoodValue EvaluateMatchArms(IReadOnlyList<MatchArm> arms, IElwoodValue input, ElwoodEnvironment env)
    {
        foreach (var arm in arms)
        {
            if (arm.Pattern is null) // wildcard
                return Evaluate(arm.Result, input, env);

            var pattern = Evaluate(arm.Pattern, input, env);
            if (ValuesEqual(input, pattern))
                return Evaluate(arm.Result, input, env);
        }

        return _factory.CreateNull(); // No match
    }

    private IElwoodValue EvaluateMemberAccess(MemberAccessExpression member, IElwoodValue current, ElwoodEnvironment env)
    {
        var target = Evaluate(member.Target, current, env);
        // Optional chaining: if target is null and access is optional, short-circuit to null
        if (target.Kind == ElwoodValueKind.Null && member.Optional)
            return _factory.CreateNull();

        // Auto-map over arrays: variable.items[*].name → select each item's .name
        if (target.Kind == ElwoodValueKind.Array)
        {
            var items = target.EnumerateArray().ToList();
            var mapped = items
                .Select(item => item.GetProperty(member.MemberName))
                .ToList();
            var filtered = mapped.Where(p => p is not null).Cast<IElwoodValue>().ToList();
            if (filtered.Count > 0 || items.Count == 0)
                return _factory.CreateArray(filtered);
        }

        // For member access on null (e.g., from left/right/full join results), return null safely
        return target.GetProperty(member.MemberName) ?? _factory.CreateNull();
    }

    // Methods that operate on the array as a whole — skip auto-mapping.
    // All other methods auto-map over array elements (consistent with property access).
    private static readonly HashSet<string> ArrayNativeMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        // Collection / aggregation
        "count", "first", "last", "sum", "min", "max", "index", "take", "skip",
        "length", "in", "range", "concat",
        // Container checks ($.arr.isNullOrEmpty() checks if the array is empty)
        "isNull", "isEmpty", "isNullOrEmpty", "isNullOrWhiteSpace",
        // Format I/O that takes arrays as input
        "toCsv", "toText", "toXml", "toParquet", "toXlsx"
    };

    private IElwoodValue EvaluateMethodCall(MethodCallExpression method, IElwoodValue current, ElwoodEnvironment env)
    {
        var target = Evaluate(method.Target, current, env);
        var args = method.Arguments.Select(a => Evaluate(a, current, env)).ToList();

        if (target.Kind == ElwoodValueKind.Array && !ArrayNativeMethods.Contains(method.MethodName))
        {
            var mapped = target.EnumerateArray()
                .Select(item => EvaluateBuiltinMethod(method.MethodName, item, args, method.Span))
                .ToList();
            return _factory.CreateArray(mapped);
        }

        return EvaluateBuiltinMethod(method.MethodName, target, args, method.Span);
    }

    private IElwoodValue EvaluateMemo(MemoExpression memo, IElwoodValue current, ElwoodEnvironment env)
    {
        return new MemoizedFunctionValue(memo.Lambda, env, this, _factory);
    }

    private IElwoodValue EvaluateFunction(FunctionCallExpression func, IElwoodValue current, ElwoodEnvironment env)
    {
        // Check environment first — user-defined functions (including memo)
        var funcValue = env.Get(func.FunctionName);
        if (funcValue is MemoizedFunctionValue memoFunc)
        {
            var memoArgs = func.Arguments.Select(a => Evaluate(a, current, env)).ToList();
            return memoFunc.Invoke(memoArgs, current);
        }

        // iterate(seed, lambda) — needs raw lambda AST, not evaluated args
        if (func.FunctionName == "iterate" && func.Arguments.Count >= 2)
        {
            return EvaluateIterate(func, current, env);
        }

        var args = func.Arguments.Select(a => Evaluate(a, current, env)).ToList();
        return EvaluateBuiltinMethod(func.FunctionName, current, args, func.Span);
    }

    private IElwoodValue EvaluateIterate(FunctionCallExpression func, IElwoodValue current, ElwoodEnvironment env)
    {
        var seed = Evaluate(func.Arguments[0], current, env);
        var lambdaExpr = func.Arguments[1];

        if (lambdaExpr is not LambdaExpression lambda)
            throw new ElwoodEvaluationException("iterate requires a lambda as second argument: iterate(seed, x => expr)", func.Span);

        const int maxIterations = 1_000_000;

        IEnumerable<IElwoodValue> Generate()
        {
            var val = seed;
            for (var i = 0; i < maxIterations; i++)
            {
                yield return val;
                var childEnv = env.CreateChild();
                childEnv.Set(lambda.Parameters[0], val);
                val = Evaluate(lambda.Body, val, childEnv);
            }
            throw new ElwoodEvaluationException(
                $"iterate exceeded maximum iteration limit ({maxIterations}). Use take(), takeWhile(), or first() to limit the sequence.",
                func.Span);
        }

        return new LazyArrayValue(Generate(), _factory);
    }

    private IElwoodValue EvaluateIndex(IndexExpression idx, IElwoodValue current, ElwoodEnvironment env)
    {
        var target = Evaluate(idx.Target, current, env);

        if (idx.Index is null) // [*]
            return _factory.CreateArray(target.EnumerateArray());

        var index = Evaluate(idx.Index, current, env);

        // String index on object → property access (e.g., obj["@id"])
        if (index.Kind == ElwoodValueKind.String && target.Kind == ElwoodValueKind.Object)
            return target.GetProperty(index.GetStringValue()!) ?? _factory.CreateNull();

        var i = (int)index.GetNumberValue();
        return target.EnumerateArray().ElementAtOrDefault(i) ?? _factory.CreateNull();
    }

    private IElwoodValue EvaluateInterpolation(InterpolatedStringExpression interp, IElwoodValue current, ElwoodEnvironment env)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var part in interp.Parts)
        {
            switch (part)
            {
                case TextPart text:
                    sb.Append(text.Text);
                    break;
                case ExpressionPart expr:
                    var value = Evaluate(expr.Expression, current, env);
                    sb.Append(ValueToString(value));
                    break;
            }
        }
        return _factory.CreateString(sb.ToString());
    }

    // ── Lambda/Implicit handling ──

    private IElwoodValue EvaluateWithLambdaOrImplicit(ElwoodExpression expr, IElwoodValue item, ElwoodEnvironment env)
    {
        if (expr is LambdaExpression lambda)
        {
            var childEnv = env.CreateChild();
            if (lambda.Parameters.Count >= 1)
                childEnv.Set(lambda.Parameters[0], item);
            childEnv.Set("$", item);
            return Evaluate(lambda.Body, item, childEnv);
        }

        // Implicit $ context — evaluate with item as current
        var implicitEnv = env.CreateChild();
        implicitEnv.Set("$", item);
        return Evaluate(expr, item, implicitEnv);
    }

    // ── Built-in Methods (initial set) ──

    private IElwoodValue EvaluateBuiltinMethod(string name, IElwoodValue target, List<IElwoodValue> args, SourceSpan span)
    {
        return name switch
        {
            // String methods
            "toLower" => EvaluateToLowerUpper(target, args, false),
            "toUpper" => EvaluateToLowerUpper(target, args, true),
            "trim" => _factory.CreateString(
                args.Count > 0
                    ? (target.GetStringValue() ?? "").Trim((args[0].GetStringValue() ?? "").ToCharArray())
                    : (target.GetStringValue() ?? "").Trim()),
            "trimStart" => _factory.CreateString(
                args.Count > 0
                    ? (target.GetStringValue() ?? "").TrimStart((args[0].GetStringValue() ?? "").ToCharArray())
                    : (target.GetStringValue() ?? "").TrimStart()),
            "trimEnd" => _factory.CreateString(
                args.Count > 0
                    ? (target.GetStringValue() ?? "").TrimEnd((args[0].GetStringValue() ?? "").ToCharArray())
                    : (target.GetStringValue() ?? "").TrimEnd()),
            "left" => EvaluateLeft(target, args),
            "right" => EvaluateRight(target, args),
            "padLeft" => _factory.CreateString(
                (target.GetStringValue() ?? "").PadLeft(
                    (int)args[0].GetNumberValue(),
                    args.Count > 1 ? (args[1].GetStringValue() ?? " ")[0] : ' ')),
            "padRight" => _factory.CreateString(
                (target.GetStringValue() ?? "").PadRight(
                    (int)args[0].GetNumberValue(),
                    args.Count > 1 ? (args[1].GetStringValue() ?? " ")[0] : ' ')),
            "length" => target.Kind == ElwoodValueKind.Array
                ? _factory.CreateNumber(target.GetArrayLength())
                : _factory.CreateNumber(target.GetStringValue()?.Length ?? 0),
            "contains" => _factory.CreateBool(
                target.GetStringValue()?.Contains(args[0].GetStringValue() ?? "", StringComparison.OrdinalIgnoreCase) ?? false),
            "startsWith" => _factory.CreateBool(
                target.GetStringValue()?.StartsWith(args[0].GetStringValue() ?? "", StringComparison.OrdinalIgnoreCase) ?? false),
            "endsWith" => _factory.CreateBool(
                target.GetStringValue()?.EndsWith(args[0].GetStringValue() ?? "", StringComparison.OrdinalIgnoreCase) ?? false),
            "replace" => _factory.CreateString(
                target.GetStringValue()?.Replace(
                    args[0].GetStringValue() ?? "",
                    args.Count > 1 ? args[1].GetStringValue() ?? "" : "",
                    args.Count > 2 && IsTruthy(args[2]) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ?? ""),
            "substring" => EvaluateSubstring(target, args),
            "regex" => _factory.CreateArray(
                System.Text.RegularExpressions.Regex.Matches(target.GetStringValue() ?? "", args[0].GetStringValue() ?? "")
                    .Select(m => _factory.CreateString(m.Value))),
            "urlDecode" => _factory.CreateString(
                Uri.UnescapeDataString(target.GetStringValue() ?? "")),
            "urlEncode" => _factory.CreateString(
                Uri.EscapeDataString(target.GetStringValue() ?? "")),
            "split" => _factory.CreateArray(
                (target.GetStringValue() ?? "").Split(args[0].GetStringValue() ?? ",")
                    .Select(s => _factory.CreateString(s))),
            "concat" => EvaluateConcatMethod(target, args),

            // Hashing & Crypto
            "hash" => EvaluateHash(target, args),
            "rsaSign" => EvaluateRsaSign(target, args),

            // String → Array
            "toCharArray" => _factory.CreateArray(
                (target.GetStringValue() ?? "").Select(c => _factory.CreateString(c.ToString()))),

            // Membership
            "in" => EvaluateIn(target, args),

            // Object manipulation
            "clone" => target.DeepClone(),
            "keep" => EvaluateKeep(target, args),
            "remove" => EvaluateRemove(target, args),
            "omitNulls" or "omitnulls" => EvaluateOmitNulls(target),

            // Generators
            "newGuid" or "newguid" => _factory.CreateString(Guid.NewGuid().ToString()),

            // Collection methods
            "count" => _factory.CreateNumber(
                target.Kind == ElwoodValueKind.Array ? target.GetArrayLength() : 1),
            "first" => target.Kind == ElwoodValueKind.Array
                ? target.EnumerateArray().FirstOrDefault() ?? _factory.CreateNull()
                : target.Kind == ElwoodValueKind.String && (target.GetStringValue()?.Length ?? 0) > 0
                    ? _factory.CreateString(target.GetStringValue()![..1])
                    : target,
            "last" => target.Kind == ElwoodValueKind.Array
                ? target.EnumerateArray().LastOrDefault() ?? _factory.CreateNull()
                : target.Kind == ElwoodValueKind.String && (target.GetStringValue()?.Length ?? 0) > 0
                    ? _factory.CreateString(target.GetStringValue()![^1..])
                    : target,

            "sum" => _factory.CreateNumber(target.EnumerateArray().Sum(i => i.GetNumberValue())),
            "min" => _factory.CreateNumber(target.EnumerateArray().Min(i => i.GetNumberValue())),
            "max" => _factory.CreateNumber(target.EnumerateArray().Max(i => i.GetNumberValue())),

            "index" => target.Kind == ElwoodValueKind.Array
                ? _factory.CreateArray(Enumerable.Range(0, target.GetArrayLength()).Select(i => _factory.CreateNumber(i)))
                : _factory.CreateNumber(0),
            "take" => _factory.CreateArray(target.EnumerateArray().Take((int)args[0].GetNumberValue())),
            "skip" => _factory.CreateArray(target.EnumerateArray().Skip((int)args[0].GetNumberValue())),

            // Type conversion / coercion
            "not" => _factory.CreateBool(!IsTruthy(target)),
            "boolean" => _factory.CreateBool(args.Count > 0 ? IsTruthy(args[0]) : IsTruthy(target)),
            "toString" => EvaluateToString(target, args),
            "toNumber" => _factory.CreateNumber(double.TryParse(target.GetStringValue(), out var n) ? n : 0),
            "convertTo" => EvaluateConvertTo(target, args),
            "parseJson" => EvaluateParseJson(target),

            // Null/empty checks — with optional fallback: .isNullOrEmpty(fallback)
            "isNull" => EvaluateNullCheck(target, args, checkWhitespace: false, nullOnly: true),
            "isEmpty" => EvaluateNullCheck(target, args, checkWhitespace: false, nullOnly: false),
            "isNullOrEmpty" => EvaluateNullCheck(target, args, checkWhitespace: false, nullOnly: false),
            "isNullOrWhiteSpace" => EvaluateNullCheck(target, args, checkWhitespace: true, nullOnly: false),

            // Sanitize
            "sanitize" => _factory.CreateString(Sanitize(target.GetStringValue() ?? "")),

            // Math
            "round" => EvaluateRound(target, args),
            "floor" => _factory.CreateNumber(Math.Floor(target.GetNumberValue())),
            "ceiling" => _factory.CreateNumber(Math.Ceiling(target.GetNumberValue())),
            "truncate" => _factory.CreateNumber(Math.Truncate(target.GetNumberValue())),
            "abs" => _factory.CreateNumber(Math.Abs(target.GetNumberValue())),

            // Generators
            "range" => _factory.CreateArray(
                Enumerable.Range((int)args[0].GetNumberValue(), (int)args[1].GetNumberValue())
                    .Select(i => _factory.CreateNumber(i))),

            // Format I/O
            "fromCsv" => EvaluateFromCsv(target, args),
            "toCsv" => EvaluateToCsv(target, args),
            "fromXml" => EvaluateFromXml(target, args),
            "toXml" => EvaluateToXml(target, args),
            "fromText" => EvaluateFromText(target, args),
            "toText" => EvaluateToText(target, args),

            // DateTime
            "add" => EvaluateAdd(target, args),
            "dateFormat" => EvaluateDateFormat(target, args),
            "tryDateFormat" => EvaluateDateFormat(target, args),
            "now" => EvaluateNow(args),
            "utcNow" or "utcnow" => _factory.CreateString(DateTime.UtcNow.ToString(
                args.Count > 0 ? args[0].GetStringValue() ?? "yyyy-MM-ddTHH:mm:ssZ" : "yyyy-MM-ddTHH:mm:ssZ",
                System.Globalization.CultureInfo.InvariantCulture)),
            "toUnixTimeSeconds" => EvaluateToUnixTimeSeconds(target, args),

            _ => CallExtensionOrThrow(name, target, args, span)
        };
    }

    private IElwoodValue CallExtensionOrThrow(string name, IElwoodValue target, List<IElwoodValue> args, SourceSpan span)
    {
        if (_extensions is not null && _extensions.TryGetMethod(name, out var handler))
        {
            try { return handler!(target, args, _factory); }
            catch (ElwoodEvaluationException) { throw; }
            catch (Exception ex) { throw new ElwoodEvaluationException(ex.Message, span); }
        }

        throw new ElwoodEvaluationException(
            $"Unknown method '{name}'.", span,
            $"Available methods: toLower, toUpper, trim, length, contains, replace, substring, split, count, toString, toNumber, round, floor, ceiling, abs, now");
    }

    // ── Format I/O ──

    private IElwoodValue EvaluateFromCsv(IElwoodValue target, List<IElwoodValue> args)
    {
        var csv = target.GetStringValue() ?? "";
        var delimiter = ",";
        var hasHeaders = true;
        var quote = '"';
        var skipRows = 0;
        var parseJson = false;

        // Parse options object if provided
        if (args.Count > 0 && args[0].Kind == ElwoodValueKind.Object)
        {
            var opts = args[0];
            var d = opts.GetProperty("delimiter");
            if (d is not null) delimiter = d.GetStringValue() ?? ",";
            var h = opts.GetProperty("headers");
            if (h is not null) hasHeaders = IsTruthy(h);
            var q = opts.GetProperty("quote");
            if (q is not null && (q.GetStringValue()?.Length ?? 0) > 0) quote = q.GetStringValue()![0];
            var s = opts.GetProperty("skipRows");
            if (s is not null) skipRows = (int)s.GetNumberValue();
            var pj = opts.GetProperty("parseJson");
            if (pj is not null) parseJson = IsTruthy(pj);
        }

        var lines = ParseCsvLines(csv, delimiter[0], quote);

        // Skip leading rows (metadata/title rows before headers or data)
        if (skipRows > 0 && skipRows < lines.Count)
            lines = lines.Skip(skipRows).ToList();

        if (lines.Count == 0) return _factory.CreateArray([]);

        IElwoodValue CellValue(string val)
        {
            if (parseJson && !string.IsNullOrWhiteSpace(val))
            {
                var trimmed = val.Trim();
                if ((trimmed.StartsWith('{') && trimmed.EndsWith('}')) ||
                    (trimmed.StartsWith('[') && trimmed.EndsWith(']')))
                {
                    try { return _factory.Parse(trimmed); } catch { }
                }
            }
            return _factory.CreateString(val);
        }

        if (hasHeaders && lines.Count >= 1)
        {
            var headers = lines[0];
            var rows = new List<IElwoodValue>();
            for (var i = 1; i < lines.Count; i++)
            {
                var row = lines[i];
                var props = new List<KeyValuePair<string, IElwoodValue>>();
                for (var j = 0; j < headers.Count; j++)
                {
                    var val = j < row.Count ? row[j] : "";
                    props.Add(new KeyValuePair<string, IElwoodValue>(headers[j], CellValue(val)));
                }
                rows.Add(_factory.CreateObject(props));
            }
            return _factory.CreateArray(rows);
        }
        else
        {
            // No headers: return array of objects with auto-generated column names (A, B, C, ... Z, AA, AB, ...)
            var maxCols = lines.Max(r => r.Count);
            var colNames = Enumerable.Range(0, maxCols).Select(GetAlphabeticColumnName).ToList();

            return _factory.CreateArray(lines.Select(row =>
            {
                var props = new List<KeyValuePair<string, IElwoodValue>>();
                for (var j = 0; j < colNames.Count; j++)
                {
                    var val = j < row.Count ? row[j] : "";
                    props.Add(new KeyValuePair<string, IElwoodValue>(colNames[j], CellValue(val)));
                }
                return _factory.CreateObject(props);
            }));
        }
    }

    private static string GetAlphabeticColumnName(int index)
    {
        var name = "";
        index++;
        while (index > 0)
        {
            index--;
            name = (char)('A' + index % 26) + name;
            index /= 26;
        }
        return name;
    }

    private static List<List<string>> ParseCsvLines(string csv, char delimiter, char quote)
    {
        var lines = new List<List<string>>();
        var fields = new List<string>();
        var field = new System.Text.StringBuilder();
        var inQuotes = false;
        var i = 0;

        while (i < csv.Length)
        {
            var c = csv[i];
            if (inQuotes)
            {
                if (c == quote && i + 1 < csv.Length && csv[i + 1] == quote)
                {
                    field.Append(quote); i += 2;
                }
                else if (c == quote)
                {
                    inQuotes = false; i++;
                }
                else
                {
                    field.Append(c); i++;
                }
            }
            else if (c == quote)
            {
                inQuotes = true; i++;
            }
            else if (c == delimiter)
            {
                fields.Add(field.ToString()); field.Clear(); i++;
            }
            else if (c == '\n' || (c == '\r' && i + 1 < csv.Length && csv[i + 1] == '\n'))
            {
                fields.Add(field.ToString()); field.Clear();
                if (fields.Any(f => f.Length > 0) || fields.Count > 1)
                    lines.Add(fields);
                fields = new List<string>();
                i += c == '\r' ? 2 : 1;
            }
            else
            {
                field.Append(c); i++;
            }
        }

        fields.Add(field.ToString());
        if (fields.Any(f => f.Length > 0) || fields.Count > 1)
            lines.Add(fields);

        return lines;
    }

    private IElwoodValue EvaluateToCsv(IElwoodValue target, List<IElwoodValue> args)
    {
        var delimiter = ",";
        var includeHeaders = true;
        var quote = '"';
        var alwaysQuote = false;

        if (args.Count > 0 && args[0].Kind == ElwoodValueKind.Object)
        {
            var opts = args[0];
            var d = opts.GetProperty("delimiter");
            if (d is not null) delimiter = d.GetStringValue() ?? ",";
            var h = opts.GetProperty("headers");
            if (h is not null) includeHeaders = IsTruthy(h);
            var aq = opts.GetProperty("alwaysQuote");
            if (aq is not null) alwaysQuote = IsTruthy(aq);
        }

        var items = target.EnumerateArray().ToList();
        if (items.Count == 0) return _factory.CreateString("");

        var sb = new System.Text.StringBuilder();
        var delim = delimiter[0];

        // Collect all property names from all rows for consistent columns
        var allKeys = new List<string>();
        foreach (var item in items)
        {
            if (item.Kind == ElwoodValueKind.Object)
            {
                foreach (var key in item.GetPropertyNames())
                    if (!allKeys.Contains(key)) allKeys.Add(key);
            }
        }

        var rows = new List<string>();

        if (includeHeaders && allKeys.Count > 0)
        {
            rows.Add(string.Join(delim, allKeys.Select(k => CsvEscape(k, delim, quote, alwaysQuote))));
        }

        foreach (var item in items)
        {
            if (item.Kind == ElwoodValueKind.Object)
            {
                var values = allKeys.Select(k =>
                {
                    var v = item.GetProperty(k);
                    return v is not null ? ValueToString(v) : "";
                });
                rows.Add(string.Join(delim, values.Select(v => CsvEscape(v, delim, quote, alwaysQuote))));
            }
            else if (item.Kind == ElwoodValueKind.Array)
            {
                var values = item.EnumerateArray().Select(v => CsvEscape(ValueToString(v), delim, quote, alwaysQuote));
                rows.Add(string.Join(delim, values));
            }
        }

        sb.Append(string.Join('\n', rows));

        return _factory.CreateString(sb.ToString().TrimEnd('\r', '\n'));
    }

    private static string CsvEscape(string value, char delimiter, char quote, bool alwaysQuote = false)
    {
        if (alwaysQuote || value.Contains(delimiter) || value.Contains(quote) || value.Contains('\n') || value.Contains('\r'))
            return $"{quote}{value.Replace(quote.ToString(), $"{quote}{quote}")}{quote}";
        return value;
    }

    // ── XML ──

    private IElwoodValue EvaluateFromXml(IElwoodValue target, List<IElwoodValue> args)
    {
        var xml = target.GetStringValue() ?? "";
        var attrPrefix = "@";
        var stripNamespaces = true;

        if (args.Count > 0 && args[0].Kind == ElwoodValueKind.Object)
        {
            var ap = args[0].GetProperty("attributePrefix");
            if (ap is not null) attrPrefix = ap.GetStringValue() ?? "@";
            var sn = args[0].GetProperty("stripNamespaces");
            if (sn is not null) stripNamespaces = IsTruthy(sn);
        }

        try
        {
            var doc = XDocument.Parse(xml);
            if (doc.Root is null) return _factory.CreateNull();
            return XmlElementToValue(doc.Root, attrPrefix, stripNamespaces);
        }
        catch
        {
            return _factory.CreateNull();
        }
    }

    private IElwoodValue XmlElementToValue(XElement element, string attrPrefix, bool stripNs)
    {
        var localName = stripNs ? element.Name.LocalName : element.Name.ToString();
        var props = new List<KeyValuePair<string, IElwoodValue>>();

        // Attributes
        foreach (var attr in element.Attributes())
        {
            if (attr.IsNamespaceDeclaration && stripNs) continue;
            var name = stripNs ? attr.Name.LocalName : attr.Name.ToString();
            props.Add(new KeyValuePair<string, IElwoodValue>(attrPrefix + name, _factory.CreateString(attr.Value)));
        }

        // Group child elements by name to detect arrays
        var childElements = element.Elements().ToList();
        if (childElements.Count > 0)
        {
            var groups = childElements.GroupBy(e => stripNs ? e.Name.LocalName : e.Name.ToString()).ToList();
            foreach (var group in groups)
            {
                var items = group.ToList();
                if (items.Count == 1)
                {
                    props.Add(new KeyValuePair<string, IElwoodValue>(group.Key, XmlChildToValue(items[0], attrPrefix, stripNs)));
                }
                else
                {
                    // Multiple same-named elements → array
                    var arr = items.Select(e => XmlChildToValue(e, attrPrefix, stripNs));
                    props.Add(new KeyValuePair<string, IElwoodValue>(group.Key, _factory.CreateArray(arr)));
                }
            }

            // Mixed content: if there's also direct text alongside child elements
            var textParts = element.Nodes().OfType<XText>().Select(t => t.Value.Trim()).Where(t => t.Length > 0).ToList();
            if (textParts.Count > 0)
                props.Add(new KeyValuePair<string, IElwoodValue>("#text", _factory.CreateString(string.Join(" ", textParts))));
        }
        else
        {
            // Leaf element: text content
            var text = element.Value;
            if (!string.IsNullOrEmpty(text) || props.Count > 0)
            {
                if (props.Count > 0)
                {
                    // Has attributes + text
                    props.Add(new KeyValuePair<string, IElwoodValue>("#text", _factory.CreateString(text)));
                }
                else
                {
                    // Simple text-only element → just the string, wrapped by caller
                    return _factory.CreateString(text);
                }
            }
        }

        // Wrap in root element name
        var inner = _factory.CreateObject(props);
        return _factory.CreateObject([new KeyValuePair<string, IElwoodValue>(localName, inner)]);
    }

    private IElwoodValue XmlChildToValue(XElement element, string attrPrefix, bool stripNs)
    {
        var hasAttrs = element.Attributes().Any(a => !(a.IsNamespaceDeclaration && stripNs));
        var hasChildren = element.HasElements;

        if (!hasAttrs && !hasChildren)
        {
            // Simple leaf → just the string value
            return _factory.CreateString(element.Value);
        }

        // Complex element → object with attributes and children
        var props = new List<KeyValuePair<string, IElwoodValue>>();

        foreach (var attr in element.Attributes())
        {
            if (attr.IsNamespaceDeclaration && stripNs) continue;
            var name = stripNs ? attr.Name.LocalName : attr.Name.ToString();
            props.Add(new KeyValuePair<string, IElwoodValue>(attrPrefix + name, _factory.CreateString(attr.Value)));
        }

        var childElements = element.Elements().ToList();
        if (childElements.Count > 0)
        {
            var groups = childElements.GroupBy(e => stripNs ? e.Name.LocalName : e.Name.ToString()).ToList();
            foreach (var group in groups)
            {
                var items = group.ToList();
                if (items.Count == 1)
                    props.Add(new KeyValuePair<string, IElwoodValue>(group.Key, XmlChildToValue(items[0], attrPrefix, stripNs)));
                else
                    props.Add(new KeyValuePair<string, IElwoodValue>(group.Key,
                        _factory.CreateArray(items.Select(e => XmlChildToValue(e, attrPrefix, stripNs)))));
            }

            var textParts = element.Nodes().OfType<XText>().Select(t => t.Value.Trim()).Where(t => t.Length > 0).ToList();
            if (textParts.Count > 0)
                props.Add(new KeyValuePair<string, IElwoodValue>("#text", _factory.CreateString(string.Join(" ", textParts))));
        }
        else if (!string.IsNullOrEmpty(element.Value))
        {
            props.Add(new KeyValuePair<string, IElwoodValue>("#text", _factory.CreateString(element.Value)));
        }

        return _factory.CreateObject(props);
    }

    private IElwoodValue EvaluateToXml(IElwoodValue target, List<IElwoodValue> args)
    {
        var attrPrefix = "@";
        var rootElement = (string?)null;
        var declaration = true;

        if (args.Count > 0 && args[0].Kind == ElwoodValueKind.Object)
        {
            var ap = args[0].GetProperty("attributePrefix");
            if (ap is not null) attrPrefix = ap.GetStringValue() ?? "@";
            var re = args[0].GetProperty("rootElement");
            if (re is not null) rootElement = re.GetStringValue();
            var decl = args[0].GetProperty("declaration");
            if (decl is not null) declaration = IsTruthy(decl);
        }

        if (target.Kind != ElwoodValueKind.Object)
            return _factory.CreateString("");

        XElement root;
        var propNames = target.GetPropertyNames().ToList();

        if (rootElement is not null)
        {
            root = ValueToXElement(rootElement, target, attrPrefix);
        }
        else if (propNames.Count == 1 && !propNames[0].StartsWith(attrPrefix))
        {
            // Single top-level key is the root element name
            var child = target.GetProperty(propNames[0]);
            root = child is not null ? ValueToXElement(propNames[0], child, attrPrefix) : new XElement(propNames[0]);
        }
        else
        {
            root = ValueToXElement("root", target, attrPrefix);
        }

        var sb = new System.Text.StringBuilder();
        if (declaration)
            sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
        sb.Append(root.ToString().Replace("\r\n", "\n"));
        return _factory.CreateString(sb.ToString());
    }

    private XElement ValueToXElement(string name, IElwoodValue value, string attrPrefix)
    {
        var el = new XElement(name);

        if (value.Kind == ElwoodValueKind.Object)
        {
            foreach (var prop in value.GetPropertyNames())
            {
                var propVal = value.GetProperty(prop);
                if (propVal is null) continue;

                if (prop.StartsWith(attrPrefix))
                {
                    // Attribute
                    var attrName = prop[attrPrefix.Length..];
                    if (attrName.Length > 0)
                        el.SetAttributeValue(attrName, ValueToString(propVal));
                }
                else if (prop == "#text")
                {
                    el.Add(new XText(ValueToString(propVal)));
                }
                else if (propVal.Kind == ElwoodValueKind.Array)
                {
                    // Array → repeated elements
                    foreach (var item in propVal.EnumerateArray())
                        el.Add(ValueToXElement(prop, item, attrPrefix));
                }
                else if (propVal.Kind == ElwoodValueKind.Object)
                {
                    el.Add(ValueToXElement(prop, propVal, attrPrefix));
                }
                else
                {
                    // Scalar → child element with text
                    el.Add(new XElement(prop, ValueToString(propVal)));
                }
            }
        }
        else
        {
            el.Value = ValueToString(value);
        }

        return el;
    }

    private IElwoodValue EvaluateFromText(IElwoodValue target, List<IElwoodValue> args)
    {
        var text = target.GetStringValue() ?? "";
        var delimiter = "\n";

        if (args.Count > 0 && args[0].Kind == ElwoodValueKind.Object)
        {
            var d = args[0].GetProperty("delimiter");
            if (d is not null) delimiter = d.GetStringValue() ?? "\n";
        }

        var lines = text.Split(delimiter).Select(l => _factory.CreateString(l.TrimEnd('\r')));
        return _factory.CreateArray(lines);
    }

    private IElwoodValue EvaluateToText(IElwoodValue target, List<IElwoodValue> args)
    {
        var delimiter = "\n";

        if (args.Count > 0 && args[0].Kind == ElwoodValueKind.Object)
        {
            var d = args[0].GetProperty("delimiter");
            if (d is not null) delimiter = d.GetStringValue() ?? "\n";
        }

        if (target.Kind == ElwoodValueKind.Array)
            return _factory.CreateString(string.Join(delimiter, target.EnumerateArray().Select(ValueToString)));
        return _factory.CreateString(ValueToString(target));
    }

    private IElwoodValue EvaluateHash(IElwoodValue target, List<IElwoodValue> args)
    {
        var input = target.GetStringValue() ?? "";
        var desiredLength = args.Count > 0 ? (int)args[0].GetNumberValue() : 32;

        using var md5 = System.Security.Cryptography.MD5.Create();
        var hashBytes = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input));
        var fullHash = BitConverter.ToString(hashBytes).Replace("-", "").ToLower();
        var shortHash = fullHash[..Math.Min(desiredLength, fullHash.Length)];

        return _factory.CreateString(shortHash);
    }

    private IElwoodValue EvaluateRsaSign(IElwoodValue target, List<IElwoodValue> args)
    {
        // rsaSign(data, privateKeyPem)
        // Signs data with an RSA private key using SHA1 + PKCS1 padding.
        // Matches legacy .RsaCryptoSignature() behavior (reversed signature bytes).
        var data = args.Count > 0 ? args[0].GetStringValue() ?? "" : target.GetStringValue() ?? "";
        var keyPem = args.Count > 1 ? args[1].GetStringValue() ?? "" : args[0].GetStringValue() ?? "";

        // Strip PEM headers
        var keyBody = keyPem
            .Replace("-----BEGIN RSA PRIVATE KEY-----", "")
            .Replace("-----END RSA PRIVATE KEY-----", "")
            .Replace("\n", "")
            .Replace("\r", "");

        var keyBytes = Convert.FromBase64String(keyBody);
        using var rsa = System.Security.Cryptography.RSA.Create();
        rsa.ImportRSAPrivateKey(keyBytes, out _);

        var dataBytes = System.Text.Encoding.ASCII.GetBytes(data);
        var signature = rsa.SignData(dataBytes,
            System.Security.Cryptography.HashAlgorithmName.SHA1,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);

        // Legacy implementation reverses the signature bytes
        Array.Reverse(signature);

        return _factory.CreateString(Convert.ToBase64String(signature));
    }

    private IElwoodValue EvaluateNow(List<IElwoodValue> args)
    {
        var format = args.Count > 0 ? args[0].GetStringValue() ?? "yyyy-MM-ddTHH:mm:ssZ" : "yyyy-MM-ddTHH:mm:ssZ";

        if (args.Count >= 2)
        {
            // now(format, timezone) — convert UTC to timezone
            var tzId = args[1].GetStringValue() ?? "UTC";
            var tz = TimeZoneInfo.FindSystemTimeZoneById(tzId);
            var localTime = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
            return _factory.CreateString(localTime.ToString(format, System.Globalization.CultureInfo.InvariantCulture));
        }

        // now(format) — UTC
        return _factory.CreateString(DateTime.UtcNow.ToString(format, System.Globalization.CultureInfo.InvariantCulture));
    }

    private IElwoodValue EvaluateToUnixTimeSeconds(IElwoodValue target, List<IElwoodValue> args)
    {
        DateTimeOffset dto;
        if (args.Count >= 1)
        {
            // toUnixTimeSeconds(dateString) — convert given date
            var input = args[0].GetStringValue() ?? "";
            dto = DateTimeOffset.Parse(input, System.Globalization.CultureInfo.InvariantCulture);
        }
        else if (target.Kind == ElwoodValueKind.String && DateTime.TryParse(target.GetStringValue(),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
        {
            // toUnixTimeSeconds() on a date string
            dto = new DateTimeOffset(dt);
        }
        else
        {
            // No arg, no date target — use current UTC
            dto = DateTimeOffset.UtcNow;
        }
        return _factory.CreateString(dto.ToUnixTimeSeconds().ToString());
    }

    private IElwoodValue EvaluateDateFormat(IElwoodValue target, List<IElwoodValue> args)
    {
        var dateStr = target.GetStringValue() ?? "";
        var outputFormat = args.Count > 0 ? args[0].GetStringValue() ?? "yyyy-MM-ddTHH:mm:ssZ" : "yyyy-MM-ddTHH:mm:ssZ";

        // Two-arg form: dateFormat(inputFormat, outputFormat)
        // One-arg form: dateFormat(outputFormat) — auto-parse input
        if (args.Count >= 2)
        {
            var inputFormat = args[0].GetStringValue() ?? "";
            outputFormat = args[1].GetStringValue() ?? "yyyy-MM-ddTHH:mm:ssZ";
            if (DateTime.TryParseExact(dateStr, inputFormat, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var exactDate))
            {
                return _factory.CreateString(exactDate.ToString(outputFormat, System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        // One-arg: auto-parse, format with the given output format
        if (DateTime.TryParse(dateStr, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var date))
        {
            return _factory.CreateString(date.ToString(outputFormat, System.Globalization.CultureInfo.InvariantCulture));
        }

        return target; // can't parse — return as-is
    }

    private IElwoodValue EvaluateAdd(IElwoodValue target, List<IElwoodValue> args)
    {
        var dateStr = target.GetStringValue() ?? "";

        // Try parsing as DateTime
        if (DateTime.TryParse(dateStr, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var date))
        {
            var tsStr = args.Count > 0 ? args[0].GetStringValue() ?? "" : "";
            if (TimeSpan.TryParse(tsStr, out var ts))
            {
                var result = date.Add(ts);
                // Preserve the original format style
                var format = date.Kind == DateTimeKind.Utc ? "yyyy-MM-ddTHH:mm:ssZ" : "yyyy-MM-ddTHH:mm:ss";
                return _factory.CreateString(result.ToString(format));
            }
        }

        // Fallback: numeric addition
        if (target.Kind == ElwoodValueKind.Number && args.Count > 0)
            return _factory.CreateNumber(target.GetNumberValue() + args[0].GetNumberValue());

        return target;
    }

    private IElwoodValue EvaluateNullCheck(IElwoodValue target, List<IElwoodValue> args, bool checkWhitespace, bool nullOnly)
    {
        bool isEmpty;
        if (nullOnly)
        {
            isEmpty = target.Kind == ElwoodValueKind.Null;
        }
        else
        {
            isEmpty = target.Kind == ElwoodValueKind.Null ||
                (target.Kind == ElwoodValueKind.String &&
                    (checkWhitespace ? string.IsNullOrWhiteSpace(target.GetStringValue()) : string.IsNullOrEmpty(target.GetStringValue()))) ||
                (target.Kind == ElwoodValueKind.Array && target.GetArrayLength() == 0);
        }

        // With fallback arg: return fallback if empty, otherwise return original value
        if (args.Count > 0)
            return isEmpty ? args[0] : target;

        // No arg: return boolean
        return _factory.CreateBool(isEmpty);
    }

    private static string Sanitize(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return s;

        var charMap = new Dictionary<char, string>
        {
            {'ß', "ss"},
            {'Α', "A"}, {'Β', "B"}, {'Γ', "G"}, {'Δ', "D"}, {'Ε', "E"}, {'Ζ', "Z"}, {'Η', "H"}, {'Θ', "Th"},
            {'Ι', "I"}, {'Κ', "K"}, {'Λ', "L"}, {'Μ', "M"}, {'Ν', "N"}, {'Ξ', "X"}, {'Ο', "O"}, {'Π', "P"},
            {'Ρ', "R"}, {'Σ', "S"}, {'Τ', "T"}, {'Υ', "Y"}, {'Φ', "Ph"}, {'Χ', "Ch"}, {'Ψ', "Ps"}, {'Ω', "O"},
            {'α', "a"}, {'β', "b"}, {'γ', "g"}, {'δ', "d"}, {'ε', "e"}, {'ζ', "z"}, {'η', "h"}, {'θ', "th"},
            {'ι', "i"}, {'κ', "k"}, {'λ', "l"}, {'μ', "m"}, {'ν', "n"}, {'ξ', "x"}, {'ο', "o"}, {'π', "p"},
            {'ρ', "r"}, {'σ', "s"}, {'τ', "t"}, {'υ', "y"}, {'φ', "ph"}, {'χ', "ch"}, {'ψ', "ps"}, {'ω', "o"}
        };

        var normalized = s.Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder();

        foreach (var c in normalized)
        {
            var category = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c);
            if (category != System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                sb.Append(charMap.TryGetValue(c, out var replacement) ? replacement : c.ToString());
            }
        }

        return sb.ToString().Normalize(System.Text.NormalizationForm.FormC);
    }

    private IElwoodValue EvaluateToString(IElwoodValue target, List<IElwoodValue> args)
    {
        if (args.Count == 0)
            return _factory.CreateString(ValueToString(target));

        var format = args[0].GetStringValue() ?? "";

        // Number formatting: .toString("F2"), .toString("D5"), .toString("N0")
        if (target.Kind == ElwoodValueKind.Number)
            return _factory.CreateString(target.GetNumberValue().ToString(format, System.Globalization.CultureInfo.InvariantCulture));

        // Date formatting (if the string is a parseable date)
        if (target.Kind == ElwoodValueKind.String &&
            DateTime.TryParse(target.GetStringValue(), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
            return _factory.CreateString(dt.ToString(format, System.Globalization.CultureInfo.InvariantCulture));

        return _factory.CreateString(ValueToString(target));
    }

    private IElwoodValue EvaluateToLowerUpper(IElwoodValue target, List<IElwoodValue> args, bool upper)
    {
        var str = target.GetStringValue() ?? "";
        if (args.Count == 1 && args[0].Kind == ElwoodValueKind.Number)
        {
            // Position-based: toLower(1) / toUpper(1) — 1-based index
            var pos = (int)args[0].GetNumberValue() - 1; // convert to 0-based
            if (pos >= 0 && pos < str.Length)
            {
                var chars = str.ToCharArray();
                chars[pos] = upper ? char.ToUpper(chars[pos]) : char.ToLower(chars[pos]);
                return _factory.CreateString(new string(chars));
            }
            return _factory.CreateString(str);
        }
        return _factory.CreateString(upper ? str.ToUpperInvariant() : str.ToLowerInvariant());
    }

    private IElwoodValue EvaluateLeft(IElwoodValue target, List<IElwoodValue> args)
    {
        var str = target.GetStringValue() ?? "";
        var len = args.Count > 0 ? (int)args[0].GetNumberValue() : 1;
        len = Math.Min(len, str.Length);
        return _factory.CreateString(str[..len]);
    }

    private IElwoodValue EvaluateRight(IElwoodValue target, List<IElwoodValue> args)
    {
        var str = target.GetStringValue() ?? "";
        var len = args.Count > 0 ? (int)args[0].GetNumberValue() : 1;
        len = Math.Min(len, str.Length);
        return _factory.CreateString(str[^len..]);
    }

    private IElwoodValue EvaluateConcatMethod(IElwoodValue target, List<IElwoodValue> args)
    {
        // .concat()              → join target array with "|"
        // .concat(sep)           → join target array with sep
        // .concat(sep, arr, ...) → merge target + additional arrays, join with sep
        var separator = args.Count > 0 ? ValueToString(args[0]) : "|";

        // Collect all items: target array + any additional collections from args[1:]
        var items = new List<string>();
        foreach (var item in target.EnumerateArray())
            items.Add(ValueToString(item));

        for (int i = 1; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg.Kind == ElwoodValueKind.Array)
            {
                foreach (var item in arg.EnumerateArray())
                    items.Add(ValueToString(item));
            }
            else
            {
                items.Add(ValueToString(arg));
            }
        }

        return _factory.CreateString(string.Join(separator, items));
    }

    private IElwoodValue EvaluateKeep(IElwoodValue target, List<IElwoodValue> args)
    {
        var names = new HashSet<string>(args.Select(a => a.GetStringValue() ?? ""));
        if (target.Kind == ElwoodValueKind.Object)
        {
            var props = target.GetPropertyNames()
                .Where(n => names.Contains(n))
                .Select(n => new KeyValuePair<string, IElwoodValue>(n, target.GetProperty(n)!));
            return _factory.CreateObject(props);
        }
        // Auto-map over arrays
        if (target.Kind == ElwoodValueKind.Array)
            return _factory.CreateArray(target.EnumerateArray().Select(item => EvaluateKeep(item, args)));
        return target;
    }

    private IElwoodValue EvaluateRemove(IElwoodValue target, List<IElwoodValue> args)
    {
        var names = new HashSet<string>(args.Select(a => a.GetStringValue() ?? ""));
        if (target.Kind == ElwoodValueKind.Object)
        {
            var props = target.GetPropertyNames()
                .Where(n => !names.Contains(n))
                .Select(n => new KeyValuePair<string, IElwoodValue>(n, target.GetProperty(n)!));
            return _factory.CreateObject(props);
        }
        // Auto-map over arrays
        if (target.Kind == ElwoodValueKind.Array)
            return _factory.CreateArray(target.EnumerateArray().Select(item => EvaluateRemove(item, args)));
        return target;
    }

    private IElwoodValue EvaluateOmitNulls(IElwoodValue target)
    {
        if (target.Kind == ElwoodValueKind.Object)
        {
            var props = target.GetPropertyNames()
                .Where(n => target.GetProperty(n) is { Kind: not ElwoodValueKind.Null })
                .Select(n => new KeyValuePair<string, IElwoodValue>(n, target.GetProperty(n)!));
            return _factory.CreateObject(props);
        }
        if (target.Kind == ElwoodValueKind.Array)
            return _factory.CreateArray(target.EnumerateArray().Select(item => EvaluateOmitNulls(item)));
        return target;
    }

    private IElwoodValue EvaluateRound(IElwoodValue target, List<IElwoodValue> args)
    {
        var value = target.GetNumberValue();
        var decimals = 0;
        var mode = MidpointRounding.AwayFromZero; // legacy default

        foreach (var arg in args)
        {
            if (arg.Kind == ElwoodValueKind.String)
            {
                mode = (arg.GetStringValue()?.ToLower()) switch
                {
                    "awayfromzero" => MidpointRounding.AwayFromZero,
                    "toeven" => MidpointRounding.ToEven,
                    "tozero" => MidpointRounding.ToZero,
                    _ => MidpointRounding.AwayFromZero
                };
            }
            else
            {
                decimals = (int)arg.GetNumberValue();
            }
        }

        return _factory.CreateNumber(Math.Round(value, decimals, mode));
    }

    private IElwoodValue EvaluateConvertTo(IElwoodValue target, List<IElwoodValue> args)
    {
        var typeName = args.Count > 0 ? args[0].GetStringValue()?.ToLower() ?? "" : "";
        var str = ValueToString(target);
        return typeName switch
        {
            "int32" or "int" or "integer" =>
                // Parse as double first (handles "5.600"), then truncate to int
                _factory.CreateNumber(double.TryParse(str, System.Globalization.CultureInfo.InvariantCulture, out var di)
                    ? (int)di
                    : (int)target.GetNumberValue()),
            "int64" or "long" =>
                _factory.CreateNumber(double.TryParse(str, System.Globalization.CultureInfo.InvariantCulture, out var dl)
                    ? (long)dl
                    : (long)target.GetNumberValue()),
            "double" or "float" or "decimal" =>
                _factory.CreateNumber(double.TryParse(str, System.Globalization.CultureInfo.InvariantCulture, out var d)
                    ? d
                    : target.GetNumberValue()),
            "boolean" or "bool" =>
                // Match legacy behavior: "1"→true (parsed as double 1≠0), "true"→true, ""→false, "0"→false
                double.TryParse(str, System.Globalization.CultureInfo.InvariantCulture, out var bd)
                    ? _factory.CreateBool(bd != 0)
                    : bool.TryParse(str, out var b)
                        ? _factory.CreateBool(b)
                        : _factory.CreateBool(!string.IsNullOrWhiteSpace(str)),
            "string" => _factory.CreateString(ValueToString(target)),
            _ => target
        };
    }

    private IElwoodValue EvaluateParseJson(IElwoodValue target)
    {
        var str = target.GetStringValue();
        if (string.IsNullOrWhiteSpace(str))
            return _factory.CreateNull();
        try
        {
            return _factory.Parse(str);
        }
        catch
        {
            return _factory.CreateNull();
        }
    }

    private IElwoodValue EvaluateIn(IElwoodValue target, List<IElwoodValue> args)
    {
        // Flatten all arguments: arrays are expanded, scalars included directly
        var candidates = args.SelectMany(arg =>
            arg.Kind == ElwoodValueKind.Array
                ? arg.EnumerateArray()
                : new[] { arg });

        return _factory.CreateBool(candidates.Any(c => ValuesEqual(target, c)));
    }

    private IElwoodValue EvaluateSubstring(IElwoodValue target, List<IElwoodValue> args)
    {
        var str = target.GetStringValue() ?? "";
        var start = (int)args[0].GetNumberValue();
        if (args.Count > 1)
        {
            var len = (int)args[1].GetNumberValue();
            return _factory.CreateString(str.Substring(start, Math.Min(len, str.Length - start)));
        }
        return _factory.CreateString(str[start..]);
    }

    // ── Helpers ──

    private IElwoodValue ArithmeticOp(IElwoodValue left, IElwoodValue right, Func<double, double, double> op)
    {
        // String concatenation with +
        if (left.Kind == ElwoodValueKind.String || right.Kind == ElwoodValueKind.String)
        {
            return _factory.CreateString(ValueToString(left) + ValueToString(right));
        }
        return _factory.CreateNumber(op(left.GetNumberValue(), right.GetNumberValue()));
    }

    private IElwoodValue CompareOp(IElwoodValue left, IElwoodValue right, Func<double, double, bool> op)
    {
        // String comparison when both sides are strings
        if (left.Kind == ElwoodValueKind.String && right.Kind == ElwoodValueKind.String)
        {
            var cmp = string.Compare(left.GetStringValue(), right.GetStringValue(), StringComparison.Ordinal);
            return _factory.CreateBool(op(cmp, 0));
        }
        return _factory.CreateBool(op(left.GetNumberValue(), right.GetNumberValue()));
    }

    private static bool ValuesEqual(IElwoodValue a, IElwoodValue b)
    {
        if (a.Kind != b.Kind) return false;
        return a.Kind switch
        {
            ElwoodValueKind.Null => true,
            ElwoodValueKind.Boolean => a.GetBooleanValue() == b.GetBooleanValue(),
            ElwoodValueKind.Number => Math.Abs(a.GetNumberValue() - b.GetNumberValue()) < 1e-10,
            ElwoodValueKind.String => a.GetStringValue() == b.GetStringValue(),
            _ => Serialize(a) == Serialize(b) // fallback to serialized comparison
        };
    }

    private static bool IsTruthy(IElwoodValue value) => value.Kind switch
    {
        ElwoodValueKind.Null => false,
        ElwoodValueKind.Boolean => value.GetBooleanValue(),
        ElwoodValueKind.Number => value.GetNumberValue() != 0,
        ElwoodValueKind.String => !string.IsNullOrEmpty(value.GetStringValue()),
        ElwoodValueKind.Array => value.GetArrayLength() > 0,
        ElwoodValueKind.Object => true,
        _ => false
    };

    private static string ValueToString(IElwoodValue value) => value.Kind switch
    {
        ElwoodValueKind.String => value.GetStringValue() ?? "",
        ElwoodValueKind.Number => value.GetNumberValue().ToString(System.Globalization.CultureInfo.InvariantCulture),
        ElwoodValueKind.Boolean => value.GetBooleanValue() ? "true" : "false",
        ElwoodValueKind.Null => "",
        _ => Serialize(value)
    };

    internal static string Serialize(IElwoodValue value)
    {
        // Simple serialization for comparison/grouping keys
        return value.Kind switch
        {
            ElwoodValueKind.Null => "null",
            ElwoodValueKind.Boolean => value.GetBooleanValue() ? "true" : "false",
            ElwoodValueKind.Number => value.GetNumberValue().ToString(System.Globalization.CultureInfo.InvariantCulture),
            ElwoodValueKind.String => $"\"{value.GetStringValue()}\"",
            ElwoodValueKind.Array => $"[{string.Join(",", value.EnumerateArray().Select(Serialize))}]",
            ElwoodValueKind.Object => $"{{{string.Join(",", value.GetPropertyNames().Select(n => $"\"{n}\":{Serialize(value.GetProperty(n)!)}"))}}}",
            _ => "?"
        };
    }

    private static string BuildPathString(IReadOnlyList<PathSegment> segments, int upTo)
    {
        var sb = new System.Text.StringBuilder("$");
        for (int i = 0; i <= upTo && i < segments.Count; i++)
        {
            switch (segments[i])
            {
                case PropertySegment p: sb.Append(p.Optional ? "?." : ".").Append(p.Name); break;
                case IndexSegment { Index: null }: sb.Append("[*]"); break;
                case IndexSegment { Index: int idx }: sb.Append('[').Append(idx).Append(']'); break;
                case SliceSegment s: sb.Append('[').Append(s.Start?.ToString() ?? "").Append(':').Append(s.End?.ToString() ?? "").Append(']'); break;
                case RecursiveDescentSegment rd: sb.Append("..").Append(rd.Name); break;
            }
        }
        return sb.ToString();
    }

    private static string? SuggestProperty(string attempted, IElwoodValue target)
    {
        if (target.Kind != ElwoodValueKind.Object) return null;
        var names = target.GetPropertyNames().ToList();
        if (names.Count == 0) return null;

        var closest = names
            .Select(n => (Name: n, Distance: LevenshteinDistance(attempted.ToLower(), n.ToLower())))
            .Where(x => x.Distance <= 3)
            .OrderBy(x => x.Distance)
            .FirstOrDefault();

        if (closest.Name is not null)
            return $"Did you mean '{closest.Name}'? Available: {string.Join(", ", names.Take(10))}";

        return $"Available properties: {string.Join(", ", names.Take(10))}";
    }

    private static int LevenshteinDistance(string s, string t)
    {
        var n = s.Length;
        var m = t.Length;
        var d = new int[n + 1, m + 1];
        for (var i = 0; i <= n; i++) d[i, 0] = i;
        for (var j = 0; j <= m; j++) d[0, j] = j;
        for (var i = 1; i <= n; i++)
            for (var j = 1; j <= m; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + (s[i - 1] == t[j - 1] ? 0 : 1));
        return d[n, m];
    }
}

/// <summary>
/// A memoized function value. Caches results by serialized argument values.
/// </summary>
internal sealed class MemoizedFunctionValue : IElwoodValue
{
    private readonly LambdaExpression _lambda;
    private readonly ElwoodEnvironment _closure;
    private readonly Evaluator _evaluator;
    private readonly IElwoodValueFactory _factory;
    private readonly Dictionary<string, IElwoodValue> _cache = new();

    public MemoizedFunctionValue(LambdaExpression lambda, ElwoodEnvironment closure, Evaluator evaluator, IElwoodValueFactory factory)
    {
        _lambda = lambda;
        _closure = closure;
        _evaluator = evaluator;
        _factory = factory;
    }

    public IElwoodValue Invoke(List<IElwoodValue> args, IElwoodValue current)
    {
        var key = string.Join("|", args.Select(Evaluator.Serialize));
        if (_cache.TryGetValue(key, out var cached))
            return cached;

        var childEnv = _closure.CreateChild();
        for (int i = 0; i < _lambda.Parameters.Count && i < args.Count; i++)
            childEnv.Set(_lambda.Parameters[i], args[i]);

        // Resolve $ paths against the root from the closure (where memo was defined),
        // not the caller's current item
        var root = _closure.Get("$") ?? current;
        var result = _evaluator.Evaluate(_lambda.Body, root, childEnv);
        _cache[key] = result;
        return result;
    }

    // Minimal IElwoodValue implementation — functions are not JSON values
    public ElwoodValueKind Kind => ElwoodValueKind.Object;
    public string? GetStringValue() => "[memo function]";
    public double GetNumberValue() => 0;
    public bool GetBooleanValue() => false;
    public IElwoodValue? GetProperty(string name) => null;
    public IEnumerable<string> GetPropertyNames() => [];
    public IEnumerable<IElwoodValue> EnumerateArray() => [this];
    public int GetArrayLength() => 1;
    public IElwoodValue? Parent => null;
    public IElwoodValue CreateObject(IEnumerable<KeyValuePair<string, IElwoodValue>> properties) => _factory.CreateObject(properties);
    public IElwoodValue CreateArray(IEnumerable<IElwoodValue> items) => _factory.CreateArray(items);
    public IElwoodValue CreateString(string value) => _factory.CreateString(value);
    public IElwoodValue CreateNumber(double value) => _factory.CreateNumber(value);
    public IElwoodValue CreateBool(bool value) => _factory.CreateBool(value);
    public IElwoodValue CreateNull() => _factory.CreateNull();
    public IElwoodValue DeepClone() => this;
}

public class ElwoodEvaluationException : Exception
{
    public SourceSpan Span { get; }
    public string? Suggestion { get; }
    public string BaseMessage { get; }

    public ElwoodEvaluationException(string message, SourceSpan span, string? suggestion = null)
        : base(suggestion is not null ? $"{message} {suggestion}" : message)
    {
        BaseMessage = message;
        Span = span;
        Suggestion = suggestion;
    }
}
