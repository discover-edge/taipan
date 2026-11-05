---
title: Your first program
description: Write, check, run and build a Taipan program.
sidebar:
  order: 2
---

A Taipan program is a `.tpn` file. Its top level runs from top to bottom, as a Python script's does.

```taipan title="hello.tpn"
def greet(name: str) -> str:
    return f"Hello, {name}!"

for name in ["Ada", "Grace"]:
    print(greet(name))
```

```output
Hello, Ada!
Hello, Grace!
```

Run it with `taipan run`:

```sh
taipan run hello.tpn
```

## Types are checked first

Before anything runs, the compiler checks the whole program. Function parameters and results are annotated, and everything else is inferred. A mistake is reported with its place and, often, how to fix it, and nothing runs:

```taipan title="hello.tpn"
def greet(name: str) -> str:
    return f"Hello, {name}!"

let count = 3
print(greet(count))
```

```error
hello.tpn:5:13: error TP0200: expected str, found int
```

`taipan check` checks a program without running it, which is what editors and CI usually want. Each error code has a page under [Diagnostics](../../diagnostics/).

## Building

`taipan build` writes a program that runs without the compiler:

```sh
taipan build hello.tpn -o out
dotnet out/hello.dll
```

With `--native`, it's a single native executable that doesn't need .NET installed at all. See [Native builds](../../guides/native/).

## Arguments and input

Arguments after `--` go to the program, as `sys.argv`. Its first element is the program's path, as in Python, so this `count.tpn` counts the rest. Run without arguments, it prints its usage:

```taipan
import sys

if len(sys.argv) < 2:
    print("usage: count <word>...")
else:
    print(len(sys.argv) - 1, "words")
```

```output
usage: count <word>...
```

With arguments, it counts them:

```sh
taipan run count.tpn -- red green blue
```

```output
3 words
```

## Next

Set up [your editor](../editor/). If you know Python, read [Taipan for Python developers](../python-developers/); otherwise, take the [Tour of Taipan](../../tour/basics/).
