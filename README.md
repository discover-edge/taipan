# Taipan

A statically typed, compiled language with Python's syntax. The compiler is written in C#/.NET 10, transpiles to C#, and compiles in-process with Roslyn. Python libraries are reachable through a checked boundary (planned for M10).

```python
def find(services: list[Service], name: str) -> Service?:
    for s in services:
        if s.name == name:
            return s
    return none
```

## Files
- `SPEC.md`: the language definition and source of truth.
- `ROADMAP.md`: milestones M0–M10 with acceptance criteria.
- `CLAUDE.md`: instructions for Claude Code covering architecture, testing, and workflow.
- `examples/`: target programs that milestones must make run.

## Getting started with Claude Code
```
claude
> Read CLAUDE.md, SPEC.md and ROADMAP.md, then implement M0.
```
After each milestone: review the summary, answer any spec questions, then move on to the next.

## Name
Taipan: a snake, as a nod to Python, and a strict one. Source files use `.tpn` and diagnostic codes use the `TP` prefix.

Before publishing, confirm the GitHub org, the NuGet IDs (`Taipan.Compiler`, `Taipan.Runtime`), and a domain are available.
