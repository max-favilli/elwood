using System.Linq.Expressions;
using Elwood.Core.Abstractions;
using Elwood.Core.Syntax;
using Expr = System.Linq.Expressions.Expression;

namespace Elwood.Core.Compilation;

/// <summary>
/// Compiles Elwood AST to .NET delegates via Expression Trees.
/// The compiled delegate takes (input, factory) and returns the result.
/// Returns null if the expression contains unsupported nodes.
/// </summary>
public sealed class ElwoodCompiler
{
    /// <summary>
    /// Try to compile an expression AST into a delegate.
    /// </summary>
    public Func<IElwoodValue, IElwoodValueFactory, IElwoodValue>? TryCompile(ElwoodExpression ast)
    {
        var input = Expr.Parameter(typeof(IElwoodValue), "input");
        var factory = Expr.Parameter(typeof(IElwoodValueFactory), "factory");

        var emitter = new ExpressionTreeEmitter(input, factory);
        var body = emitter.TryEmit(ast, input);

        if (body is null) return null;

        var lambda = Expr.Lambda<Func<IElwoodValue, IElwoodValueFactory, IElwoodValue>>(
            body, input, factory);

        return lambda.Compile();
    }

    /// <summary>
    /// Try to compile a script (let bindings + return) into a delegate.
    /// </summary>
    public Func<IElwoodValue, IElwoodValueFactory, IElwoodValue>? TryCompile(ScriptNode script)
    {
        var input = Expr.Parameter(typeof(IElwoodValue), "input");
        var factory = Expr.Parameter(typeof(IElwoodValueFactory), "factory");

        var emitter = new ExpressionTreeEmitter(input, factory);

        var variables = new List<ParameterExpression>();
        var statements = new List<Expr>();

        foreach (var binding in script.Bindings)
        {
            var valueExpr = emitter.TryEmit(binding.Value, input);
            if (valueExpr is null) return null;

            var variable = Expr.Variable(typeof(IElwoodValue), binding.Name);
            variables.Add(variable);
            statements.Add(Expr.Assign(variable, valueExpr));

            // Register the variable in scope for subsequent bindings
            emitter.AddToScope(binding.Name, variable);
        }

        if (script.ReturnExpression is null) return null;

        var returnExpr = emitter.TryEmit(script.ReturnExpression, input);
        if (returnExpr is null) return null;

        statements.Add(returnExpr);

        var block = Expr.Block(typeof(IElwoodValue), variables, statements);
        var lambda = Expr.Lambda<Func<IElwoodValue, IElwoodValueFactory, IElwoodValue>>(
            block, input, factory);

        return lambda.Compile();
    }
}
