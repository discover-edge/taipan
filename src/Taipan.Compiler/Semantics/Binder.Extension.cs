using System.Collections.Immutable;
using Taipan.Compiler.Syntax;

namespace Taipan.Compiler.Semantics;

// A program bound as a CPython extension module (SPEC §17, M25): what its module file exports.
public sealed partial class Binder
{
    private readonly bool _asExtension;

    private BoundExtension BindExtension(Module main, List<BoundFunction> bodies, BoundBlock topLevel)
    {
        Enter(main);
        // Classes and enums first, since they make the types that functions and fields may pass (SPEC §17.5).
        var exported = new HashSet<Symbol>();
        foreach (var item in main.File.Unit.Items)
        {
            switch (item)
            {
                case ClassDef classDef when !classDef.Name.Text.StartsWith('_'):
                    var symbol = main.ClassDefs.First(c => c.Syntax == classDef).Symbol;
                    if (symbol.IsProtocol || BuiltinClasses.IsException(symbol))
                        break;
                    if (!symbol.TypeParameters.IsEmpty)
                        _diagnostics.ReportNotExported(classDef.Name.Span, classDef.Name.Text, "it's generic");
                    else
                        exported.Add(symbol);
                    break;
                case EnumDef enumDef when !enumDef.Name.Text.StartsWith('_'):
                    var enumSymbol = main.EnumDefs.First(e => e.Syntax == enumDef).Symbol;
                    if (!enumSymbol.TypeParameters.IsEmpty)
                        _diagnostics.ReportNotExported(enumDef.Name.Span, enumDef.Name.Text, "it's generic");
                    else
                        exported.Add(enumSymbol);
                    break;
            }
        }
        _exportedToPython = exported;

        var functions = new List<FunctionSymbol>();
        var constants = new List<BoundConstant>();
        var exceptions = new List<ClassSymbol>();
        var classes = new List<ExtensionClass>();
        var enums = new List<EnumSymbol>();
        foreach (var item in main.File.Unit.Items)
        {
            switch (item)
            {
                case FuncDef funcDef when !funcDef.Name.Text.StartsWith('_'):
                    var function = main.Functions.First(f => f.Syntax == funcDef).Symbol;
                    if (WhyNotExported(function) is { } reason)
                        _diagnostics.ReportNotExported(funcDef.Name.Span, funcDef.Name.Text, reason);
                    else
                        functions.Add(function);
                    break;
                case ConstDef constDef when !constDef.Name.Text.StartsWith('_'):
                    var constant = main.Constants.First(c => c.Symbol.Name == constDef.Name.Text);
                    if (Unconvertible(constant.Symbol.Type, isResult: false) is { } unconvertible)
                        _diagnostics.ReportNotExported(constDef.Name.Span, constDef.Name.Text, $"'{unconvertible}' can't be passed to Python");
                    else
                        constants.Add(constant);
                    break;
                case ClassDef classDef when !classDef.Name.Text.StartsWith('_'):
                    var symbol = main.ClassDefs.First(c => c.Syntax == classDef).Symbol;
                    if (!symbol.IsProtocol && BuiltinClasses.IsException(symbol))
                        exceptions.Add(symbol);
                    else if (exported.Contains(symbol))
                        classes.Add(ExportClass(classDef, symbol, main));
                    break;
                case EnumDef enumDef when !enumDef.Name.Text.StartsWith('_'):
                    var enumSymbol = main.EnumDefs.First(e => e.Syntax == enumDef).Symbol;
                    if (!exported.Contains(enumSymbol))
                        break;
                    if (enumSymbol.Variants.SelectMany(v => v.Fields).Select(f => Unconvertible(f.Type, isResult: false)).FirstOrDefault(t => t is not null) is { } fieldType)
                        _diagnostics.ReportNotExported(enumDef.Name.Span, enumDef.Name.Text, $"'{fieldType}' can't be passed to Python");
                    else
                        enums.Add(enumSymbol);
                    break;
            }
        }
        // A Python import is already TP0233, and its uses aren't reported again.
        if (main.PythonImports.Count == 0)
            CheckPythonObjects(bodies, topLevel);
        // Each class after its bases, so its Python base exists when it's created.
        var ordered = exceptions.OrderBy(Depth).ToImmutableArray();
        return new BoundExtension(main.File.Name, [.. functions], [.. constants], ordered)
        {
            Classes = [.. classes.OrderBy(c => Depth(c.Symbol))],
            Enums = [.. enums],
        };
    }

    // The module's classes and enums that are Python types, which signatures may then use (SPEC §17.5).
    private HashSet<Symbol> _exportedToPython = [];

    private string? WhyNotExported(FunctionSymbol function)
    {
        var unconvertibleType = function.Parameters.Select(p => Unconvertible(p.Type, isResult: false, isParameter: true))
            .Append(Unconvertible(function.ReturnType, isResult: true)).FirstOrDefault(t => t is not null);
        return !function.TypeParameters.IsEmpty ? "it's generic"
            : function.IsAsync ? "it's async"
            : unconvertibleType is not null ? $"'{unconvertibleType}' can't be passed to Python"
            : null;
    }

    /// <summary>A class as a Python type: the fields, properties and methods that convert are on it, and the methods that don't are TP0302 (SPEC §17.5).</summary>
    private ExtensionClass ExportClass(ClassDef syntax, ClassSymbol symbol, Module main)
    {
        var fields = symbol.Fields.Where(f => !f.Name.StartsWith('_') && Unconvertible(f.Type, isResult: false) is null).ToImmutableArray();
        var properties = ImmutableArray.CreateBuilder<FunctionSymbol>();
        var methods = ImmutableArray.CreateBuilder<FunctionSymbol>();
        foreach (var method in syntax.Methods)
        {
            if (method.Name.Text.StartsWith('_') || symbol.Methods.GetValueOrDefault(method.Name.Text) is not { } member || member.IsClassLevel || member.SetterOf is not null)
                continue;
            if (member.Kind == MemberKind.Property)
            {
                if (Unconvertible(member.ReturnType, isResult: false) is null)
                    properties.Add(member);
                continue;
            }
            if (WhyNotExported(member) is { } reason)
                _diagnostics.ReportNotExported(method.Name.Span, method.Name.Text, reason);
            else
                methods.Add(member);
        }
        return new ExtensionClass(symbol, fields, properties.ToImmutable(), methods.ToImmutable());
    }

    /// <summary>
    /// Inside an extension module, a Python object may only be passed along and displayed (SPEC
    /// §17.6): any other operation on a <c>py</c> value is TP0233.
    /// </summary>
    private void CheckPythonObjects(List<BoundFunction> bodies, BoundBlock topLevel)
    {
        void Expression(BoundExpr expr)
        {
            var operation = expr switch
            {
                BoundPyGetAttr or BoundPyGetItem or BoundPyCall or BoundPyIsInstance or BoundToPy => true,
                BoundCast cast => cast.Value.Type == TaipanType.Py,
                BoundBinary binary => binary.Left.Type == TaipanType.Py || binary.Right.Type == TaipanType.Py,
                BoundFieldAccess { Field.Name: "value", Target.Type: ClassType { Symbol: var owner } } => owner == BuiltinClasses.PythonError,
                _ => false,
            };
            if (operation)
                _diagnostics.ReportPythonOperationInExtension(expr.Span);
            else
                foreach (var child in BoundTree.Children(expr))
                    Expression(child);
        }
        void Statement(BoundStmt stmt)
        {
            if (stmt is BoundPySetAttr or BoundPySetItem || stmt is BoundForEach { Iterable.Type: PrimitiveType { Name: "py" } })
                _diagnostics.ReportPythonOperationInExtension(stmt.Span);
            foreach (var expr in BoundTree.Expressions(stmt))
                Expression(expr);
            foreach (var inner in BoundTree.Statements(stmt))
                Statement(inner);
        }
        foreach (var body in bodies)
            Statement(body.Body);
        Statement(topLevel);
    }

    // Set for --library --native (SPEC §15.6, M45).
    private bool _asNativeLibrary;

    /// <summary>
    /// The functions a native library exports (SPEC §15.6): the main file's own public ones whose
    /// parameters and result C can pass. Others are warning TP0302.
    /// </summary>
    private BoundNativeLibrary BindNativeLibrary(Module main)
    {
        var functions = new List<FunctionSymbol>();
        foreach (var (syntax, function) in main.Functions.Where(f => !f.Syntax.Name.Text.StartsWith('_')))
        {
            var unpassable = function.Parameters.Select(p => p.Type).Append(function.ReturnType)
                .FirstOrDefault(t => !t.ContainsError && !(t == TaipanType.Int || t == TaipanType.Float || t == TaipanType.Bool || t == TaipanType.Str
                    || t is NothingType && t == function.ReturnType));
            var reason = !function.TypeParameters.IsEmpty ? "it's generic"
                : function.IsAsync ? "it's async"
                : function.Kinds.Any(k => k is ParamKind.VarArgs or ParamKind.VarKeywords) ? "it has '*args' or '**kwargs'"
                : function.Kinds.Contains(ParamKind.KeywordOnly) ? "it has keyword-only parameters"
                : unpassable is not null ? $"'{unpassable}' can't be passed to C"
                : null;
            if (reason is null)
                functions.Add(function);
            else
                _diagnostics.ReportNotExported(syntax.Name.Span, syntax.Name.Text, reason);
        }
        return new BoundNativeLibrary(main.File.Name, [.. functions]);
    }

    private static int Depth(ClassSymbol symbol) => symbol.Base is { } baseClass ? Depth(baseClass) + 1 : 0;

    /// <summary>
    /// The part of a type that can't be passed to or from Python (SPEC §17.2), or null if it's
    /// convertible (§10.3): no result, for a result, but not <c>py</c>.
    /// </summary>
    private TaipanType? Unconvertible(TaipanType type, bool isResult, bool isParameter = false) => type switch
    {
        // An error is already reported.
        { ContainsError: true } => null,
        NothingType when isResult => null,
        PrimitiveType { Name: "int" or "float" or "bool" or "str" } => null,
        // Since M45, Python objects themselves, the module's Python types, and Python callables as parameters (SPEC §17.5, §17.6).
        PrimitiveType { Name: "py" } when _asExtension => null,
        ClassType { Symbol: var symbol } when _exportedToPython.Contains(symbol) => null,
        EnumType { Symbol: var symbol } when _exportedToPython.Contains(symbol) => null,
        FunctionType function when isParameter => function.Parameters.Select(p => Unconvertible(p, isResult: false))
            .Append(Unconvertible(function.Result, isResult: true)).FirstOrDefault(t => t is not null) is { } inner ? inner : null,
        OptionalOf optional => Unconvertible(optional.Inner, isResult: false, isParameter),
        ListOfType list => Unconvertible(list.Element, isResult: false),
        DictOfType dict => Unconvertible(dict.Key, isResult: false) ?? Unconvertible(dict.Value, isResult: false),
        TupleOfType tuple => tuple.Elements.Select(e => Unconvertible(e, isResult: false)).FirstOrDefault(e => e is not null),
        _ => type,
    };
}
