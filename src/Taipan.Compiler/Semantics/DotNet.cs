using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Taipan.Compiler.Semantics;

/// <summary>
/// The .NET assemblies a program can use (SPEC §13.2), read through Roslyn: the running .NET
/// runtime's own, plus any the program references. Types and members come from their metadata.
/// </summary>
public sealed class DotNetLibrary
{
    private static readonly Lazy<DotNetLibrary> RuntimeOnly = new(() => new DotNetLibrary([], RuntimeDirectory));

    // Libraries by their references, since reading metadata takes a while and a test run compiles many programs.
    private static readonly ConcurrentDictionary<string, Lazy<DotNetLibrary>> WithReferences = new();

    private readonly CSharpCompilation _compilation;

    private DotNetLibrary(ImmutableArray<string> references, string? frameworkDirectory)
    {
        References = [.. frameworkDirectory is null ? [] : FrameworkAssemblies(frameworkDirectory), .. references];
        _compilation = CSharpCompilation.Create(
            "taipan-metadata", references: References.Select(path => MetadataReference.CreateFromFile(path)));
    }

    /// <summary>The runtime's own assemblies, which every program may use.</summary>
    public static DotNetLibrary Framework => RuntimeOnly.Value;

    /// <summary>
    /// The framework's assemblies and those at <paramref name="references"/>, which must exist. The
    /// framework is the running runtime's, or with <paramref name="frameworkDirectory"/> the SDK's
    /// reference assemblies, which a library compiles against (SPEC §14.4).
    /// </summary>
    public static DotNetLibrary For(ImmutableArray<string> references, string? frameworkDirectory = null) =>
        references.IsEmpty && frameworkDirectory is null
            ? Framework
            : WithReferences.GetOrAdd(
                string.Join("\n", [frameworkDirectory ?? RuntimeDirectory, .. references]),
                _ => new(() => new DotNetLibrary(references, frameworkDirectory ?? RuntimeDirectory))).Value;

    /// <summary>Exactly these assemblies, framework included, as MSBuild resolves them for an SDK project (SPEC §18.2).</summary>
    public static DotNetLibrary Exactly(ImmutableArray<string> references) =>
        WithReferences.GetOrAdd("exactly\n" + string.Join("\n", references), _ => new(() => new DotNetLibrary(references, null) { IsExact = true })).Value;

    /// <summary>Whether the program compiles against these assemblies only, even if it uses no .NET type.</summary>
    public bool IsExact { get; private init; }

    private static string RuntimeDirectory => Path.GetDirectoryName(typeof(object).Assembly.Location)!;

    /// <summary>
    /// The .NET SDK's reference assemblies for the running runtime's version, which libraries
    /// compile against so other projects can reference them (SPEC §14.4); null without the SDK.
    /// The runtime is <c>&lt;root&gt;/shared/Microsoft.NETCore.App/&lt;version&gt;</c>, and the
    /// references <c>&lt;root&gt;/packs/Microsoft.NETCore.App.Ref/&lt;version&gt;/ref/net&lt;major.minor&gt;</c>.
    /// </summary>
    public static string? ReferencePackDirectory()
    {
        var root = Directory.GetParent(RuntimeDirectory)?.Parent?.Parent?.FullName;
        var packs = root is null ? null : Path.Combine(root, "packs", "Microsoft.NETCore.App.Ref");
        if (packs is null || !Directory.Exists(packs))
            return null;
        var version = Environment.Version;
        var framework = $"net{version.Major}.{version.Minor}";
        return Directory.EnumerateDirectories(packs)
            .Where(d => Version.TryParse(Path.GetFileName(d).Split('-')[0], out var v) && v.Major == version.Major && v.Minor == version.Minor)
            .OrderByDescending(d => Version.Parse(Path.GetFileName(d).Split('-')[0]))
            .Select(d => Path.Combine(d, "ref", framework))
            .FirstOrDefault(Directory.Exists);
    }

    /// <summary>Every assembly's path, to compile and load the program against.</summary>
    public ImmutableArray<string> References { get; }

    /// <summary>A .NET type built in to C#, such as <c>long</c>, to check constraints against (SPEC §13.8).</summary>
    public INamedTypeSymbol Special(SpecialType type) => _compilation.GetSpecialType(type);

    /// <summary>A namespace, by its dotted name (<c>System.Text</c>); null if no assembly defines it.</summary>
    public INamespaceSymbol? Namespace(string dotted)
    {
        var current = _compilation.GlobalNamespace;
        foreach (var part in dotted.Split('.'))
        {
            current = current.GetNamespaceMembers().FirstOrDefault(n => n.Name == part);
            if (current is null)
                return null;
        }
        return current;
    }

    // The managed assemblies in a framework directory.
    private static IEnumerable<string> FrameworkAssemblies(string directory) =>
        Directory.EnumerateFiles(directory, "*.dll").Order(StringComparer.Ordinal).Where(IsManaged);

    private static bool IsManaged(string path)
    {
        try
        {
            using var reader = new PEReader(File.OpenRead(path));
            return reader.HasMetadata;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
    }
}

/// <summary>A public, non-generic .NET class, struct, interface or enum, as an opaque type (SPEC §13.3).</summary>
public sealed record DotNetType(INamedTypeSymbol Symbol) : TaipanType
{
    private static readonly SymbolDisplayFormat Format = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes);

    public bool IsObject => Symbol.SpecialType == SpecialType.System_Object;

    public bool Equals(DotNetType? other) => other is not null && SymbolEqualityComparer.Default.Equals(other.Symbol, Symbol);

    public override int GetHashCode() => SymbolEqualityComparer.Default.GetHashCode(Symbol);

    protected override string Describe() => IsObject ? "object" : Symbol.ToDisplayString(Format);
}

/// <summary>A generic method's type parameters, as Taipan types (SPEC §13.8).</summary>
public sealed class TypeArguments(IEnumerable<KeyValuePair<ITypeParameterSymbol, TaipanType>> pairs)
    : Dictionary<ITypeParameterSymbol, TaipanType>(pairs, SymbolEqualityComparer.Default)
{
}

/// <summary>How well an argument fits a .NET parameter, best last (SPEC §13.5).</summary>
public enum DotNetFit { None, Object, Float, Conversion, Exact }

/// <summary>The mapping between .NET and Taipan types at the boundary (SPEC §13.3).</summary>
public static class DotNetTypes
{
    /// <summary>A type as C# writes it, for messages: <c>Task&lt;string&gt;</c>, <c>Int32</c>.</summary>
    public static string Display(ITypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

    /// <summary>
    /// A .NET type's Taipan type, as a result (<paramref name="result"/>) or as a parameter. Null if
    /// Taipan can't use it, with the type at fault in <paramref name="unsupported"/>. A generic
    /// method's type parameters are the Taipan types in <paramref name="typeArguments"/> (SPEC §13.8):
    /// what was inferred, or type parameters standing for them while it's inferred.
    /// </summary>
    public static TaipanType? Map(ITypeSymbol type, bool result, out ITypeSymbol? unsupported, TypeArguments? typeArguments = null)
    {
        unsupported = null;
        var mapped = MapInner(type, result, ref unsupported, typeArguments);
        if (mapped is null)
            return null;
        // A reference type is optional when it's annotated so, or as a result without annotations.
        var optional = type.IsReferenceType && mapped is not DotNetType { IsObject: true }
            && (type.NullableAnnotation == NullableAnnotation.Annotated || (result && type.NullableAnnotation == NullableAnnotation.None));
        return optional ? new OptionalOf(mapped) : mapped;
    }

    private static TaipanType? MapInner(ITypeSymbol type, bool result, ref ITypeSymbol? unsupported, TypeArguments? typeArguments)
    {
        if (type is ITypeParameterSymbol parameter)
        {
            if (typeArguments?.GetValueOrDefault(parameter) is { } argument)
                return argument;
            unsupported = type;
            return null;
        }
        switch (type.SpecialType)
        {
            case SpecialType.System_Int64 or SpecialType.System_Int32 or SpecialType.System_Int16 or SpecialType.System_SByte
                or SpecialType.System_Byte or SpecialType.System_UInt16 or SpecialType.System_UInt32 or SpecialType.System_UInt64:
                return TaipanType.Int;
            case SpecialType.System_Double or SpecialType.System_Single:
                return TaipanType.Float;
            case SpecialType.System_Boolean:
                return TaipanType.Bool;
            case SpecialType.System_String or SpecialType.System_Char:
                return TaipanType.Str;
            case SpecialType.System_Void:
                return TaipanType.Nothing;
            case SpecialType.System_Object when !result:
                return new DotNetType((INamedTypeSymbol)type);
            case SpecialType.System_Object or SpecialType.System_Decimal or SpecialType.System_IntPtr or SpecialType.System_UIntPtr:
                unsupported = type;
                return null;
        }

        if (type is IArrayTypeSymbol { Rank: 1 } array)
            return Element(array.ElementType, result, ref unsupported, typeArguments);
        if (type is INamedTypeSymbol { TypeKind: TypeKind.Delegate } callable)
            return Delegate(callable, result, ref unsupported, typeArguments);
        if (type is not INamedTypeSymbol named || named.DeclaredAccessibility != Accessibility.Public
            || named.TypeKind is not (TypeKind.Class or TypeKind.Struct or TypeKind.Interface or TypeKind.Enum)
            || named.IsRefLikeType || IsUnsupportedStruct(named))
        {
            unsupported = type;
            return null;
        }
        if (named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            return MapInner(named.TypeArguments[0], result, ref unsupported, typeArguments) is { } inner ? new OptionalOf(inner) : null;
        if (IsTask(named))
        {
            if (named.TypeArguments.IsEmpty)
                return new AwaitableOfType(TaipanType.Nothing);
            var awaited = Map(named.TypeArguments[0], result, out var inner, typeArguments);
            unsupported ??= inner;
            return awaited is null or NothingType or DotNetType { IsObject: true } ? Unsupported(named.TypeArguments[0], ref unsupported) : new AwaitableOfType(awaited);
        }
        if (named.IsGenericType)
        {
            if (IsListLike(named) || result && IsOrdered(named))
                return Element(named.TypeArguments[0], result, ref unsupported, typeArguments);
            if (IsDictionary(named))
            {
                var key = Map(named.TypeArguments[0], result, out var keyFault, typeArguments);
                var value = Map(named.TypeArguments[1], result, out var valueFault, typeArguments);
                unsupported ??= keyFault ?? valueFault;
                return key is null or NothingType or DotNetType { IsObject: true } || !DictOfType.IsValidKey(key) && key is not TypeParameterType ? Unsupported(named.TypeArguments[0], ref unsupported)
                    : value is null or NothingType or DotNetType { IsObject: true } ? Unsupported(named.TypeArguments[1], ref unsupported)
                    : new DictOfType(key, value);
            }
            return Unsupported(type, ref unsupported);
        }
        return new DotNetType(named);
    }

    /// <summary>
    /// A delegate type as a function type (SPEC §13.9). A Taipan function given to .NET receives its
    /// parameters as results and returns its result as an argument; a delegate .NET gives is the reverse.
    /// </summary>
    private static TaipanType? Delegate(INamedTypeSymbol type, bool result, ref ITypeSymbol? unsupported, TypeArguments? typeArguments)
    {
        if (type.DelegateInvokeMethod is not { } invoke || type.DeclaredAccessibility != Accessibility.Public || invoke.ReturnsByRef || invoke.ReturnsByRefReadonly)
            return Unsupported(type, ref unsupported);
        var parameters = ImmutableArray.CreateBuilder<TaipanType>();
        foreach (var parameter in invoke.Parameters)
        {
            var mapped = parameter.RefKind == RefKind.None ? Map(TypeOf(parameter), !result, out var fault, typeArguments) : null;
            if (mapped is null or NothingType)
                return Unsupported(parameter.Type, ref unsupported);
            parameters.Add(mapped);
        }
        var returned = Map(ResultOf(invoke), result, out var resultFault, typeArguments);
        unsupported ??= resultFault;
        return returned is null ? Unsupported(invoke.ReturnType, ref unsupported) : new FunctionType(parameters.ToImmutable(), returned);
    }

    private static TaipanType? Unsupported(ITypeSymbol type, ref ITypeSymbol? unsupported)
    {
        unsupported = type;
        return null;
    }

    private static TaipanType? Element(ITypeSymbol element, bool result, ref ITypeSymbol? unsupported, TypeArguments? typeArguments)
    {
        var mapped = Map(element, result, out var inner, typeArguments);
        unsupported ??= inner;
        return mapped is null or NothingType or DotNetType { IsObject: true } ? Unsupported(element, ref unsupported) : new ListOfType(mapped);
    }

    // Int128 and friends have no Taipan form, though they're ordinary structs.
    private static bool IsUnsupportedStruct(INamedTypeSymbol type) =>
        type.ContainingNamespace?.ToDisplayString() is "System" or "System.Numerics"
        && type.Name is "Int128" or "UInt128" or "Half" or "BigInteger" or "Complex";

    private static readonly HashSet<string> ListLike =
        ["List", "IEnumerable", "ICollection", "IList", "IReadOnlyCollection", "IReadOnlyList"];

    /// <summary><c>List&lt;T&gt;</c> or one of the interfaces a Taipan list is given as (SPEC §13.3).</summary>
    public static bool IsListLike(INamedTypeSymbol type) =>
        type.Arity == 1 && ListLike.Contains(type.Name) && type.ContainingNamespace?.ToDisplayString() == "System.Collections.Generic";

    /// <summary><c>IOrderedEnumerable&lt;T&gt;</c>, a list as a result (SPEC §13.3, M33).</summary>
    public static bool IsOrdered(INamedTypeSymbol type) =>
        type.Arity == 1 && type.Name == "IOrderedEnumerable" && type.ContainingNamespace?.ToDisplayString() == "System.Linq";

    private static readonly HashSet<string> DictionaryLike = ["Dictionary", "IDictionary", "IReadOnlyDictionary"];

    /// <summary><c>Dictionary&lt;K, V&gt;</c> or an interface a Taipan dict is given as (SPEC §13.3, M33).</summary>
    public static bool IsDictionary(INamedTypeSymbol type) =>
        type.Arity == 2 && DictionaryLike.Contains(type.Name) && type.ContainingNamespace?.ToDisplayString() == "System.Collections.Generic";

    /// <summary><c>Task</c>, <c>Task&lt;T&gt;</c>, <c>ValueTask</c> or <c>ValueTask&lt;T&gt;</c>: an awaitable (SPEC §13.11, M33).</summary>
    public static bool IsTask(INamedTypeSymbol type) =>
        type.Arity <= 1 && type.Name is "Task" or "ValueTask" && type.ContainingNamespace?.ToDisplayString() == "System.Threading.Tasks";

    /// <summary>A parameter's type with its own nullability, which Roslyn keeps on the parameter.</summary>
    public static ITypeSymbol TypeOf(IParameterSymbol parameter) => parameter.Type.WithNullableAnnotation(parameter.NullableAnnotation);

    public static ITypeSymbol ResultOf(IMethodSymbol method) => method.ReturnType.WithNullableAnnotation(method.ReturnNullableAnnotation);

    public static ITypeSymbol TypeOf(ISymbol member) => member switch
    {
        IPropertySymbol property => property.Type.WithNullableAnnotation(property.NullableAnnotation),
        IFieldSymbol field => field.Type.WithNullableAnnotation(field.NullableAnnotation),
        _ => throw new InvalidOperationException($"no type for {member.Kind}"),
    };

    /// <summary>Whether an integer type is narrower than <c>long</c>, so an argument needs a checked conversion.</summary>
    public static bool IsNarrowInteger(ITypeSymbol type) => type.SpecialType is SpecialType.System_Int32 or SpecialType.System_Int16
        or SpecialType.System_SByte or SpecialType.System_Byte or SpecialType.System_UInt16 or SpecialType.System_UInt32 or SpecialType.System_UInt64;

    /// <summary>
    /// How an argument of Taipan type <paramref name="argument"/> fits a parameter of .NET type
    /// <paramref name="parameter"/> (SPEC §13.5).
    /// </summary>
    public static DotNetFit Fit(TaipanType argument, ITypeSymbol parameter, TypeArguments? typeArguments = null)
    {
        if (argument.ContainsError)
            return DotNetFit.Exact;
        if (Map(parameter, result: false, out _, typeArguments) is not { } mapped)
            return DotNetFit.None;
        // A type parameter stands for the argument's own type (SPEC §13.8).
        if (parameter is ITypeParameterSymbol)
            return argument == mapped ? DotNetFit.Exact : DotNetFit.None;
        // A value that isn't optional takes a Nullable<T> by conversion, as in C#; a nullable reference is only annotated (SPEC §13.5).
        var widened = false;
        if (mapped is OptionalOf optional)
        {
            if (argument is NoneType)
                return DotNetFit.Exact;
            if (argument is OptionalOf inner)
                argument = inner.Inner;
            else
                widened = parameter is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T };
            mapped = optional.Inner;
            parameter = parameter is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
                ? nullable.TypeArguments[0]
                : parameter.WithNullableAnnotation(NullableAnnotation.NotAnnotated);
        }
        else if (argument is OptionalOf or NoneType)
        {
            return DotNetFit.None;
        }

        var fit = FitValue(argument, mapped, parameter, typeArguments);
        return widened && fit == DotNetFit.Exact ? DotNetFit.Conversion : fit;
    }

    private static DotNetFit FitValue(TaipanType argument, TaipanType mapped, ITypeSymbol parameter, TypeArguments? typeArguments)
    {
        switch (mapped)
        {
            case DotNetType { IsObject: true }:
                return argument is PrimitiveType { Name: not "py" } or DotNetType ? DotNetFit.Object : DotNetFit.None;
            case DotNetType target when argument is DotNetType value:
                return SymbolEqualityComparer.Default.Equals(value.Symbol, target.Symbol) ? DotNetFit.Exact
                    : Derives(value.Symbol, target.Symbol) ? DotNetFit.Conversion : DotNetFit.None;
            case ListOfType when argument is ListOfType list:
                var element = parameter is IArrayTypeSymbol array ? array.ElementType : ((INamedTypeSymbol)parameter).TypeArguments[0];
                // A list is passed as is only if its elements are already the parameter's.
                var fit = Fit(list.Element, element, typeArguments);
                return fit == DotNetFit.None ? fit
                    : parameter is IArrayTypeSymbol || fit != DotNetFit.Exact || ((ListOfType)mapped).Element != list.Element ? DotNetFit.Conversion
                    : fit;
            // A Dictionary is always a copy; an interface takes the dict itself if nothing converts (SPEC §13.3).
            case DictOfType when argument is DictOfType dict && parameter is INamedTypeSymbol dictionary:
                var keyFit = Fit(dict.Key, dictionary.TypeArguments[0], typeArguments);
                var valueFit = Fit(dict.Value, dictionary.TypeArguments[1], typeArguments);
                return keyFit == DotNetFit.None || valueFit == DotNetFit.None ? DotNetFit.None
                    : keyFit == DotNetFit.Exact && valueFit == DotNetFit.Exact && dictionary.Name != "Dictionary" ? DotNetFit.Exact
                    : DotNetFit.Conversion;
            case AwaitableOfType when argument is AwaitableOfType:
                return argument != mapped ? DotNetFit.None : parameter.Name == "Task" ? DotNetFit.Exact : DotNetFit.Conversion;
        }

        if (argument == TaipanType.Int)
        {
            return parameter.SpecialType switch
            {
                SpecialType.System_Int64 => DotNetFit.Exact,
                SpecialType.System_Double or SpecialType.System_Single => DotNetFit.Float,
                _ when IsNarrowInteger(parameter) => DotNetFit.Conversion,
                _ => DotNetFit.None,
            };
        }
        if (argument == TaipanType.Float)
            return parameter.SpecialType switch { SpecialType.System_Double => DotNetFit.Exact, SpecialType.System_Single => DotNetFit.Float, _ => DotNetFit.None };
        if (argument == TaipanType.Str)
            return parameter.SpecialType switch { SpecialType.System_String => DotNetFit.Exact, SpecialType.System_Char => DotNetFit.Conversion, _ => DotNetFit.None };
        return argument == mapped ? DotNetFit.Exact : DotNetFit.None;
    }

    /// <summary>Whether a .NET type derives from, or implements, another.</summary>
    public static bool Derives(INamedTypeSymbol type, INamedTypeSymbol target)
    {
        if (target.SpecialType == SpecialType.System_Object)
            return true;
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, target))
                return true;
        }
        return type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, target));
    }
}
