# Roadmap

Each milestone is done when **all** of its acceptance criteria pass as golden tests (see CLAUDE.md). Milestones are sequential. Don't start the next one early.

M0–M10 (with M8.1) make up v0.1 and are complete. M11 through M47 are complete.

## M0 — Scaffold
- Create the solution layout from CLAUDE.md, the `taipan` CLI with `check | emit | build | run` stubs, and the golden-test harness.
- **Accept:**
  - `dotnet test` passes with one golden case: an empty `.tpn` file runs, prints nothing, and exits 0.

## M1 — Lexer
- Tokens, INDENT/DEDENT/NEWLINE, implicit line joining, string escapes, and f-string tokens with hole spans.
- Diagnostics TP0001–TP0007.
- **Accept:**
  - Token-stream unit tests for nested blocks, dedent-by-multiple-levels, bracket joining, comments, and blank lines.
  - Error cases for all seven codes.

## M2 — Parser
- A full AST for SPEC §2, excluding generics, methods, and `as`.
- Recovery: on a syntax error, report TP0100, sync to the next NEWLINE at the current indent, and continue.
- **Accept:**
  - AST snapshot tests for every grammar production.
  - One file with three independent syntax errors reports all three.

## M3 — Type checker (core)
- Primitives, `let`/`var`/`const`, functions, `if`/`while`/`for`/`range`, operators, conditions, and return-path analysis.
- **Accept:**
  - An error golden case for every TP02xx code reachable without structs, enums, or optionals.

## M4 — Codegen + run
- C# emission, in-process Roslyn compile and execute, the `Taipan.Runtime` panic and display system, and `#line` mapping.
- **Accept:**
  - `hello.tpn`, `fizzbuzz.tpn`, and `fib.tpn` (recursive) produce the expected stdout.
  - An overflow and a ÷0 case exit 101 with the correct `.tpn` line.
  - `taipan emit` output is readable C#.

## M5 — Structs, lists, dicts, methods
- **Accept:**
  - Construction (positional and keyword), field mutation, structural `==`, and display form.
  - List and dict built-ins, `for` over list, dict, and str, and negative indexing.
  - Methods with `self`.
  - TP0206 and TP0210 cases.

## M6 — Optionals
- `T?`, `none`, `is some`/`is none` narrowing, `??`, and `d.get`.
- **Accept:**
  - The `find(...)` example from `examples/health.tpn` compiles.
  - Using `Service?` as `Service` reports TP0203 at the right column.
  - TP0213 is reported.

## M7 — Enums + match
- **Accept:**
  - `examples/health.tpn` runs and prints `degraded (3/4)`.
  - Removing any arm reports TP0204 naming the missing variant.
  - TP0216 and TP0300 cases.
  - `match` over int, str, and bool with exhaustiveness rules.

## M8 — Generics
- PEP 695-style `def first[T](xs: list[T]) -> T?` and `struct Box[T]`, with type-argument inference at call sites.
- Update SPEC §3 with the rules first.

## M8.1 — Constrained type parameters
- PEP 695 constraint tuples: `def total[N: (int, float)](xs: list[N]) -> N`, `def count[K: (int, str, bool)](xs: list[K]) -> dict[K, int]`.
- A constrained parameter allows exactly the operations every listed type supports (arithmetic, ordering, dict keys); the body is checked once per listed type.
- Update SPEC §3.6 with the rules first.
- **Accept:**
  - `total`, `largest` and `count` work for each of their listed types.
  - An operation that some listed type lacks is reported, naming that type.
  - A call whose inferred type argument isn't in the tuple is reported.

## M9 — Modules
- Multi-file programs: `import other` and `from other import name`.
- The spec section is written at the start of this milestone.

## M10 — Python interop (level 1)
- The SPEC §10 sketch, finalized into real spec text first, then implemented with pythonnet.
- **Accept:**
  - Import `json` from the Python stdlib, parse a string, and extract an `int` via `as int?`.
  - A failed `as int` panics with exit 101.

## M11 — Lambdas and closures
- `lambda x: x * 2` and nested `def`s that capture enclosing bindings, with function types to annotate them.
- Function values can be stored, passed and returned. Built-ins gain the higher-order functions they enable (`sorted(xs, key=...)`, `list.sort`, `map`/`filter` or their equivalent).
- Update SPEC first: function types, capture semantics (a `let` captures its value, a `var` captures its binding), and how this fits "functions aren't values" (§4.5).
- **Accept:**
  - `sorted(services, key=lambda s: s.healthy)` works, and a closure counter (a `def` returning a function that bumps a captured `var`) behaves as in Python.
  - Calling a non-function, or a function with the wrong argument types, reports the right code at the right column.

## M12 — Tuples and slicing
- Tuple types, literals and unpacking: `def bounds(xs: list[int]) -> tuple[int, int]`, `let lo, hi = bounds(xs)`, `for i, x in enumerate(xs)`.
- Slicing for `list` and `str`: `xs[1:3]`, `xs[::-1]`, `s[:5]`, with Python's clamping.
- Update SPEC first: tuple display, equality, use as dict keys, and how tuples meet `match` and Python's `tuple`.
- **Accept:**
  - Unpacking with the wrong count is a compile error, and `xs[a:b:0]` panics.
  - Slices are new lists, matching Python for negative and out-of-range bounds.

## M13 — `None`
- Spell the none value and type `None`, as in Python: `x is None`, `return None`, `Callable[[str], None]`, and `None` in display output. `none` is no longer a keyword.
- Update SPEC first, then the tests and examples.
- **Accept:**
  - Every existing case passes with `None`, and `print([1, None])` prints `[1, None]`.
  - `none` in source reports `TP0201`, with help suggesting `None`.

## M14 — Default and keyword arguments
- Parameters with default values (`def connect(host: str, port: int = 8080)`) and keyword arguments in calls to user functions and methods (`connect("db", port=5432)`), mixed with positional ones as in Python.
- Update SPEC first: which defaults are allowed and when they're evaluated, how positional and keyword arguments are matched, and the errors for missing, repeated or unknown arguments.
- **Accept:**
  - Defaults and keywords work for functions and methods, and a default isn't shared between calls the way Python's mutable defaults are.
  - A missing, repeated or unknown argument reports the right code at the right column.

## M15 — Classes
- `class Animal:` with typed field declarations, `__init__`, methods, and single inheritance (`class Dog(Animal):`, `super().__init__(…)`).
- `@dataclass class` replaces `struct`: it generates the constructor, field-by-field equality and display, as in Python. Plain classes compare by identity and may be dict keys.
- Strict initialization: every field is set by the end of `__init__`, and `self` isn't used before then. Overrides need `@override` and a compatible signature.
- A subclass is assignable to its base. `isinstance(x, Dog)` and `x is not None` narrow a `let` or parameter itself inside the block (`is some v` stays), and `match` class patterns and `as` narrow too. Generic types stay invariant, while function types take parameters contravariantly and results covariantly.
- Update SPEC first: class declarations and initialization rules, dataclasses, override checking, equality and display of classes, narrowing, and how classes are emitted. Then rewrite the existing `struct` cases.
- **Accept:**
  - A `Dog` stored in a `list[Animal]` calls its overriding method, and `isinstance` narrowing reaches a `Dog`-only method.
  - Reading an unset field, a missing `@override`, and an incompatible override each report the right code at the right column.

## M16 — Exceptions
- `try`/`except`/`finally` and `raise`, with Taipan's runtime errors (index, key, overflow…) as catchable exception types. Exceptions are classes deriving from `Exception`, as in Python.
- Python exceptions from `py` operations become catchable instead of panicking.
- Update SPEC first: the exception types, what is and isn't catchable, and how uncaught exceptions keep §7's panic output and exit code 101.
- **Accept:**
  - Catching a missing dict key, an overflow, and a Python `json.JSONDecodeError` each work.
  - An uncaught exception still prints `panic: …` with the `.tpn` line and exits 101.

## M17 — Protocols
- Structural interfaces (`protocol Comparable: def less(self, other: Self) -> bool`) that classes satisfy without declaring it.
- Protocols as generic bounds (`def largest[T: Comparable](xs: list[T]) -> T?`), complementing M8.1's constraint tuples.
- Update SPEC first: matching rules, what built-in types satisfy, and error messages for unmet bounds.
- **Accept:**
  - `largest` works for a user class and for `int`/`str` through their built-in protocols.
  - A class missing a method reports which one at the call site.

## M18 — Type aliases
- `type Handler = Callable[[Request], Response]`, as in Python 3.12 (PEP 695), including generic aliases (`type Pair[T] = tuple[T, T]`) and aliases from other modules.
- An alias is the type it names: interchangeable with it everywhere, with no new runtime type.
- Update SPEC first: where aliases may be declared and used, cycles (`type A = list[A]`), shadowing and name clashes, and how diagnostics display an aliased type.
- **Accept:**
  - A function taking a `Handler` accepts a matching lambda, and a generic alias instantiates correctly.
  - A recursive alias and a wrong number of alias type arguments each report the right code at the right column.

## M19 — `match` as an expression
- `match` producing a value, so `let label = match status: …` works, with each arm an expression.
- Exhaustiveness as for the `match` statement, and the result type is the arms' common type, as for list literals.
- Update SPEC first: the syntax (keeping Python's `match` statement unchanged), where a `match` expression may appear, and how arms' types join.
- **Accept:**
  - A `match` expression over an enum and over a class hierarchy each give the right value.
  - A non-exhaustive `match` expression and arms of incompatible types each report the right code.

## M20 — `dotnet tool` packaging
- Package the `taipan` CLI as a .NET tool, so `dotnet tool install -g <id>` puts `taipan` on the path. The runtime library travels with it.
- Settle the NuGet package IDs (for the tool and for `Taipan.Runtime`) before starting.
- **Accept:**
  - Installing the tool from a locally built `.nupkg` in a clean directory lets `taipan run examples/health.tpn` work, Python examples included.
  - `taipan --version` prints the package version.

## M21 — .NET libraries from Taipan
- Reference .NET assemblies and NuGet packages, and use their APIs with static types read from metadata through Roslyn: `from dotnet.System.Text import StringBuilder`. Unlike `py`, calls are checked at compile time.
- Static methods, constructors, instance methods and properties whose types map to Taipan's (`long`, `double`, `string`, `bool`; other integer and float widths through checked conversions), and .NET classes as opaque types. Nullable annotations give `T?`.
- .NET exceptions are catchable in `except`.
- Update SPEC first: how assemblies and packages are referenced, the type mapping and conversions, how members are named and looked up, treatment of assemblies without nullable annotations, and what is deferred (overloads beyond a defined resolution rule, generic methods, delegates, `async`, events, `ref`/`out`).
- **Accept:**
  - A program builds a string with `System.Text.StringBuilder` and reads a file's lines through `System.IO.File`, each with the right output.
  - Calling a .NET method with a wrong argument type, and a member that doesn't exist, each report the right code at the right column.

## M22 — Taipan as a .NET library
- `taipan build <file> --library -o <dir>` produces a .NET assembly that C# and F# can reference: each module a public static class of its functions and constants, classes with public constructors, and enums as records.
- No panic handler in a library: Taipan exceptions propagate to the caller as .NET exceptions. Generic functions are exported only where they can be real .NET generics.
- Builds on M20: `Taipan.Runtime` is published as a package that libraries depend on.
- Update SPEC first: the public names and shapes of exported definitions, what isn't exported, and how Python interop is initialized in a library.
- **Accept:**
  - A C# test project references a built Taipan library, calls a function, constructs a class and catches a Taipan exception.
  - Exported names are stable: renaming a local or adding a private helper doesn't change the public API.

## M23 — NativeAOT publishing
- `taipan build <file> -o <dir> --native` produces a self-contained native executable through NativeAOT, needing no .NET runtime to run.
- Update SPEC first: which programs can be published natively (Python interop may not be, since pythonnet relies on runtime reflection), and the error for one that can't.
- **Accept:**
  - A native build of a non-Python example runs and prints the same output and exit codes as `taipan run`, panics included.
  - A program that can't be published natively is rejected with a clear diagnostic, not a failed publish.

## M24 — Language server
- `taipan lsp`: a Language Server Protocol server over stdio, reusing the compiler's diagnostics. No new dependencies without approval.
- Diagnostics as you type, hover showing a name's type, and go to definition, including across modules.
- **Accept:**
  - A scripted LSP session gets the same diagnostics `taipan check` reports, with matching ranges.
  - Hover on a local inferred from an expression shows its type, and go to definition on an imported name reaches the other module.

## M25 — CPython extension modules
- Interop level 3: `taipan build <module> --python-extension` produces a native module that CPython imports. Taipan functions taking and returning convertible types (§10.3) become callable from Python.
- Builds on M23's NativeAOT output, exporting `PyInit_<module>` through the CPython C API.
- Update SPEC first: which definitions are exported, how arguments and results convert, and how a Taipan exception reaches Python.
- **Accept:**
  - `python -c "import geometry; print(geometry.area(2.0))"` works against a built module.
  - A Taipan exception raised in an exported function arrives in Python as an exception with the same message.

## M26 — Everyday gaps
- Format specs in f-strings (`f"{x:.2f}"`, `f"{n:>5}"`, `f"{n:,}"`), as Python's format mini-language for `int`, `float` and `str`.
- `-> None` as an explicit "returns nothing" annotation, `zip` of more than two lists, `isinstance(x, (A, B))` with a tuple of classes, and slice assignment and `del` on lists and dicts. (Defaults on lambda parameters were dropped: per-iteration capture makes Python's `lambda i=i:` idiom unnecessary.)
- Update SPEC first: which format specs are supported and the error for one that isn't, and how `del` and slice assignment behave on out-of-range indices.
- **Accept:**
  - Golden cases print the same text as Python for a table of format specs, and an unsupported spec is a compile-time error at the spec.
  - `del xs[1]`, `del d["k"]`, `xs[1:3] = [9]`, and a three-way `zip` each give Python's results.

## M27 — Flow narrowing
- Narrowing that follows control flow: after `if x is None: return` (or `raise`, `break`, `continue`), `x` is narrowed for the rest of the block, and likewise for `isinstance`.
- Narrowing `var`s until they're reassigned, and fields read through a `let`, where nothing in between can change them.
- Update SPEC first: exactly which statements end narrowing (assignments, calls that could change a field), so the rules stay predictable.
- **Accept:**
  - The early-return pattern type-checks without `??` or `is some`, and assigning the variable afterwards ends the narrowing (`TP0203` at a later use).
  - Narrowing a field is rejected where a method call in between could change it.

## M28 — Classes II
- Special methods: `__str__` (used by `print`, `str()` and f-strings), `__eq__` and `__hash__` (used by `==`, `in` and dict keys), `__le__`, `__gt__` and `__ge__`, and the arithmetic operators (`__add__`, `__sub__`, `__mul__`, `__neg__`, …).
- `@property`, `@staticmethod`, `@classmethod`, class attributes, and abstract classes with `@abstractmethod`.
- Update SPEC first: the signatures each special method must have, how `__eq__` and `__hash__` interact with dict keys, and what's checked at compile time for abstract classes.
- **Accept:**
  - A `Money` class with `__add__`, `__eq__`, `__hash__` and `__str__` works with `+`, `==`, as a dict key, and in f-strings, matching Python's output.
  - Instantiating an abstract class, or a subclass missing an abstract method, is a compile-time error.

## M29 — Destructuring
- Tuple patterns in `match` (`case (0, y):`), sub-patterns inside class and variant patterns (`Dog(name="rex")`, `Some(Circle(r))`), nested unpacking (`let (a, b), c = …`) and unpacking lists.
- `match` expressions anywhere an expression may appear, and match-expression arms with indented blocks.
- Update SPEC first: exhaustiveness checking with nested patterns, and what unpacking a list of the wrong length does at run time.
- **Accept:**
  - A nested-pattern `match` over an enum of tuples is checked for exhaustiveness, with the missing cases listed in `TP0204`.
  - Unpacking a list of the wrong length raises `ValueError` with Python's message.

## M30 — Parameters
- Keyword-only (`*`) and positional-only (`/`) parameters, `*args` (as a `list`), and `**kwargs` (as a `dict[str, T]`), for functions, methods and constructors.
- Carried through to libraries (M22) and extension modules (M25) where they can be expressed.
- Update SPEC first: how `*args` and `**kwargs` are typed, how they combine with defaults, and how they appear in a library's .NET signature.
- **Accept:**
  - Calls mixing positional, keyword, `*args` and `**kwargs` arguments bind as Python binds them, with Python's errors for a wrong call reported at compile time.
  - An extension module function with keyword-only parameters rejects them positionally, with Python's `TypeError`.

## M31 — Iterators and generators
- Generator functions with `yield`, `Iterator[T]` and `Iterable[T]` types, `for` over user iterators, and lazy `map` and `filter`.
- Update SPEC first: the iterator protocol (`__iter__`, `__next__`), how generators compile, and the change to `map` and `filter` (they returned lists until now), with how existing code that indexes their results is reported.
- **Accept:**
  - An infinite generator consumed by `zip` with a finite list terminates, printing Python's output.
  - Code that indexes the result of `map` gets a compile-time error suggesting `list(…)`.

## M32 — Editor experience
- Language server features beyond M24: completion (names in scope, members after `.`, imports), signature help, find references and rename (across modules), and semantic tokens.
- A VS Code extension in the repository that starts `taipan lsp`, with a TextMate grammar for `.tpn` files.
- Update SPEC first: what completion offers in each context, and what rename refuses (built-in names, .NET and Python names).
- **Accept:**
  - Scripted LSP sessions get completions for a local, a field after `.`, and a module's names, and a rename across two modules edits every reference.
  - The VS Code extension packages with `vsce`, and highlights a sample file as its golden token list says.

## M33 — .NET interop II
- Delegates: passing Taipan functions and lambdas where .NET expects `Func<…>` or `Action<…>`, which unlocks LINQ (`Enumerable.Where`) and callbacks.
- Generic .NET methods with inferred type arguments, `Dictionary<K, V>` as `dict[K, V]`, indexers, operators, and `async` methods (`Task<T>`) awaited with `await`.
- Update SPEC first: how generic methods' type arguments are inferred, how a `Task` is awaited (and whether `async` comes to Taipan functions), and how a Taipan function converts to a delegate.
- **Accept:**
  - `Enumerable.Where(xs, lambda x: x > 2)` and a `Dictionary<string, int>` round trip give the right results.
  - `await HttpClient().GetStringAsync(url)` against a local test server prints the response.

## M34 — Build integration
- An MSBuild project SDK, `TaipanLang.Sdk`, so `.tpn` files build with `dotnet build` as part of a .NET solution, with project references in both directions.
- Libraries packed as NuGet packages (`taipan pack`), with a namespace, assembly name and version set in `taipan.json`.
- Update SPEC first: the SDK's project format, which `taipan.json` keys it reads, and how a Taipan project references a C# project.
- **Accept:**
  - A solution with a Taipan project and a C# project referencing each other builds and tests with `dotnet test`.
  - A packed Taipan library installs from a local feed into a fresh C# project and is called from it.

## M35 — Everyday built-ins II
- Built-in functions `abs`, `min`, `max`, `sum`, `any`, `all`, `round` and `divmod`, the `**` operator with `__pow__`, and the common methods of `str` (`join`, `find`, `count`, `split()` on whitespace, `title`, `isdigit`, …), `list` (`extend`, `remove`, `index`, `count`, `reverse`, `clear`, `copy`, `pop(i)`) and `dict` (`pop`, `setdefault`, `update`, `clear`, `copy`).
- `set[T]`: literals, `in`, `add`, `remove`, `discard`, and the operators `|`, `&`, `-`, `^`, `<=`.
- Update SPEC first: each function's and method's signature and Python behavior, which take a `key=`, set display and equality, and which element types a set takes (as dict keys, §3.1).
- **Accept:**
  - A program using each new built-in, method and set operation prints CPython's output.
  - `max([])` and `min` on an empty iterable raise `ValueError` as in Python, and `min` of a list of a class without `__lt__` is a compile-time error.

## M36 — Comprehensions
- List, dict and set comprehensions with any number of `for` and `if` clauses, and generator expressions, lazy like M31's iterators (`sum(x * x for x in xs)`).
- Update SPEC first: their scoping (each has its own scope, as in Python 3), how their element types are inferred, and how a generator expression as a function's only argument drops its parentheses.
- **Accept:**
  - Nested comprehensions and generator expressions over lists, dicts, strings and generators print CPython's output.
  - A comprehension's variable isn't visible after it (`TP0201`), and an element type that can't be inferred (`[x for x in []]`) is `TP0206`.

## M37 — Programs and I/O
- Program arguments (`sys.argv`, from `import sys`), `sys.exit(code)`, `input()`, and environment variables (`os.environ`).
- Files: `open(path, mode)` giving a file object with `read`, `readline`, `write` and iteration by line, and `with` statements, with context managers as classes defining `__enter__` and `__exit__`.
- Update SPEC first: which parts of `sys` and `os` exist, file encodings and newline handling, and how `with` runs `__exit__` when the block raises.
- **Accept:**
  - A program copies an input file to an output file line by line, upper-casing it, with paths from its arguments, matching CPython.
  - A `with` block that raises still runs `__exit__`, and the exception reaches the caller as in Python.

## M38 — Patterns II
- Or-patterns (`A | B`), guards (`case x if …`, here an arm's `if`), `as` patterns, `None` and float literal patterns, mapping patterns (`{"k": v}`), and `*` in tuple patterns.
- Update SPEC first: the arm syntax for guards, how guards affect exhaustiveness (a guarded arm covers nothing), and what an or-pattern may bind (the same names in each alternative).
- **Accept:**
  - A `match` using each new pattern kind prints CPython's output.
  - A match whose only arm for a case is guarded is non-exhaustive (`TP0204`), and an or-pattern binding different names in its alternatives is a compile-time error.

## M39 — Classes III
- Dataclass options `frozen=True` and `order=True`, `__post_init__`, `@final`, and `Self` outside protocols.
- Special methods `__len__`, `__getitem__`, `__setitem__`, `__contains__`, `__call__`, the in-place operators (`__iadd__` and the like), and `__format__`; `functools.total_ordering`.
- Static and class methods and class attributes on generic classes, and static and class methods as values.
- Update SPEC first: what `frozen` forbids at compile time, the order `order=True` defines, and how each special method is called and checked.
- **Accept:**
  - Assigning a frozen dataclass's field is a compile-time error, and `order=True` dataclasses sort as in Python.
  - A class with `__len__`, `__getitem__` and `__call__` works with `len`, indexing and calls, printing CPython's output.

## M40 — Types II
- Protocols as the types of values, generic protocols, protocol fields, class bounds (`[T: Animal]`), and inheritance involving generic classes.
- Generic functions, built-ins and constructors as values, type aliases inside functions and classes, and an alias's name in diagnostics.
- Narrowing of elements (`xs[0]`) and of `var` paths, narrowing that follows `==` with a literal, `isinstance` on `py` values, and `is` between two objects (identity).
- Update SPEC first: when a class satisfies a protocol used as a type (structurally, as in Python's typing), how a generic function value is instantiated, and what invalidates each new narrowing.
- **Accept:**
  - A `list[Drawable]` holding instances of unrelated classes that satisfy the protocol calls each one's method.
  - A class that doesn't satisfy a protocol it's used as is a compile-time error naming the missing method.

## M41 — Exceptions II
- `raise … from …`, exception chaining (`__cause__`, `__context__`), printing tracebacks for uncaught exceptions as Python does, `args` on exceptions, and dataclass exceptions.
- Update SPEC first: the traceback format (file, line and the line's text, as Python prints them), and which chaining a bare `raise` in an `except` keeps.
- **Accept:**
  - An uncaught chained exception prints a traceback with both exceptions and "The above exception was the direct cause of the following exception", matching CPython's layout.
  - `e.args` and `e.__cause__` give Python's values.

## M42 — Generators and async II
- Generator `send`, `throw` and `close`, `yield` as an expression, generator return values (`StopIteration.value`, `yield from`'s result), `Generator[Y, S, R]`, and `yield` inside `try`/`except`.
- Async generators, `async for`, `async with`, and helpers for running awaitables together (`gather`, `sleep`, timeouts).
- Update SPEC first: how a generator with `send` compiles (C# iterators can't receive values), and the async helpers' module and names.
- **Accept:**
  - A coroutine-style generator driven with `send` prints CPython's output.
  - Two async generators consumed with `async for`, run together with `gather`, interleave as in Python.

## M43 — .NET interop III
- Generic .NET types (`HashSet[str]()`, `Queue[int]`), explicit type arguments on generic methods, extension-method syntax (`xs.Where(f)` for imported extension classes), `ref`/`out` parameters, `params` arrays, and `object` and `decimal` results.
- Deriving from .NET classes and implementing .NET interfaces, raising .NET exceptions, `isinstance`, `as` and `match` on .NET types, flags enums, events, namespace imports (`import dotnet.System.IO as io`), and cancelling a `Task`.
- Update SPEC first: how a generic .NET type is written and imported, how an `out` parameter's value comes back (a tuple, as Python's .NET bridges do), and what a Taipan class deriving from a .NET class may override.
- **Accept:**
  - `HashSet[str]()`, `xs.Where(lambda x: x > 2).Select(str)` and `Int32.TryParse("12")` give the right results.
  - A Taipan class implementing `IComparable<T>` sorts with .NET's `List.Sort`.

## M44 — Tooling II
- A formatter, `taipan fmt` (with `--check`), also served as the language server's formatting, and code actions for common fixes (adding an import, `@override`, `list(…)` around an iterator).
- `taipan test`, which finds and runs `test_*` functions in Taipan files, and test projects in the SDK; completion of .NET namespaces and Python modules' names, and renaming a module (and its file).
- Update SPEC first: the formatter's style (it doesn't change what a program means, and is idempotent), how tests are found and reported, and which diagnostics have code actions.
- **Accept:**
  - Formatting every golden case leaves it unchanged in meaning (the same output) and formatting again changes nothing.
  - `taipan test` runs a file's tests and reports a failing assertion with its line, exiting non-zero.

## M45 — Platforms
- Native builds for other runtimes (`--runtime linux-arm64`, `win-x64`, `osx-arm64`), native libraries callable from C, and size options such as invariant globalization.
- CPython extension modules that export classes and enums as Python types and take Python objects as arguments.
- Update SPEC first: which runtime identifiers are supported and what cross-compiling needs, the C ABI of a native library's exports, and how a Taipan class appears in Python.
- **Accept:**
  - A native build for another runtime identifier produces an executable of that platform (checked by its file header).
  - Python creates an instance of a Taipan class from an extension module and calls its methods.


## M46 — Website
- The language's website, built with Astro Starlight in `website/` and deployed to GitHub Pages: Get started, a Tour of Taipan, Taipan for Python developers, guides (NuGet packages, Python, extension modules, .NET libraries, native builds, SDK projects, testing), and reference (built-ins, the CLI, `taipan.json`, `.tpnproj`, diagnostics, and SPEC.md itself).
- Taipan code is highlighted with the VS Code extension's TextMate grammar.
- A page for every diagnostic code, generated from SPEC §9's table, showing its failing golden case and the error it gives.
- Every `taipan` code block on the site with an `output` block after it is a golden case: `dotnet test` runs it and compares the output.
- **Accept:**
  - The site builds in CI, and every code sample on it compiles, runs and prints its stated output.
  - Every code in SPEC §9 has a page, and a push to main deploys the site.

## M47 — Editors
- The VS Code extension, published to the Visual Studio Marketplace and Open VSX by the publish workflow on a version tag, with trusted publishing and at the compiler's version. It gains an icon, the commands Run File and Restart Language Server, and a message with the install command when `taipan` can't be started.
- Vim and Neovim support in `editors/vim`: filetype detection, a syntax file, and a filetype plugin and indent script (comments, indenting after `:`). Neovim's LSP configuration (`lsp/taipan.lua`) is included too.
- A website page on editors: VS Code, Neovim, Vim, and any other LSP client.
- Update SPEC first: what the extension contributes and how it's published, the Vim syntax groups and what each highlights, and what's still deferred (tree-sitter, Test Explorer).
- **Accept:**
  - The extension packages at the compiler's version with its icon and commands, and its scripted tests show Run File running the file and a missing `taipan` giving the install message.
  - Vim highlights the grammar's sample file as its golden syntax list says, and typing a block indents it.
  - A scripted Neovim session using `lsp/taipan.lua` gets the diagnostics `taipan check` reports.
