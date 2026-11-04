---
title: Building .NET libraries
description: Compile Taipan modules into an assembly that C#, F# and other .NET code can use.
sidebar:
  order: 3
---

`taipan build --library` compiles a program's files into a .NET assembly. Its definitions are a public API that C# and other .NET languages call like any library's.

```taipan title="shapes.tpn"
enum Shape:
    Circle(radius: float)
    Square(side: float)

class UnknownShape(Exception):
    pass

def area(s: Shape) -> float:
    match s:
        Circle(r): return 3.14159 * r * r
        Square(side): return side * side

def parse(name: str) -> Shape:
    match name:
        "circle": return Circle(1.0)
        "square": return Square(1.0)
        _: raise UnknownShape(f"no shape called {name!r}")
```

```taipan title="geometry.tpn"
import shapes

const VERSION = "1.0"

def total_area(items: list[shapes.Shape]) -> float:
    var total = 0.0
    for item in items:
        total += shapes.area(item)
    return total
```

```sh
taipan build geometry.tpn --library -o out
```

That writes `out/geometry.dll`, with `Taipan.Runtime.dll` beside it. From C#:

```csharp
using Geometry;

var circle = new shapes.Shape.Circle(2.0);
Console.WriteLine(geometry.total_area([circle, new shapes.Shape.Square(3.0)]));
try { shapes.parse("hexagon"); }
catch (shapes.UnknownShape e) { Console.WriteLine(e.message); }
```

## What C# sees

- **The namespace** is the main file's name in PascalCase (`geometry` is `Geometry`), and each module is a static class in it, named as the module: `Geometry.shapes`.
- **Functions** are static methods, with an overload for each trailing default. **Constants** are static read-only properties.
- **Classes** are public classes, with constructors, properties for their fields and methods; dataclasses' `ToString` gives Taipan's display. **Enums** are abstract records with a nested record per variant.
- **Types** map as in [Using .NET](../dotnet/#types-at-the-boundary), the other way round: `int` is `long`, `str` is `string`, `list[T]` is `List<T>`, `dict[K, V]` is `OrderedDictionary<K, V>`.
- **Generic** functions, classes and enums are .NET generics where they can be.
- **Names** are kept as written, `total_area` and all. A name starting with `_` stays internal.
- **Exceptions** propagate as .NET exceptions: Taipan's classes, the runtime's (`Taipan.Runtime.KeyError`, …), or .NET's own.

A definition that can't be exported, such as one whose signature uses a type that isn't, is warning [TP0302](../../diagnostics/tp0302/), saying why.

The library's main file holds declarations only: a top-level statement is [TP0222](../../diagnostics/tp0222/). Each module initializes when one of its functions, constructors or constants is first used.

## Referencing it

A .NET project references `geometry.dll` and the `TaipanLang.Runtime` package of the same version. Better, use an SDK project and a `ProjectReference`: see [Projects](../projects/).

## Naming and packing

`taipan.json` can name the assembly, namespace and version:

```json
{
  "assembly": "Acme.Shapes",
  "namespace": "Acme.Shapes",
  "version": "1.2.0",
  "authors": "Acme",
  "description": "Shapes and their areas."
}
```

`taipan pack geometry.tpn -o out` builds the library and writes a NuGet package, `out/Acme.Shapes.1.2.0.nupkg`, depending on `TaipanLang.Runtime` and the packages `taipan.json` lists.
