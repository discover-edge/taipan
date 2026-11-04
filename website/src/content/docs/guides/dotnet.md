---
title: Using .NET
description: Call .NET libraries and NuGet packages from Taipan, with static types.
sidebar:
  order: 1
---

Taipan programs run on .NET, and can use any .NET library: the runtime's own, local assemblies, and NuGet packages. .NET types are read from the assemblies' metadata, so a .NET call is checked when you compile, like a Taipan call.

## Importing types

`from dotnet.<namespace> import <Type>` imports .NET types by name. The namespace is written as in C#:

```taipan
from dotnet.System import Math, DateTime, Guid, TimeSpan
from dotnet.System.Text import StringBuilder

let sb = StringBuilder()
sb.Append("pi is ").Append(Math.Round(Math.PI, 3))
print(sb.ToString())
print(TimeSpan.FromMinutes(90).TotalHours)
print(len(Guid.NewGuid().ToString()), DateTime(2026, 1, 1).DayOfWeek)
```

```output
pi is 3.142
1.5
36 Thursday
```

- .NET names are written as in .NET: `sb.Append`, `File.ReadAllLines`, `DateTime.Now`.
- Static members are reached through the type, instance members through a value.
- Constructors are called through the type: `StringBuilder()`, `Uri("https://example.com")`.
- `import dotnet.System.IO as io` imports a whole namespace, used as `io.File.Exists(path)`.

## Types at the boundary

.NET types map to Taipan's where they meet:

| .NET | Taipan |
|---|---|
| `long`, `int`, `short`, `byte`, … | `int` (narrower integers are checked: out of range raises `OverflowError`) |
| `double`, `float` | `float` |
| `bool`, `string`, `char` | `bool`, `str`, `str` |
| `T?`, `Nullable<T>`, a nullable reference | `T?` |
| `List<T>`, `T[]`, `IEnumerable<T>`, … | `list[T]` |
| `Dictionary<K, V>`, `IDictionary<K, V>`, … | `dict[K, V]` |
| `Func<…>`, `Action<…>` and other delegates | `Callable[[…], …]` |
| `Task<T>`, `ValueTask<T>` | `Awaitable[T]` |
| `object` | `object` |
| any other class, struct, interface or enum | itself |

A `list[T]` is a .NET `List<T>`, so it also has `List<T>`'s methods, such as `xs.BinarySearch(x)` and `xs.ConvertAll(f)`.

## Overloads, generics and delegates

Overloads are chosen as C# chooses them. Generic methods infer their type arguments, or take them in brackets, and lambdas convert to delegates:

```taipan
from dotnet.System import Array, Int32, String
from dotnet.System.Linq import Enumerable

let xs = [5, 3, 8, 1]
print(Enumerable.Where(xs, lambda x: x > 2))
print(xs.Where(lambda x: x % 2 == 1).Select(lambda x: x * 10).ToList())
print(String.Join(", ", "a", "b", "c"))
print(Int32.TryParse("12"), Int32.TryParse("x"))
print(len(Array.Empty[str]()))
```

```output
[5, 3, 8]
[50, 30, 10]
a, b, c
(True, 12) (False, 0)
0
```

- An `IEnumerable<T>` result is a `list[T]`, read at once.
- **Extension methods** apply when their static class is imported by name: `xs.Where(…)` after `from dotnet.System.Linq import Enumerable`.
- **`out` parameters** come back in a tuple after the result, so `Int32.TryParse("12")` is `(True, 12)`.
- **`params` arrays** take a list or separate arguments.

## Exceptions

.NET exceptions are caught by their class, and .NET exception classes can be raised:

```taipan
from dotnet.System.IO import File, FileNotFoundException
from dotnet.System import InvalidOperationException

try:
    File.ReadAllText("no-such-file.txt")
except FileNotFoundException as e:
    print("missing:", e.Message.startswith("Could not find file"))

try:
    raise InvalidOperationException("closed")
except Exception as e:
    print(e)
```

```output
missing: True
closed
```

`except Exception` catches .NET exceptions too, as a `DotNetError`. Uncaught, one prints a traceback ending in the .NET class's full name.

## Classes and interfaces

A Taipan class can derive from a .NET class, override its virtual methods, and implement .NET interfaces, so .NET code can call it:

```taipan
from dotnet.System import IComparable

class Version(IComparable[Version]):
    major: int
    minor: int

    def __init__(self, major: int, minor: int):
        self.major = major
        self.minor = minor

    def CompareTo(self, other: Version?) -> int:
        if other is None:
            return 1
        return (self.major - other.major) * 1000 + (self.minor - other.minor)

    def __repr__(self) -> str:
        return f"{self.major}.{self.minor}"

let versions = [Version(2, 0), Version(1, 5), Version(1, 10)]
versions.Sort()
print(versions)
```

```output
[1.5, 1.10, 2.0]
```

## Async

.NET tasks are awaitable. In an async function, a .NET method's `CancellationToken` parameter can be left out, and the function's own token is passed, so a timeout cancels the .NET work too:

```taipan
import asyncio
from dotnet.System.Threading.Tasks import Task

async def main():
    try:
        async with asyncio.timeout(0.05):
            await Task.Delay(10_000)
    except TimeoutError:
        print("cancelled the delay")

asyncio.run(main())
```

```output
cancelled the delay
```

## Assemblies and NuGet packages

Other assemblies and packages are listed in `taipan.json`, beside the main file:

```json
{
  "references": ["lib/Acme.Geometry.dll"],
  "packages": { "Humanizer.Core": "2.14.1" }
}
```

```taipan
from dotnet.Humanizer import NumberToWordsExtension

print(NumberToWordsExtension.ToWords(42))
```

Packages are restored with the .NET SDK the first time a list is used, and cached. `taipan build` copies the assemblies next to the program. In an SDK project, use `PackageReference` and `ProjectReference` instead; see [Projects](../projects/).

The [specification](../../reference/specification/#13-net-interop-m21) has the complete rules.
