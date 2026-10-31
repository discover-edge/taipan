# Roadmap

Each milestone is done when **all** of its acceptance criteria pass as golden tests (see CLAUDE.md). Milestones are sequential. Don't start the next one early.

M0–M10 (with M8.1) make up v0.1 and are complete. M11 onward are planned.

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
- Tuple types, literals and unpacking: `def bounds(xs: list[int]) -> (int, int)`, `let lo, hi = bounds(xs)`, `for i, x in enumerate(xs)`.
- Slicing for `list` and `str`: `xs[1:3]`, `xs[::-1]`, `s[:5]`, with Python's clamping.
- Update SPEC first: tuple display, equality, use as dict keys, and how tuples meet `match` and Python's `tuple`.
- **Accept:**
  - Unpacking with the wrong count is a compile error, and `xs[a:b:0]` panics.
  - Slices are new lists, matching Python for negative and out-of-range bounds.

## M13 — Exceptions
- `try`/`except`/`finally` and `raise`, with Taipan's runtime errors (index, key, overflow…) as catchable exception types.
- Python exceptions from `py` operations become catchable instead of panicking.
- Update SPEC first: the exception types, what is and isn't catchable, and how uncaught exceptions keep §7's panic output and exit code 101.
- **Accept:**
  - Catching a missing dict key, an overflow, and a Python `json.JSONDecodeError` each work.
  - An uncaught exception still prints `panic: …` with the `.tpn` line and exits 101.

## M14 — Protocols
- Structural interfaces (`protocol Comparable: def less(self, other: Self) -> bool`) that structs satisfy without declaring it.
- Protocols as generic bounds (`def largest[T: Comparable](xs: list[T]) -> T?`), complementing M8.1's constraint tuples.
- Update SPEC first: matching rules, what built-in types satisfy, and error messages for unmet bounds.
- **Accept:**
  - `largest` works for a user struct and for `int`/`str` through their built-in protocols.
  - A struct missing a method reports which one at the call site.

## Later
- `match` as an expression, an LSP server, NativeAOT publishing, packaging `taipan` as a `dotnet tool` (with the NuGet IDs settled), and CPython extension output.
