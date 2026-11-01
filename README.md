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

- **Python's syntax:** indentation, `def`, `for`/`while`, f-strings, `match`, PEP 695 generics (`def first[T](xs: list[T]) -> T?`).
- **Static types:** annotate function signatures, and locals are inferred. There's no truthiness: conditions must be `bool`.
- **Optionals instead of null:** a `T?` must be narrowed with `is some`, or given a default with `??`, before use.
- **Classes:** Python's classes, with single inheritance, `super().__init__(…)`, required `@override`, and fields that must be set before the object is used. `@dataclass` classes get a constructor, field-by-field equality and display. `isinstance(a, Dog)` and `x is not None` narrow a name in the code they guard.
- **Enums:** enums carry payloads, and `match` must cover every variant.
- **Default and keyword arguments:** `def connect(host: str, port: int = 8080)`, called as `connect("db", port=5432)`.
- **Functions as values:** lambdas, nested `def`s and closures, typed as Python's `Callable[[int], str]`. `sorted(services, key=lambda s: s.healthy)`, `map` and `filter` work as in Python, except that `map` and `filter` return lists.
- **Tuples and slicing:** `tuple[int, str]`, `return lo, hi`, `let lo, hi = bounds(xs)`, `for i, x in enumerate(xs)`, and `xs[1:3]`, `s[::-1]` with Python's semantics. Unpacking with the wrong count is a compile error.
- **Exceptions:** `try`/`except`/`else`/`finally` and `raise`, with Python's exception classes. A missing dict key is a `KeyError` and an overflow an `OverflowError`, and a Python exception can be caught by its class (`except json.JSONDecodeError`). User exceptions are classes deriving from `Exception`.
- **Protocols:** structural interfaces, as Python's `typing.Protocol`. `def largest[T: Comparable](xs: list[T]) -> T?` works for `int`, `str` and any class with `__lt__`. A class that doesn't fit is reported at compile time, naming the missing method; a class may also declare the protocols it means to satisfy, `class Version(Comparable):`, to be checked where it's defined.
- **Checked arithmetic:** int overflow, division by zero, and out-of-range indexing raise exceptions. Uncaught, they're panics that report the `.tpn` line.
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
