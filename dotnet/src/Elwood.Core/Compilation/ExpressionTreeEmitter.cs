using System.Linq.Expressions;
using System.Reflection;
using Elwood.Core.Abstractions;
using Elwood.Core.Syntax;
using static Elwood.Core.Compilation.CompilerHelpers;
using Expr = System.Linq.Expressions.Expression;

namespace Elwood.Core.Compilation;

/// <summary>
/// Compiles Elwood AST nodes to System.Linq.Expressions.Expression trees.
/// Returns null for unsupported nodes (caller falls back to interpreter).
/// </summary>
public sealed class ExpressionTreeEmitter
{
    private readonly ParameterExpression _input;
    private readonly ParameterExpression _factory;

    // Reflection cache for frequently called methods
    private static readonly MethodInfo _getProperty = typeof(IElwoodValue).GetMethod(nameof(IElwoodValue.GetProperty))!;
    private static readonly MethodInfo _getPropertySafe = typeof(CompilerHelpers).GetMethod(nameof(CompilerHelpers.GetPropertySafe))!;
    private static readonly MethodInfo _getStringValue = typeof(IElwoodValue).GetMethod(nameof(IElwoodValue.GetStringValue))!;
    private static readonly MethodInfo _getNumberValue = typeof(IElwoodValue).GetMethod(nameof(IElwoodValue.GetNumberValue))!;
    private static readonly MethodInfo _getBooleanValue = typeof(IElwoodValue).GetMethod(nameof(IElwoodValue.GetBooleanValue))!;
    private static readonly MethodInfo _enumerateArray = typeof(IElwoodValue).GetMethod(nameof(IElwoodValue.EnumerateArray))!;
    private static readonly MethodInfo _getArrayLength = typeof(IElwoodValue).GetMethod(nameof(IElwoodValue.GetArrayLength))!;
    private static readonly PropertyInfo _kind = typeof(IElwoodValue).GetProperty(nameof(IElwoodValue.Kind))!;

    private static readonly MethodInfo _createString = typeof(IElwoodValueFactory).GetMethod(nameof(IElwoodValueFactory.CreateString))!;
    private static readonly MethodInfo _createNumber = typeof(IElwoodValueFactory).GetMethod(nameof(IElwoodValueFactory.CreateNumber))!;
    private static readonly MethodInfo _createBool = typeof(IElwoodValueFactory).GetMethod(nameof(IElwoodValueFactory.CreateBool))!;
    private static readonly MethodInfo _createNull = typeof(IElwoodValueFactory).GetMethod(nameof(IElwoodValueFactory.CreateNull))!;
    private static readonly MethodInfo _createArray = typeof(IElwoodValueFactory).GetMethod(nameof(IElwoodValueFactory.CreateArray))!;
    private static readonly MethodInfo _createObject = typeof(IElwoodValueFactory).GetMethod(nameof(IElwoodValueFactory.CreateObject))!;

    private static readonly MethodInfo _isTruthy = typeof(CompilerHelpers).GetMethod(nameof(CompilerHelpers.IsTruthy))!;
    private static readonly MethodInfo _valuesEqual = typeof(CompilerHelpers).GetMethod(nameof(CompilerHelpers.ValuesEqual))!;
    private static readonly MethodInfo _compareValues = typeof(CompilerHelpers).GetMethod(nameof(CompilerHelpers.CompareValues))!;
    private static readonly MethodInfo _valueToString = typeof(CompilerHelpers).GetMethod(nameof(CompilerHelpers.ValueToString))!;

    // Variable scope for let bindings and lambda parameters
    private readonly Dictionary<string, Expr> _scope = new();

    public ExpressionTreeEmitter(ParameterExpression input, ParameterExpression factory)
    {
        _input = input;
        _factory = factory;
        _scope["$root"] = input;
    }

    /// <summary>
    /// Create a child emitter with an additional scope binding (for lambdas).
    /// </summary>
    private ExpressionTreeEmitter(ExpressionTreeEmitter parent, string paramName, Expr paramExpr)
    {
        _input = parent._input;
        _factory = parent._factory;
        _scope = new Dictionary<string, Expr>(parent._scope) { [paramName] = paramExpr };
    }

    /// <summary>
    /// Add a variable to the current scope (used by the compiler for let bindings).
    /// </summary>
    public void AddToScope(string name, Expr expr) => _scope[name] = expr;

    /// <summary>
    /// Try to emit an Expression tree for the given AST node.
    /// Returns null if the node type is not supported by the compiler.
    /// </summary>
    public Expr? TryEmit(ElwoodExpression node, Expr current)
    {
        return node switch
        {
            LiteralExpression lit => EmitLiteral(lit),
            PathExpression path => EmitPath(path, current),
            IdentifierExpression id => EmitIdentifier(id, current),
            Syntax.BinaryExpression bin => EmitBinary(bin, current),
            Syntax.UnaryExpression un => EmitUnary(un, current),
            IfExpression iff => EmitIf(iff, current),
            ObjectExpression obj => EmitObject(obj, current),
            ArrayExpression arr => EmitArray(arr, current),
            PipelineExpression pipe => EmitPipeline(pipe, current),
            MemberAccessExpression member => EmitMemberAccess(member, current),
            Syntax.IndexExpression idx => EmitIndex(idx, current),
            _ => null // unsupported — fall back to interpreter
        };
    }

    /// <summary>
    /// Emit an expression that evaluates to bool directly — avoids the CreateBool/IsTruthy round-trip.
    /// Used for where predicates and if conditions.
    /// </summary>
    public Expr? TryEmitAsBool(ElwoodExpression node, Expr current)
    {
        return node switch
        {
            Syntax.BinaryExpression bin => EmitBinaryAsBool(bin, current),
            Syntax.UnaryExpression { Operator: UnaryOperator.Not } un =>
                Negate(TryEmitAsBool(un.Operand, current)),
            LiteralExpression { Value: bool b } => Expr.Constant(b),
            // For anything else, fall back: emit as IElwoodValue then IsTruthy
            _ => WrapWithIsTruthy(TryEmit(node, current))
        };
    }

    private static Expr? Negate(Expr? inner)
        => inner is null ? null : Expr.Not(inner);

    private static Expr? WrapWithIsTruthy(Expr? inner)
        => inner is null ? null : Expr.Call(_isTruthy, inner);

    private Expr? EmitBinaryAsBool(Syntax.BinaryExpression bin, Expr current)
    {
        // For logical operators, stay in bool domain
        if (bin.Operator == BinaryOperator.And)
        {
            var left = TryEmitAsBool(bin.Left, current);
            var right = TryEmitAsBool(bin.Right, current);
            return left is null || right is null ? null : Expr.AndAlso(left, right);
        }
        if (bin.Operator == BinaryOperator.Or)
        {
            var left = TryEmitAsBool(bin.Left, current);
            var right = TryEmitAsBool(bin.Right, current);
            return left is null || right is null ? null : Expr.OrElse(left, right);
        }

        // For comparisons, emit IElwoodValue operands but return bool directly
        var leftVal = TryEmit(bin.Left, current);
        var rightVal = TryEmit(bin.Right, current);
        if (leftVal is null || rightVal is null) return null;

        return bin.Operator switch
        {
            BinaryOperator.Equal => Expr.Call(_valuesEqual, leftVal, rightVal),
            BinaryOperator.NotEqual => Expr.Not(Expr.Call(_valuesEqual, leftVal, rightVal)),
            BinaryOperator.GreaterThan => Expr.GreaterThan(Expr.Call(_compareValues, leftVal, rightVal), Expr.Constant(0)),
            BinaryOperator.GreaterThanOrEqual => Expr.GreaterThanOrEqual(Expr.Call(_compareValues, leftVal, rightVal), Expr.Constant(0)),
            BinaryOperator.LessThan => Expr.LessThan(Expr.Call(_compareValues, leftVal, rightVal), Expr.Constant(0)),
            BinaryOperator.LessThanOrEqual => Expr.LessThanOrEqual(Expr.Call(_compareValues, leftVal, rightVal), Expr.Constant(0)),
            // Arithmetic comparisons don't return bool — fall back
            _ => WrapWithIsTruthy(TryEmit(bin, current))
        };
    }

    private Expr EmitLiteral(LiteralExpression lit)
    {
        return lit.Value switch
        {
            string s => Expr.Call(_factory, _createString, Expr.Constant(s)),
            double d => Expr.Call(_factory, _createNumber, Expr.Constant(d)),
            bool b => Expr.Call(_factory, _createBool, Expr.Constant(b)),
            null => Expr.Call(_factory, _createNull),
            _ => Expr.Call(_factory, _createNull)
        };
    }

    private Expr? EmitPath(PathExpression path, Expr current)
    {
        Expr value = path.IsRooted ? (_scope.TryGetValue("$root", out var root) ? root : _input) : current;

        foreach (var segment in path.Segments)
        {
            Expr? next = segment switch
            {
                PropertySegment prop => Expr.Call(_getPropertySafe, value, Expr.Constant(prop.Name), _factory),
                IndexSegment { Index: null } => // [*] wildcard
                    Expr.Call(_factory, _createArray, Expr.Call(value, _enumerateArray)),
                IndexSegment idx when idx.Index is not null =>
                    EmitArrayIndex(value, idx.Index.Value),
                _ => null // Unsupported segment (slice, recursive descent, etc.)
            };
            if (next is null) return null;
            value = next;
        }

        return value;
    }

    private Expr EmitArrayIndex(Expr array, int index)
    {
        // target.EnumerateArray().ElementAtOrDefault(index) ?? factory.CreateNull()
        var enumMethod = typeof(Enumerable).GetMethod(nameof(Enumerable.ElementAtOrDefault),
            [typeof(IEnumerable<>).MakeGenericType(typeof(IElwoodValue)), typeof(int)])!;
        var elementAt = Expr.Call(enumMethod, Expr.Call(array, _enumerateArray), Expr.Constant(index));
        return Expr.Coalesce(elementAt, Expr.Call(_factory, _createNull));
    }

    private Expr? EmitIdentifier(IdentifierExpression id, Expr current)
    {
        if (_scope.TryGetValue(id.Name, out var expr))
            return expr;
        // Unknown identifier — can't compile
        return null;
    }

    private Expr? EmitBinary(Syntax.BinaryExpression bin, Expr current)
    {
        var left = TryEmit(bin.Left, current);
        var right = TryEmit(bin.Right, current);
        if (left is null || right is null) return null;

        return bin.Operator switch
        {
            BinaryOperator.Equal => Expr.Call(_factory, _createBool,
                Expr.Call(_valuesEqual, left, right)),
            BinaryOperator.NotEqual => Expr.Call(_factory, _createBool,
                Expr.Not(Expr.Call(_valuesEqual, left, right))),
            BinaryOperator.GreaterThan => Expr.Call(_factory, _createBool,
                Expr.GreaterThan(Expr.Call(_compareValues, left, right), Expr.Constant(0))),
            BinaryOperator.GreaterThanOrEqual => Expr.Call(_factory, _createBool,
                Expr.GreaterThanOrEqual(Expr.Call(_compareValues, left, right), Expr.Constant(0))),
            BinaryOperator.LessThan => Expr.Call(_factory, _createBool,
                Expr.LessThan(Expr.Call(_compareValues, left, right), Expr.Constant(0))),
            BinaryOperator.LessThanOrEqual => Expr.Call(_factory, _createBool,
                Expr.LessThanOrEqual(Expr.Call(_compareValues, left, right), Expr.Constant(0))),
            BinaryOperator.Add => Expr.Call(_factory, _createNumber,
                Expr.Add(Expr.Call(left, _getNumberValue), Expr.Call(right, _getNumberValue))),
            BinaryOperator.Subtract => Expr.Call(_factory, _createNumber,
                Expr.Subtract(Expr.Call(left, _getNumberValue), Expr.Call(right, _getNumberValue))),
            BinaryOperator.Multiply => Expr.Call(_factory, _createNumber,
                Expr.Multiply(Expr.Call(left, _getNumberValue), Expr.Call(right, _getNumberValue))),
            BinaryOperator.Divide => Expr.Call(_factory, _createNumber,
                Expr.Divide(Expr.Call(left, _getNumberValue), Expr.Call(right, _getNumberValue))),
            BinaryOperator.And => Expr.Call(_factory, _createBool,
                Expr.AndAlso(Expr.Call(_isTruthy, left), Expr.Call(_isTruthy, right))),
            BinaryOperator.Or => Expr.Call(_factory, _createBool,
                Expr.OrElse(Expr.Call(_isTruthy, left), Expr.Call(_isTruthy, right))),
            _ => null
        };
    }

    private Expr? EmitUnary(Syntax.UnaryExpression un, Expr current)
    {
        var operand = TryEmit(un.Operand, current);
        if (operand is null) return null;

        return un.Operator switch
        {
            UnaryOperator.Not => Expr.Call(_factory, _createBool,
                Expr.Not(Expr.Call(_isTruthy, operand))),
            UnaryOperator.Negate => Expr.Call(_factory, _createNumber,
                Expr.Negate(Expr.Call(operand, _getNumberValue))),
            _ => null
        };
    }

    private Expr? EmitIf(IfExpression iff, Expr current)
    {
        var cond = TryEmit(iff.Condition, current);
        var then = TryEmit(iff.ThenBranch, current);
        var @else = TryEmit(iff.ElseBranch, current);
        if (cond is null || then is null || @else is null) return null;

        return Expr.Condition(Expr.Call(_isTruthy, cond), then, @else);
    }

    private Expr? EmitObject(ObjectExpression obj, Expr current)
    {
        // Build a List<KeyValuePair<string, IElwoodValue>> then call factory.CreateObject
        var kvpType = typeof(KeyValuePair<string, IElwoodValue>);
        var kvpCtor = kvpType.GetConstructor([typeof(string), typeof(IElwoodValue)])!;
        var listType = typeof(List<KeyValuePair<string, IElwoodValue>>);
        var addMethod = listType.GetMethod("Add")!;

        var listVar = Expr.Variable(listType, "props");
        var statements = new List<Expr> { Expr.Assign(listVar, Expr.New(listType)) };

        foreach (var prop in obj.Properties)
        {
            if (prop.IsSpread)
                return null; // Can't compile spread yet
            if (prop.ComputedKey is not null)
                return null; // Computed keys not yet supported

            var keyExpr = Expr.Constant(prop.Key);
            var valueExpr = TryEmit(prop.Value, current);
            if (valueExpr is null) return null;

            statements.Add(Expr.Call(listVar, addMethod,
                Expr.New(kvpCtor, keyExpr, valueExpr)));
        }

        statements.Add(Expr.Call(_factory, _createObject,
            Expr.Convert(listVar, typeof(IEnumerable<KeyValuePair<string, IElwoodValue>>))));

        return Expr.Block(typeof(IElwoodValue), [listVar], statements);
    }

    private Expr? EmitArray(ArrayExpression arr, Expr current)
    {
        var items = arr.Items.Select(i => TryEmit(i, current)).ToList();
        if (items.Any(i => i is null)) return null;

        var listType = typeof(List<IElwoodValue>);
        var addMethod = listType.GetMethod("Add")!;
        var listVar = Expr.Variable(listType, "items");
        var statements = new List<Expr> { Expr.Assign(listVar, Expr.New(listType)) };

        foreach (var item in items)
            statements.Add(Expr.Call(listVar, addMethod, item!));

        statements.Add(Expr.Call(_factory, _createArray,
            Expr.Convert(listVar, typeof(IEnumerable<IElwoodValue>))));

        return Expr.Block(typeof(IElwoodValue), [listVar], statements);
    }

    private Expr? EmitPipeline(PipelineExpression pipe, Expr current)
    {
        var source = TryEmit(pipe.Source, current);
        if (source is null) return null;

        // TODO: fused loop optimization deferred — scope handling in where+select needs work
        // var fused = TryEmitFusedLoop(pipe.Operations, source, current);
        // if (fused is not null) return fused;

        // Fallback: chain operators
        Expr result = source;
        foreach (var op in pipe.Operations)
        {
            var next = EmitPipeOperation(op, result, current);
            if (next is null) return null;
            result = next;
        }
        return result;
    }

    /// <summary>
    /// Fuse consecutive where/select/take into a single for loop.
    /// Eliminates LINQ overhead, delegate invocations, and intermediate collections.
    /// </summary>
    private Expr? TryEmitFusedLoop(IReadOnlyList<PipeOperation> ops, Expr source, Expr outerCurrent)
    {
        // Only fuse simple patterns: where + select (optionally with take)
        // Don't fuse aggregates with predicates, or complex patterns
        if (ops.Count == 0 || ops.Count > 3) return null;
        if (!ops.All(o => o is WhereOperation or SelectOperation or SliceOperation { Kind: "take" }))
            return null;
        // Must have at least one where or select
        if (!ops.Any(o => o is WhereOperation or SelectOperation))
            return null;

        // Extract where predicates, select projection, take limit
        var wheres = new List<(string param, ElwoodExpression body)>();
        (string param, ElwoodExpression body)? selectProj = null;
        ElwoodExpression? takeLimit = null;

        foreach (var op in ops)
        {
            switch (op)
            {
                case WhereOperation w:
                    var (wp, wb) = ExtractLambda(w.Predicate);
                    if (wp is null) return null;
                    wheres.Add((wp, wb!));
                    break;
                case SelectOperation s:
                    if (selectProj is not null) return null; // Multiple selects — don't fuse
                    var (sp, sb) = ExtractLambda(s.Projection);
                    if (sp is null) return null;
                    selectProj = (sp, sb!);
                    break;
                case SliceOperation { Kind: "take" } t:
                    takeLimit = t.Count;
                    break;
                default:
                    return null;
            }
        }

        // Build the fused loop
        var resultList = Expr.Variable(typeof(List<IElwoodValue>), "result");
        var enumerator = Expr.Variable(typeof(IEnumerator<IElwoodValue>), "enumerator");
        var itemVar = Expr.Variable(typeof(IElwoodValue), "item");
        var countVar = Expr.Variable(typeof(int), "count");
        var breakLabel = Expr.Label("break");
        var continueLabel = Expr.Label("continue");

        // The lambda parameter name — use the first where's param, or select's, or "item"
        var paramName = wheres.Count > 0 ? wheres[0].param :
                       selectProj?.param ?? "item";

        var childEmitter = new ExpressionTreeEmitter(this, paramName, itemVar);

        // Build predicate: all where clauses ANDed together, as direct bool
        Expr predicate = Expr.Constant(true);
        foreach (var (_, body) in wheres)
        {
            var boolExpr = childEmitter.TryEmitAsBool(body, itemVar);
            if (boolExpr is null) return null;
            predicate = predicate.Type == typeof(bool) && predicate is ConstantExpression { Value: true }
                ? boolExpr
                : Expr.AndAlso(predicate, boolExpr);
        }

        // Build projection
        Expr? projection = null;
        if (selectProj is not null)
        {
            // Re-create child emitter with correct param name
            var selectEmitter = new ExpressionTreeEmitter(this, selectProj.Value.param, itemVar);
            projection = selectEmitter.TryEmit(selectProj.Value.body, itemVar);
            if (projection is null) return null;
        }

        // Take limit expression
        Expr? limitExpr = null;
        if (takeLimit is not null)
        {
            var limitVal = TryEmit(takeLimit, outerCurrent);
            if (limitVal is null) return null;
            limitExpr = Expr.Convert(Expr.Call(limitVal, _getNumberValue), typeof(int));
        }

        // Build the loop body
        var loopBody = new List<Expr>();

        // item = enumerator.Current
        loopBody.Add(Expr.Assign(itemVar,
            Expr.Property(enumerator, nameof(IEnumerator<IElwoodValue>.Current))));

        // if (!predicate) continue
        if (predicate is not ConstantExpression { Value: true })
        {
            loopBody.Add(Expr.IfThen(Expr.Not(predicate),
                Expr.Goto(continueLabel)));
        }

        // result.Add(projection ?? item)
        loopBody.Add(Expr.Call(resultList, typeof(List<IElwoodValue>).GetMethod("Add")!,
            projection ?? itemVar));

        // count++
        loopBody.Add(Expr.PostIncrementAssign(countVar));

        // if (count >= limit) break
        if (limitExpr is not null)
        {
            loopBody.Add(Expr.IfThen(
                Expr.GreaterThanOrEqual(countVar, limitExpr),
                Expr.Break(breakLabel)));
        }

        // The full loop
        var moveNext = typeof(System.Collections.IEnumerator).GetMethod("MoveNext")!;
        var disposeMethod = typeof(IDisposable).GetMethod("Dispose")!;

        var loop = Expr.Block(
            // while (enumerator.MoveNext()) { ...body... continue: }
            Expr.Loop(
                Expr.IfThenElse(
                    Expr.Call(enumerator, moveNext),
                    Expr.Block(
                        loopBody.Append(Expr.Label(continueLabel)).ToArray()
                    ),
                    Expr.Break(breakLabel)
                ),
                breakLabel
            )
        );

        var statements = new List<Expr>
        {
            Expr.Assign(resultList, Expr.New(typeof(List<IElwoodValue>))),
            Expr.Assign(countVar, Expr.Constant(0)),
            Expr.Assign(enumerator, Expr.Call(source, _enumerateArray,
                Type.EmptyTypes.Length == 0 ? Array.Empty<Expr>() : [])),
        };

        // Actually, EnumerateArray returns IEnumerable, need GetEnumerator
        var getEnumMethod = typeof(IEnumerable<IElwoodValue>).GetMethod("GetEnumerator")!;
        statements[2] = Expr.Assign(enumerator,
            Expr.Call(Expr.Call(source, _enumerateArray), getEnumMethod));

        // try { loop } finally { enumerator.Dispose() }
        statements.Add(Expr.TryFinally(loop, Expr.Call(enumerator, disposeMethod)));

        // Return result as array
        statements.Add(Expr.Call(_factory, _createArray,
            Expr.Convert(resultList, typeof(IEnumerable<IElwoodValue>))));

        return Expr.Block(typeof(IElwoodValue),
            [resultList, enumerator, itemVar, countVar],
            statements);
    }

    private Expr? EmitPipeOperation(PipeOperation op, Expr source, Expr outerCurrent)
    {
        return op switch
        {
            WhereOperation where => EmitWhere(where, source, outerCurrent),
            SelectOperation select => EmitSelect(select, source, outerCurrent),
            AggregateOperation agg => EmitAggregate(agg, source),
            SliceOperation slice => EmitSlice(slice, source, outerCurrent),
            _ => null
        };
    }

    private Expr? EmitWhere(WhereOperation where, Expr source, Expr outerCurrent)
    {
        var (paramName, body) = ExtractLambda(where.Predicate);
        if (paramName is null) return null;

        var itemParam = Expr.Parameter(typeof(IElwoodValue), paramName);
        var childEmitter = new ExpressionTreeEmitter(this, paramName, itemParam);

        // Use direct bool emission for predicate — avoids CreateBool/IsTruthy round-trip
        var boolExpr = childEmitter.TryEmitAsBool(body!, itemParam);
        if (boolExpr is null) return null;

        var predicateLambda = Expr.Lambda<Func<IElwoodValue, bool>>(boolExpr, itemParam);

        var whereMethod = typeof(Enumerable).GetMethods()
            .First(m => m.Name == "Where" && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(IElwoodValue));

        return Expr.Call(_factory, _createArray,
            Expr.Call(whereMethod, Expr.Call(source, _enumerateArray), predicateLambda));
    }

    private Expr? EmitSelect(SelectOperation select, Expr source, Expr outerCurrent)
    {
        var (paramName, body) = ExtractLambda(select.Projection);
        if (paramName is null) return null;

        var itemParam = Expr.Parameter(typeof(IElwoodValue), paramName);
        var childEmitter = new ExpressionTreeEmitter(this, paramName, itemParam);
        var bodyExpr = childEmitter.TryEmit(body!, itemParam);
        if (bodyExpr is null) return null;

        var selectorLambda = Expr.Lambda<Func<IElwoodValue, IElwoodValue>>(bodyExpr, itemParam);

        var selectMethod = typeof(Enumerable).GetMethods()
            .First(m => m.Name == "Select" && m.GetParameters().Length == 2 &&
                        m.GetParameters()[1].ParameterType.GetGenericArguments().Length == 2)
            .MakeGenericMethod(typeof(IElwoodValue), typeof(IElwoodValue));

        return Expr.Call(_factory, _createArray,
            Expr.Call(selectMethod, Expr.Call(source, _enumerateArray), selectorLambda));
    }

    private Expr? EmitAggregate(AggregateOperation agg, Expr source)
    {
        return agg.Name switch
        {
            "count" => Expr.Call(_factory, _createNumber,
                Expr.Convert(Expr.Call(source, _getArrayLength), typeof(double))),
            "first" => EmitFirstLast(source, first: true),
            "last" => EmitFirstLast(source, first: false),
            _ => null
        };
    }

    private Expr EmitFirstLast(Expr source, bool first)
    {
        var method = first
            ? typeof(Enumerable).GetMethods().First(m => m.Name == "FirstOrDefault" && m.GetParameters().Length == 1).MakeGenericMethod(typeof(IElwoodValue))
            : typeof(Enumerable).GetMethods().First(m => m.Name == "LastOrDefault" && m.GetParameters().Length == 1).MakeGenericMethod(typeof(IElwoodValue));

        return Expr.Coalesce(
            Expr.Call(method, Expr.Call(source, _enumerateArray)),
            Expr.Call(_factory, _createNull));
    }

    private Expr? EmitSlice(SliceOperation slice, Expr source, Expr outerCurrent)
    {
        var countExpr = TryEmit(slice.Count, outerCurrent);
        if (countExpr is null) return null;

        var countInt = Expr.Convert(Expr.Call(countExpr, _getNumberValue), typeof(int));

        var method = slice.Kind == "take"
            ? typeof(Enumerable).GetMethods().First(m => m.Name == "Take" && m.GetParameters().Length == 2 && m.GetParameters()[1].ParameterType == typeof(int)).MakeGenericMethod(typeof(IElwoodValue))
            : typeof(Enumerable).GetMethods().First(m => m.Name == "Skip" && m.GetParameters().Length == 2 && m.GetParameters()[1].ParameterType == typeof(int)).MakeGenericMethod(typeof(IElwoodValue));

        return Expr.Call(_factory, _createArray,
            Expr.Call(method, Expr.Call(source, _enumerateArray), countInt));
    }

    private Expr? EmitMemberAccess(MemberAccessExpression member, Expr current)
    {
        var target = TryEmit(member.Target, current);
        if (target is null) return null;
        return Expr.Call(_getPropertySafe, target, Expr.Constant(member.MemberName), _factory);
    }

    private Expr? EmitIndex(Syntax.IndexExpression idx, Expr current)
    {
        var target = TryEmit(idx.Target, current);
        if (target is null) return null;

        if (idx.Index is null) // [*]
            return Expr.Call(_factory, _createArray, Expr.Call(target, _enumerateArray));

        var index = TryEmit(idx.Index, current);
        if (index is null) return null;

        // String index on object → property access; numeric → array index
        // For simplicity, use a helper
        return null; // TODO: can be added later
    }

    /// <summary>
    /// Extract lambda parameter name and body from a pipe argument expression.
    /// Handles: "x => body" (explicit lambda) or implicit (use $ as parameter).
    /// </summary>
    private (string? paramName, ElwoodExpression? body) ExtractLambda(ElwoodExpression expr)
    {
        if (expr is Syntax.LambdaExpression lambda && lambda.Parameters.Count == 1)
            return (lambda.Parameters[0], lambda.Body);

        // Implicit lambda — expression uses $ as current item
        return ("$item", expr);
    }
}
