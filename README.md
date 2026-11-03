# Taipan

A statically typed, compiled language with Python's syntax. Taipan reads like Python, but checks types at compile time, has no `null` surprises, and requires exhaustive `match`. Programs compile to .NET assemblies, and Python libraries are reachable through a checked boundary.

```python
@dataclass
class Service:
    name: str
    replicas: int
    healthy: int

enum Health:
    Ok
    Degraded(healthy: int, total: int)
    Down

def status(svc: Service) -> Health:
    if svc.healthy == svc.replicas:
        return Ok
    elif svc.healthy > 0:
        return Degraded(svc.healthy, svc.replicas)
    return Down

def find[T](items: list[T], keep: list[bool]) -> T?:
    for i in range(len(items)):
        if keep[i]:
            return items[i]
    return None

let services = [Service("api", 3, 3), Service("worker", 4, 3)]
for svc in services:
    match status(svc):
        Ok: print(svc.name, "ok")
        Degraded(h, t): print(svc.name, f"degraded ({h}/{t})")
        Down: print(svc.name, "down")

if find(services, [False, True]) is some s:
    print("found", s.name)
```

```
api ok
worker degraded (3/4)
found worker
```

## Getting started

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download). Then build the compiler from source:

```
git clone git@github.com:discover-edge/taipan.git
cd taipan
dotnet build
```

Run a program with:

```
dotnet run --project src/Taipan.Cli -- run examples/health.tpn
```

To put `taipan` on your path instead, pack it as a .NET tool and install it from the local package. It needs only the .NET 10 runtime to run:

```
dotnet pack -c Release -o packages
dotnet tool install -g TaipanLang --add-source packages
taipan run examples/health.tpn
```

### Commands

| Command | What it does |
|---|---|
| `taipan check <file>` | Type-checks the program and prints diagnostics |
| `taipan run <file>` | Compiles and runs the program |
| `taipan emit <file>` | Prints the generated C# |
| `taipan build <file> -o <dir>` | Writes a runnable `<dir>/<file>.dll` (run it with `dotnet`) |
| `taipan --version` | Prints the version |

Exit codes: `0` success, `1` compile error, `2` usage error, `101` runtime panic.

## The language at a glance

- **Python's syntax:** indentation, `def`, `for`/`while`, f-strings (with format specs, `!r` and `{x=}`, checked at compile time), `match`, `del` and slice assignment, PEP 695 generics (`def first[T](xs: list[T]) -> T?`).
- **Static types:** annotate function signatures, and locals are inferred. There's no truthiness: conditions must be `bool`.
- **Optionals instead of null:** a `T?` must be narrowed before use: with `is some`, `is not None`, or an early exit (`if s is None: return`), which narrows the rest of the block, or given a default with `??`. Narrowing follows `var`s, field paths (`self.config.port`) and elements with a fixed key (`xs[0]`) too, until something could change them, and the error says what did. `x == 3` narrows an optional as `x is not None` does, and `a is b` compares identity.
- **Classes:** Python's classes, with single inheritance, `super().__init__(…)`, required `@override`, and fields that must be set before the object is used. `@dataclass` classes get a constructor, field-by-field equality and display. Special methods (`__str__`, `__repr__`, `__eq__`, `__hash__`, comparisons and arithmetic, with Python's reflected forms) work with `print`, `==`, dict keys and operators. Classes can also have `@property` (with setters), `@staticmethod` and `@classmethod` methods, `ClassVar` attributes, and `@abstractmethod`s, and constructing an abstract class is a compile-time error. `@dataclass(frozen=True, order=True)`, `__post_init__`, `@final`, `@total_ordering` and `Self` results work as in Python, assigning a frozen field is a compile-time error, and classes can define `__len__`, `__getitem__`, `__setitem__`, `__contains__`, `__call__`, in-place operators such as `__iadd__`, and `__format__`. Generic classes can derive and be derived from (`class IntBox(Box[int])`). `isinstance(a, Dog)` and `x is not None` narrow a name in the code they guard.
- **Enums and `match`:** enums carry payloads, and `match` must cover every case. Patterns nest as in Python (`Move((0, dy))`, `Some(Dog(name="rex"))`, `[first, *rest]`), or-patterns, guards, `as`, `None`, mapping and `*` patterns work as in Python (`0 | 1`, `n if n > 0`, `{"port": p, **rest}`), a missing case is reported as a pattern, and `match` is also an expression, usable anywhere, with arms that can be blocks.
- **Parameters:** defaults and keyword arguments, `def connect(host: str, port: int = 8080)` called as `connect("db", port=5432)`, plus Python's positional-only (`/`) and keyword-only (`*`) parameters, `*args: T` and `**kwargs: T`, and `f(*xs, **opts)` at a call. A wrong call is reported at compile time, with the errors Python would raise.
- **Functions as values:** lambdas, nested `def`s and closures, typed as Python's `Callable[[int], str]`. `sorted(services, key=lambda s: s.healthy)`, `map` and `filter` work as in Python, and generic functions, built-ins and constructors are values too: `sorted(words, key=len)`, `list(map(Dog, names))`.
- **Everyday built-ins:** `min`, `max`, `sum`, `any`, `all`, `abs`, `round` and `divmod`, the `**` operator, Python's common `str`, `list` and `dict` methods (`join`, `split()`, `find`, `extend`, `setdefault`, …), and `set[T]` with `{a, b}` literals and the `| & - ^` operators.
- **Comprehensions:** `[x * x for x in xs if x > 0]`, dict and set comprehensions, and lazy generator expressions such as `sum(len(w) for w in words)`, with any number of `for` and `if` clauses.
- **Programs and I/O:** `sys.argv`, `sys.exit`, `input()`, `os.environ` and `os.getenv`, text files from `open()` read by line or whole and written with `write` or `print(..., file=f)`, and `with` statements over files and context managers written as classes with `__enter__` and `__exit__`.
- **Iterators and generators:** `def naturals() -> Iterator[int]:` with `yield` and `yield from`, `Iterable[T]` parameters that take lists, dicts, strs and iterators, classes with `__iter__` and `__next__`, and lazy `map`, `filter`, `zip` and `enumerate`, as in Python 3. `list(…)`, `iter(…)` and `next(…)` are built in, and indexing an iterator is a compile-time error suggesting `list(…)`.
- **Async functions:** `async def fetch(url: str) -> str:` with `await`, in async functions and at the main file's top level. An async function's result is an `Awaitable[str]`, and one that's never awaited is a compile-time error.
- **Tuples and slicing:** `tuple[int, str]`, variable-length `tuple[str, ...]`, `return lo, hi`, `let lo, hi = bounds(xs)`, `for i, (k, v) in enumerate(d.items())`, `first, *rest = xs`, and `xs[1:3]`, `s[::-1]` with Python's semantics. Unpacking a tuple with the wrong count is a compile error; a list's length is checked at run time, with Python's `ValueError`.
- **Exceptions:** `try`/`except`/`else`/`finally` and `raise`, with Python's exception classes. A missing dict key is a `KeyError` and an overflow an `OverflowError`, and a Python exception can be caught by its class (`except json.JSONDecodeError`). User exceptions are classes deriving from `Exception`, or dataclasses. `raise NotFound(name) from e` chains exceptions, an exception raised while handling another keeps it as its `__context__`, and `args`, `__cause__` and `__traceback__` work as in Python.
- **Protocols:** structural interfaces, as Python's `typing.Protocol`. `def largest[T: Comparable](xs: list[T]) -> T?` works for `int`, `str` and any class with `__lt__`. A class that doesn't fit is reported at compile time, naming the missing method; a class may also declare the protocols it means to satisfy, `class Version(Comparable):`, to be checked where it's defined. Protocols can also be the types of values (`list[Drawable]`), have fields, and be generic, and a type parameter may be bounded by a class (`[A: Animal]`).
- **Checked arithmetic:** int overflow, division by zero, and out-of-range indexing raise exceptions. Uncaught, an exception prints a traceback laid out as CPython's, chained exceptions included.
- **Compile-time warnings** for code that can never run, and for an `except` that silently swallows every exception.
- **Modules:** `import geometry` or `from geometry import Point` loads `geometry.tpn` from the main file's directory.
- **Python interop:** `from python import json` gives `py` values. These are dynamic objects that reach static types only through checked conversions (`x as int?`):

```python
from python import json

let data = json.loads("{\"replicas\": 3}")
let replicas = data["replicas"] as int? ?? 0
print(replicas * 2)
```

Python interop needs a CPython 3 installation. Taipan finds it through the `PYTHONNET_PYDLL` environment variable (the path to the shared library, such as `libpython3.12.so`), or else by asking `python3` or `python` on your `PATH`.

- **.NET libraries:** `from dotnet.System.Text import StringBuilder` imports .NET types, whose members are checked at compile time from the assemblies' metadata. Overloads are resolved as C# would, nullable annotations give optionals, and .NET exceptions can be caught by their class. Generic methods infer their type arguments, so LINQ works as `Enumerable.Where(xs, lambda x: x > 2)`; functions and lambdas pass as delegates, `Dictionary<K, V>` is a `dict`, `Task<T>` is awaited with `await`, and .NET indexers and operators work as written. Local assemblies and NuGet packages are listed in a `taipan.json` beside the main file:

```python
from dotnet.System.IO import File, FileNotFoundException
from dotnet.System.Text import StringBuilder

let sb = StringBuilder()
try:
    for line in File.ReadAllLines("notes.txt"):
        sb.Append(line.upper()).Append("\n")
except FileNotFoundException as e:
    sb.Append("missing: ").Append(e.FileName ?? "notes.txt")
print(sb.ToString())
```

```json
{ "references": ["lib/Acme.Geometry.dll"], "packages": { "Humanizer.Core": "2.14.1" } }
```

- **Taipan as a .NET library:** `taipan build geometry.tpn --library -o out` compiles a program's modules into `geometry.dll` for C# or F# to reference, alongside the `TaipanLang.Runtime` package. Each module is a static class in the namespace `Geometry`, with its classes and enums nested inside, all named as in Taipan. Names starting with `_` stay private, and Taipan exceptions reach the caller as .NET exceptions. Building a library needs the .NET SDK.
- **In a .NET solution:** a `.tpnproj` using the `TaipanLang.Sdk` project SDK builds `.tpn` files with `dotnet build`, `dotnet run`, `dotnet test` and `dotnet pack`, with `ProjectReference`s to and from C# projects and `PackageReference`s as usual:

```xml
<Project Sdk="TaipanLang.Sdk/0.3.0">
  <ItemGroup>
    <ProjectReference Include="../Greeting/Greeting.csproj" />
  </ItemGroup>
</Project>
```

  Outside a solution, `taipan pack geometry.tpn -o out` packs a library as a NuGet package, named by `assembly`, `namespace` and `version` in `taipan.json`.

```csharp
using Geometry;

Console.WriteLine(shapes.area(new shapes.Shape.Circle(2.0)));
try { shapes.parse("hexagon"); }
catch (shapes.UnknownShape e) { Console.WriteLine(e.message); }
```

- **Native executables:** `taipan build health.tpn --native -o out` compiles a program ahead of time with .NET's NativeAOT into one self-contained executable that runs without .NET installed, with the same output, exit codes and panic locations as `taipan run`. It needs the .NET SDK and a C toolchain (clang or gcc on Linux) to build. Python interop can't be compiled natively, and is rejected at compile time.

- **CPython extension modules:** `taipan build geometry.tpn --python-extension -o out` compiles a module ahead of time into `geometry.abi3.so`, which any CPython from 3.10 on can import, with no .NET installed. Functions taking and returning `int`, `float`, `bool`, `str`, `None` and lists, dicts, tuples and optionals of those become Python functions (keyword arguments, defaults, `/`, `*`, `*args` and `**kwargs` included), constants become attributes, and exception classes become Python exception classes. A Taipan exception arrives in Python with the same message:

```
$ cd out && python3 -c "import geometry; print(geometry.area(2.0))"
12.56636
```

- **Language server:** `taipan lsp` speaks the Language Server Protocol over stdin and stdout, for diagnostics as you type, the type of a name on hover, go to definition, completion, signature help, find references, rename across modules (refusing a rename that would break the program), and semantic highlighting.
- **VS Code:** `editors/vscode` is an extension with a TextMate grammar that starts `taipan lsp`. Build it with `npm ci && npx vsce package` there, and install the `.vsix` with *Extensions: Install from VSIX…*. Any other LSP client works too. In Neovim:

```lua
vim.filetype.add({ extension = { tpn = "taipan" } })
vim.api.nvim_create_autocmd("FileType", {
  pattern = "taipan",
  callback = function() vim.lsp.start({ name = "taipan", cmd = { "taipan", "lsp" } }) end,
})
```

In Helix's `languages.toml`:

```toml
[language-server.taipan]
command = "taipan"
args = ["lsp"]

[[language]]
name = "taipan"
scope = "source.taipan"
file-types = ["tpn"]
language-servers = ["taipan"]
```

[SPEC.md](SPEC.md) is the full language definition, and `examples/` and `tests/cases/` have many more programs.

## How it works

The compiler is written in C#. It lexes and parses `.tpn` files, type-checks them into a bound tree, emits C#, and compiles that in-process with Roslyn. `#line` directives map .NET stack traces back to `.tpn` lines, so panics report the right source line. Generics are monomorphized. Python is embedded through [pythonnet](https://github.com/pythonnet/pythonnet).

```
src/Taipan.Compiler/   lexer, parser, binder and type checker, C# emitter
src/Taipan.Runtime/    panics, operators, display, Python interop (used by compiled programs)
src/Taipan.Cli/        the taipan command
tests/                 golden tests (tests/cases/) and unit tests
```

## Development

```
dotnet test
```

Golden tests in `tests/cases/` are the main signal. Each `.tpn` runs through the CLI, and its output is compared with sidecar files (`.out`, `.err`, `.exit`, and `.cs` for emitted C#). [CLAUDE.md](CLAUDE.md) describes the conventions, and [ROADMAP.md](ROADMAP.md) has the milestones, past and planned. Language changes start as SPEC.md edits.

## Name

Taipan is a snake, as a nod to Python, and a strict one. Source files use `.tpn`, and diagnostic codes use the `TP` prefix.
