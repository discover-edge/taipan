---
title: The taipan command
description: Every taipan command and option, and its exit codes.
sidebar:
  order: 1
---

| Command | What it does |
|---|---|
| `taipan run <file> [-- args]` | Compile and run a program, passing `args` to it as `sys.argv[1:]` |
| `taipan check <file> [<kind>]` | Type-check only, printing diagnostics |
| `taipan build <file> [<kind>] -o <dir>` | Write a runnable program, library, native executable or extension module to `<dir>` |
| `taipan emit <file> [<kind>]` | Print the C# the program compiles to |
| `taipan test [<path>…]` | Run test functions ([Testing](../../guides/testing/)) |
| `taipan fmt [--check] <path>…` | Format files, or check that they're formatted |
| `taipan pack <file> -o <dir>` | Pack a library as a NuGet package |
| `taipan lsp [--stdio]` | Run the language server on stdin and stdout |
| `taipan compile …` | Compile one assembly; used by `TaipanLang.Sdk` |
| `taipan --version` | Print the version |
| `taipan --help` | Print the usage text |

## Kinds

`check`, `emit` and `build` take what to build:

| Option | Builds | Guide |
|---|---|---|
| *(none)* | a program, `<dir>/<name>.dll`, run with `dotnet` | |
| `--library` | a .NET library, `<dir>/<name>.dll` | [Building .NET libraries](../../guides/dotnet-libraries/) |
| `--native` | a native executable, `<dir>/<name>` | [Native builds](../../guides/native/) |
| `--library --native` | a C library and header, `<dir>/<name>.so` and `<name>.h` | [Native builds](../../guides/native/#c-libraries) |
| `--python-extension` | a CPython extension module, `<dir>/<name>.abi3.so` | [Python extension modules](../../guides/python-extensions/) |

With `--native` or `--python-extension`, `build` also takes:

| Option | Effect |
|---|---|
| `--runtime <rid>` | Build for `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`, `win-x64` or `win-arm64` |
| `--invariant-globalization` | Leave out culture data |
| `--optimize-size` | Prefer smaller code to faster code |

## Exit codes

| Code | Meaning |
|---|---|
| `0` | success |
| `1` | a compile error (or, for `test`, a failing test; for `fmt --check`, an unformatted file) |
| `2` | a usage or I/O error: bad arguments, a missing file, a failed package restore or native build |
| `101` | an uncaught exception, which prints a traceback |

A program that calls `sys.exit(n)` exits with `n`.

## Diagnostics

Diagnostics go to stderr, as `<file>:<line>:<col>: error TP<code>: <message>`, with an optional `help:` line under it. Warnings say `warning` instead, and don't stop compilation. Every code is listed under [Diagnostics](../../diagnostics/).
