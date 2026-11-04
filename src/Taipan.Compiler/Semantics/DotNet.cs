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

    /// <summary>A type by its metadata name, such as <c>System.Collections.Generic.List`1</c>.</summary>
    public INamedTypeSymbol Named(string metadataName) =>
        _compilation.GetTypeByMetadataName(metadataName) ?? throw new InvalidOperationException($"no type {metadataName}");

    /// <summary>A .NET type built in to C#, such as <c>long</c>, to check constraints against (SPEC §13.8).</summary>
    public INamedTypeSymbol Special(SpecialType type) => _compilation.GetSpecialType(type);

    /// <summary>The namespaces inside one, or inside the global namespace for <c>""</c>, by name (SPEC §16.4, M44).</summary>
    public IEnumerable<string> NamespacesIn(string dotted) =>
        ((dotted.Length == 0 ? _compilation.GlobalNamespace : Namespace(dotted))?.GetNamespaceMembers() ?? []).Select(n => n.Name).Order(StringComparer.Ordinal);

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

/// <summary>
/// A public .NET class, struct, interface or enum, as an opaque type (SPEC §13.3). A generic one
/// (M43, §13.13) is its definition with Taipan type arguments, which stand for their .NET forms:
/// <c>HashSet[int]</c> is <c>HashSet&lt;long&gt;</c>. The arguments are those of the types containing
/// it first, then its own, as Roslyn's type parameters are ordered.
/// </summary>
public sealed record DotNetType : TaipanType
{
    private static readonly SymbolDisplayFormat Format = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes);

    private static readonly SymbolDisplayFormat NameOnly = new(genericsOptions: SymbolDisplayGenericsOptions.None);

    public DotNetType(INamedTypeSymbol symbol, ImmutableArray<TaipanType> typeArguments = default)
    {
        Symbol = symbol.OriginalDefinition;
        TypeArguments = typeArguments.IsDefault ? [] : typeArguments;
    }

    /// <summary>The type's definition.</summary>
    public INamedTypeSymbol Symbol { get; }

    public ImmutableArray<TaipanType> TypeArguments { get; }

    public bool IsGeneric => !TypeArguments.IsEmpty;

    /// <summary>Its type parameters, and those of the types containing it, as Taipan types.</summary>
    public TypeArguments? Map => IsGeneric
        ? new TypeArguments(DotNetTypes.AllTypeParameters(Symbol).Zip(TypeArguments, KeyValuePair.Create))
        : null;

    public bool Equals(DotNetType? other) =>
        other is not null && SymbolEqualityComparer.Default.Equals(other.Symbol, Symbol) && other.TypeArguments.SequenceEqual(TypeArguments);

    public override int GetHashCode() => SymbolEqualityComparer.Default.GetHashCode(Symbol);

    protected override string Describe()
    {
        if (!IsGeneric)
            return Symbol.ToDisplayString(Format);
        // HashSet[str], or List[int].Enumerator for a type nested in a generic one.
        var used = 0;
        string Part(INamedTypeSymbol type)
        {
            var outer = type.ContainingType is { } containing ? Part(containing) + "." : "";
            var arguments = TypeArguments.Skip(used).Take(type.TypeParameters.Length).ToList();
            used += type.TypeParameters.Length;
            return outer + type.ToDisplayString(NameOnly) + (arguments.Count == 0 ? "" : $"[{string.Join(", ", arguments)}]");
        }
        return Part(Symbol);
    }
}

/// <summary>A generic method's type parameters, as Taipan types (SPEC §13.8).</summary>
public sealed class TypeArguments(IEnumerable<KeyValuePair<ITypeParameterSymbol, TaipanType>> pairs)
    : Dictionary<ITypeParameterSymbol, TaipanType>(pairs, SymbolEqualityComparer.Default)
{
    /// <summary>Both maps' type parameters, such as a generic type's and its generic method's; null if neither has any.</summary>
    public static TypeArguments? Merge(TypeArguments? a, TypeArguments? b) =>
        a is null ? b : b is null ? a : new TypeArguments(a.Concat(b));
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
        // A reference type is optional when it's annotated so, or as a result without annotations; an object may hold None already.
        var optional = type.IsReferenceType && mapped is not ObjectType
            && (type.NullableAnnotation == NullableAnnotation.Annotated || (result && type.NullableAnnotation == NullableAnnotation.None));
        // An unconstrained T? is optional when T stands for a reference type, as C# reads it (SPEC §13.16).
        if (type is ITypeParameterSymbol && type.NullableAnnotation == NullableAnnotation.Annotated && IsReferenceForm(mapped))
            optional = true;
        return optional && mapped is not OptionalOf ? new OptionalOf(mapped) : mapped;
    }

    /// <summary>Whether a Taipan type's .NET form (SPEC §8) is a reference type.</summary>
    public static bool IsReferenceForm(TaipanType type) => type switch
    {
        PrimitiveType { Name: "int" or "float" or "bool" } or TupleOfType or OptionalOf or NothingType or ErrorType or TypeParameterType => false,
        DotNetType net => !net.Symbol.IsValueType,
        _ => true,
    };

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
            case SpecialType.System_Object:
                return TaipanType.Object;
            case SpecialType.System_IntPtr or SpecialType.System_UIntPtr:
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
            return awaited is null or NothingType ? Unsupported(named.TypeArguments[0], ref unsupported) : new AwaitableOfType(awaited);
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
                return key is null or NothingType or ObjectType || !DictOfType.IsValidKey(key) && key is not TypeParameterType ? Unsupported(named.TypeArguments[0], ref unsupported)
                    : value is null or NothingType ? Unsupported(named.TypeArguments[1], ref unsupported)
                    : new DictOfType(key, value);
            }
            // Any other generic type is opaque, when its type arguments are Taipan types' .NET forms (SPEC §13.13).
            var arguments = ImmutableArray.CreateBuilder<TaipanType>();
            foreach (var argument in AllTypeArguments(named))
            {
                if (Exact(argument, typeArguments) is not { } exact)
                    return Unsupported(type, ref unsupported);
                arguments.Add(exact);
            }
            return new DotNetType(named, arguments.ToImmutable());
        }
        if (named.ContainingType is { } outer && AllTypeArguments(outer).Any())
            return Unsupported(type, ref unsupported);
        return new DotNetType(named);
    }

    /// <summary>The type parameters of a type and the types containing it, outermost first, as Roslyn orders them.</summary>
    public static IEnumerable<ITypeParameterSymbol> AllTypeParameters(INamedTypeSymbol type) =>
        (type.ContainingType is { } outer ? AllTypeParameters(outer) : []).Concat(type.OriginalDefinition.TypeParameters);

    /// <summary>The type arguments of a constructed type and the types containing it, outermost first.</summary>
    public static IEnumerable<ITypeSymbol> AllTypeArguments(INamedTypeSymbol type) =>
        (type.ContainingType is { } outer ? AllTypeArguments(outer) : []).Concat(type.TypeArguments);

    /// <summary>
    /// The Taipan type whose .NET form (SPEC §8) is exactly <paramref name="type"/>, as a generic
    /// .NET type's type argument must be (§13.13), or null: <c>long</c> is <c>int</c>, but
    /// <c>int</c> is no Taipan type's form.
    /// </summary>
    public static TaipanType? Exact(ITypeSymbol type, TypeArguments? typeArguments)
    {
        var optional = type.IsReferenceType && type.NullableAnnotation == NullableAnnotation.Annotated;
        var exact = ExactInner(type.WithNullableAnnotation(NullableAnnotation.NotAnnotated), typeArguments);
        return exact is null ? null : optional && exact is not ObjectType and not OptionalOf ? new OptionalOf(exact) : exact;
    }

    private static TaipanType? ExactInner(ITypeSymbol type, TypeArguments? typeArguments)
    {
        if (type is ITypeParameterSymbol parameter)
            return typeArguments?.GetValueOrDefault(parameter);
        switch (type.SpecialType)
        {
            case SpecialType.System_Int64:
                return TaipanType.Int;
            case SpecialType.System_Double:
                return TaipanType.Float;
            case SpecialType.System_Boolean:
                return TaipanType.Bool;
            case SpecialType.System_String:
                return TaipanType.Str;
            case SpecialType.System_Object:
                return TaipanType.Object;
        }
        if (type is not INamedTypeSymbol named || named.DeclaredAccessibility != Accessibility.Public || named.IsRefLikeType || IsUnsupportedStruct(named)
            || named.TypeKind is not (TypeKind.Class or TypeKind.Struct or TypeKind.Interface or TypeKind.Enum) || IsNarrowInteger(named)
            || named.SpecialType is SpecialType.System_Single or SpecialType.System_Char or SpecialType.System_Void or SpecialType.System_IntPtr or SpecialType.System_UIntPtr)
            return null;
        if (named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            return Exact(named.TypeArguments[0], typeArguments) is { } inner and not OptionalOf ? new OptionalOf(inner) : null;
        if (named.Arity == 1 && named.Name == "List" && named.ContainingNamespace?.ToDisplayString() == "System.Collections.Generic")
            return Exact(named.TypeArguments[0], typeArguments) is { } element ? new ListOfType(element) : null;
        if (IsTask(named) && named.Name == "Task")
            return named.TypeArguments.IsEmpty ? new AwaitableOfType(TaipanType.Nothing)
                : Exact(named.TypeArguments[0], typeArguments) is { } awaited ? new AwaitableOfType(awaited) : null;
        if (IsListLike(named) || IsDictionary(named) || named.TypeKind == TypeKind.Delegate || IsTask(named))
            return null;
        var arguments = ImmutableArray.CreateBuilder<TaipanType>();
        foreach (var argument in AllTypeArguments(named))
        {
            if (Exact(argument, typeArguments) is not { } exact)
                return null;
            arguments.Add(exact);
        }
        return new DotNetType(named, arguments.ToImmutable());
    }

    /// <summary>
    /// The constructed base class or interface of <paramref name="type"/> whose definition is
    /// <paramref name="target"/>, with the type arguments in its Roslyn form, substituted by <paramref name="type"/>'s;
    /// null if it doesn't derive from it.
    /// </summary>
    public static INamedTypeSymbol? BaseNamed(INamedTypeSymbol type, INamedTypeSymbol target)
    {
        var definition = target.OriginalDefinition;
        for (var current = type.OriginalDefinition; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, definition))
                return current;
        }
        return type.OriginalDefinition.AllInterfaces.FirstOrDefault(i => SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, definition));
    }

    /// <summary>
    /// <paramref name="type"/> seen as its base class or interface <paramref name="target"/>, with its
    /// type arguments: <c>ISet[str]</c> for <c>HashSet[str]</c>. Null if it doesn't derive from it, or a type argument isn't exact.
    /// </summary>
    public static DotNetType? AsBase(DotNetType type, INamedTypeSymbol target)
    {
        if (BaseNamed(type.Symbol, target) is not { } found)
            return null;
        var map = type.Map;
        var arguments = ImmutableArray.CreateBuilder<TaipanType>();
        foreach (var argument in AllTypeArguments(found))
        {
            if (Exact(argument, map) is not { } exact)
                return null;
            arguments.Add(exact);
        }
        return new DotNetType(found, arguments.ToImmutable());
    }

    /// <summary>Whether a .NET type is, derives from or implements another, with the same type arguments.</summary>
    public static bool IsBase(DotNetType type, DotNetType target) =>
        target.Symbol.SpecialType == SpecialType.System_Object || AsBase(type, target.Symbol) == target;

    /// <summary>
    /// The element type of a .NET value that implements <c>IEnumerable&lt;T&gt;</c>, mapped as a
    /// result (SPEC §13.13), and its .NET form, or null.
    /// </summary>
    public static (TaipanType Element, ITypeSymbol NetElement)? EnumerableElement(DotNetType type)
    {
        var enumerable = type.Symbol.TypeKind == TypeKind.Interface && IsEnumerableOfT(type.Symbol)
            ? type.Symbol
            : type.Symbol.AllInterfaces.FirstOrDefault(IsEnumerableOfT);
        if (enumerable is null)
            return null;
        var element = enumerable.TypeArguments[0];
        return Map(element, result: true, out _, type.Map) is { } mapped and not NothingType ? (mapped, element) : null;
    }

    private static bool IsEnumerableOfT(INamedTypeSymbol type) =>
        type.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T;


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
        return mapped is null or NothingType ? Unsupported(element, ref unsupported) : new ListOfType(mapped);
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
            case ObjectType:
                return argument is ObjectType ? DotNetFit.Exact
                    : argument is PrimitiveType { Name: not "py" } or DotNetType ? DotNetFit.Object : DotNetFit.None;
            case DotNetType target when argument is DotNetType value:
                return value == target ? DotNetFit.Exact
                    : IsBase(value, target) ? DotNetFit.Conversion : DotNetFit.None;
            // A Taipan class with .NET bases is one of them (SPEC §13.16).
            case DotNetType target when argument is ClassType classType:
                return ClassIsBase(classType, target) ? DotNetFit.Conversion : DotNetFit.None;
            case DotNetType when ConvertsImplicitly(argument, parameter):
                return DotNetFit.Conversion;
            // A .NET collection is passed as is to an interface it implements (SPEC §13.13).
            case ListOfType when argument is DotNetType collection:
                return parameter is INamedTypeSymbol { TypeKind: TypeKind.Interface } wanted && Implements(collection, wanted, typeArguments)
                    ? DotNetFit.Conversion : DotNetFit.None;
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

    /// <summary>
    /// Whether a .NET value implements a constructed interface, whose type arguments may use a
    /// generic method's type parameters, with exactly the same type arguments.
    /// </summary>
    public static bool Implements(DotNetType value, INamedTypeSymbol wanted, TypeArguments? typeArguments)
    {
        if (BaseNamed(value.Symbol, wanted) is not { } found)
            return false;
        var map = value.Map;
        return AllTypeArguments(found).Zip(AllTypeArguments(wanted)).All(pair =>
            Exact(pair.First, map) is { } have && Exact(pair.Second, typeArguments) is { } need && have == need);
    }

    /// <summary>A Taipan class's .NET bases, its own and its base classes', that are or derive from <paramref name="target"/> (SPEC §13.16).</summary>
    public static bool ClassIsBase(ClassType type, DotNetType target)
    {
        for (var current = type.Symbol; current is not null; current = current.BaseClass)
        {
            if (current.DotNetBases.Any(b => IsBase(b, target)))
                return true;
        }
        return target.Symbol.SpecialType == SpecialType.System_Object;
    }

    /// <summary>Whether a .NET type defines an implicit conversion from an argument's .NET form: <c>int</c> to <c>Decimal</c> (SPEC §13.3).</summary>
    public static bool ConvertsImplicitly(TaipanType argument, ITypeSymbol target)
    {
        var from = argument switch
        {
            PrimitiveType { Name: "int" } => SpecialType.System_Int64,
            PrimitiveType { Name: "float" } => SpecialType.System_Double,
            PrimitiveType { Name: "bool" } => SpecialType.System_Boolean,
            PrimitiveType { Name: "str" } => SpecialType.System_String,
            _ => SpecialType.None,
        };
        return target is INamedTypeSymbol named && named.GetMembers("op_Implicit").OfType<IMethodSymbol>().Any(m =>
            m.MethodKind == MethodKind.Conversion && m.DeclaredAccessibility == Accessibility.Public && m.Parameters.Length == 1
            && SymbolEqualityComparer.Default.Equals(m.ReturnType, target)
            && (from != SpecialType.None ? m.Parameters[0].Type.SpecialType == from
                : argument is DotNetType net && SymbolEqualityComparer.Default.Equals(m.Parameters[0].Type, net.Symbol)));
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
