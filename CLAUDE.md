# CLAUDE.md — Taipan compiler

## Source of truth
- **SPEC.md defines the language.** Code implements the spec, never the other way around.
- If the spec is ambiguous, contradictory, or missing a rule, **stop and propose a SPEC.md edit first**. Once it's agreed, implement it. Don't invent semantics in code.
- **ROADMAP.md defines the order.** Work on exactly one milestone at a time, and don't implement features from later milestones, even partially.
- New diagnostic codes go into the SPEC §9 table before they're used.

## Stack
- .NET 10, C# latest, `<Nullable>enable</Nullable>`, `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` (compiler code only, not generated code).
- Roslyn (`Microsoft.CodeAnalysis.CSharp`) for compiling emitted C#.
- xUnit for tests. No other dependencies without asking. pythonnet is approved for M10 only.

## Layout
```
src/
  Taipan.Compiler/
    Text/          SourceText, spans, line/col mapping
    Lexing/        Lexer, Token, TokenKind
    Syntax/        AST (records), Parser
    Semantics/     Binder, Scope, Symbols, TypeChecker, Types
    Emit/          CSharpEmitter, RoslynCompiler
    Diagnostics/   Diagnostic, DiagnosticBag, codes
  Taipan.Runtime/   Ops, Display, Builtins, Panic (referenced by compiled programs)
  Taipan.Cli/       `taipan` entry point
tests/
  Taipan.Tests/     unit tests + golden test runner
  cases/<milestone>/<name>.tpn   (+ .out / .err / .exit sidecars)
examples/
```

## Architecture rules
- **Pipeline:** `SourceText → Lexer → Parser → AST → Binder/TypeChecker → bound tree → CSharpEmitter → Roslyn → run`.
- Each stage takes immutable input, produces new output, and never mutates earlier stages.
- **AST nodes** are `sealed record`s with a `TextSpan`. **Bound nodes** carry resolved `TaipanType` and `Symbol`, and the emitter works from the bound tree only.
- The parser is hand-written recursive descent, with one method per grammar production in SPEC §2, named to match.
- **Diagnostics** are collected in a `DiagnosticBag`, never thrown. Exceptions mean compiler bugs.
- Keep it boring: no DI containers, no reflection, no source generators, minimal interfaces. Pattern matching over the visitor pattern.

## CLI
```
taipan check <file>        type-check only; print diagnostics
taipan emit  <file>        print generated C#
taipan build <file> -o <dir>
taipan run   <file> [-- args]
```
Exit codes follow SPEC §9.

## Testing
- **Golden tests** are the primary signal. For each `tests/cases/**/*.tpn`, and each `examples/*.tpn` that has a `.out`, the runner:
  - runs `taipan run` (or `check` if there's no `.out`)
  - passes the case path relative to the repo root, so diagnostics read `tests/cases/<milestone>/<name>.tpn:...`
  - compares stdout to `.out`, stderr to `.err` (exact text; no `.err` means stderr must be empty), and the exit code to `.exit` (default 0, or 1 when `.err` exists)
  - compares `taipan emit` output to `.cs`, when there is one
- **Multi-file cases** live in their own directory, `tests/cases/<milestone>/<case>/main.tpn` plus its modules. In a directory with a `main.tpn`, only that file is a case, and its sidecars sit next to it (`main.out`, `main.err`, …).
- Every feature and every diagnostic code lands with at least one passing and one failing golden case.
- Lexer and parser also get focused unit tests.
- `dotnet test` must be green before any milestone is called done.

## Commands
```
dotnet build
dotnet test
dotnet run --project src/Taipan.Cli -- run examples/health.tpn
```

## Workflow per milestone
1. Re-read the relevant SPEC sections and the milestone's acceptance criteria.
2. Write the golden cases first (spec-driven: tests encode the spec).
3. Implement until green.
4. Summarize what was built, any spec questions raised, and anything deferred.
