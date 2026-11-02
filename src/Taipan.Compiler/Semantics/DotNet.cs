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
    private static readonly Lazy<DotNetLibrary> RuntimeOnly = new(() => new DotNetLibrary([]));

    // Libraries by their references, since reading metadata takes a while and a test run compiles many programs.
    private static readonly ConcurrentDictionary<string, Lazy<DotNetLibrary>> WithReferences = new();

    private readonly CSharpCompilation _compilation;

    private DotNetLibrary(ImmutableArray<string> references)
    {
        References = [.. FrameworkAssemblies(), .. references];
        _compilation = CSharpCompilation.Create(
            "taipan-metadata", references: References.Select(path => MetadataReference.CreateFromFile(path)));
    }

    /// <summary>The runtime's own assemblies, which every program may use.</summary>
    public static DotNetLibrary Framework => RuntimeOnly.Value;

    /// <summary>The runtime's assemblies and those at <paramref name="references"/>, which must exist.</summary>
    public static DotNetLibrary For(ImmutableArray<string> references) => references.IsEmpty
        ? Framework
        : WithReferences.GetOrAdd(string.Join("\n", references), _ => new(() => new DotNetLibrary(references))).Value;

    /// <summary>Every assembly's path, to compile and load the program against.</summary>
    public ImmutableArray<string> References { get; }

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

    // The managed assemblies beside System.Private.CoreLib, which is the running runtime's directory.
    private static IEnumerable<string> FrameworkAssemblies()
    {
        var directory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        return Directory.EnumerateFiles(directory, "*.dll").Order(StringComparer.Ordinal).Where(IsManaged);
    }

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

    public override string ToString() => IsObject ? "object" : Symbol.ToDisplayString(Format);
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
    /// Taipan can't use it, with the type at fault in <paramref name="unsupported"/>.
    /// </summary>
    public static TaipanType? Map(ITypeSymbol type, bool result, out ITypeSymbol? unsupported)
    {
        unsupported = null;
        var mapped = MapInner(type, result, ref unsupported);
        if (mapped is null)
            return null;
        // A reference type is optional when it's annotated so, or as a result without annotations.
        var optional = type.IsReferenceType && mapped is not DotNetType { IsObject: true }
            && (type.NullableAnnotation == NullableAnnotation.Annotated || (result && type.NullableAnnotation == NullableAnnotation.None));
        return optional ? new OptionalOf(mapped) : mapped;
    }

    private static TaipanType? MapInner(ITypeSymbol type, bool result, ref ITypeSymbol? unsupported)
    {
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
            return Element(array.ElementType, result, ref unsupported);
        if (type is not INamedTypeSymbol named || named.DeclaredAccessibility != Accessibility.Public
            || named.TypeKind is not (TypeKind.Class or TypeKind.Struct or TypeKind.Interface or TypeKind.Enum)
            || named.IsRefLikeType || IsUnsupportedStruct(named))
        {
            unsupported = type;
            return null;
        }
        if (named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            return MapInner(named.TypeArguments[0], result, ref unsupported) is { } inner ? new OptionalOf(inner) : null;
        if (named.IsGenericType)
            return IsListLike(named) ? Element(named.TypeArguments[0], result, ref unsupported) : Unsupported(type, ref unsupported);
        return new DotNetType(named);
    }

    private static TaipanType? Unsupported(ITypeSymbol type, ref ITypeSymbol? unsupported)
    {
        unsupported = type;
        return null;
    }

    private static TaipanType? Element(ITypeSymbol element, bool result, ref ITypeSymbol? unsupported)
    {
        var mapped = Map(element, result, out var inner);
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
    public static DotNetFit Fit(TaipanType argument, ITypeSymbol parameter)
    {
        if (argument.ContainsError)
            return DotNetFit.Exact;
        if (Map(parameter, result: false, out _) is not { } mapped)
            return DotNetFit.None;
        if (mapped is OptionalOf optional)
        {
            if (argument is NoneType)
                return DotNetFit.Exact;
            if (argument is OptionalOf inner)
                argument = inner.Inner;
            mapped = optional.Inner;
            parameter = parameter is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
                ? nullable.TypeArguments[0]
                : parameter.WithNullableAnnotation(NullableAnnotation.NotAnnotated);
        }
        else if (argument is OptionalOf or NoneType)
        {
            return DotNetFit.None;
        }

        switch (mapped)
        {
            case DotNetType { IsObject: true }:
                return argument is PrimitiveType { Name: not "py" } or DotNetType ? DotNetFit.Object : DotNetFit.None;
            case DotNetType target when argument is DotNetType value:
                return SymbolEqualityComparer.Default.Equals(value.Symbol, target.Symbol) ? DotNetFit.Exact
                    : Derives(value.Symbol, target.Symbol) ? DotNetFit.Conversion : DotNetFit.None;
            case ListOfType when argument is ListOfType list:
                var element = parameter is IArrayTypeSymbol array ? array.ElementType : ((INamedTypeSymbol)parameter).TypeArguments[0];
                var fit = Fit(list.Element, element);
                return fit == DotNetFit.None ? fit : parameter is IArrayTypeSymbol || fit != DotNetFit.Exact ? DotNetFit.Conversion : fit;
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
