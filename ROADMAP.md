# Roadmap

Each milestone is done when **all** of its acceptance criteria pass as golden tests (see CLAUDE.md). Milestones are sequential. Don't start the next one early.

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

## M9 — Modules
- Multi-file programs: `import other` and `from other import name`.
- The spec section is written at the start of this milestone.

## M10 — Python interop (level 1)
- The SPEC §10 sketch, finalized into real spec text first, then implemented with pythonnet.
- **Accept:**
  - Import `json` from the Python stdlib, parse a string, and extract an `int` via `as int?`.
  - A failed `as int` panics with exit 101.

## Later
- Protocols, lambdas, tuples, slicing, match expressions, exceptions, an LSP server, NativeAOT, and CPython extension output.
