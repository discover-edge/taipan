---
title: Basics
description: Values, variables, strings, operators and control flow.
sidebar:
  order: 1
---

## Values and types

Taipan's basic types are `int` (64-bit), `float`, `bool`, `str` and `None`. Every value has a type, known when the program compiles.

```taipan
let answer = 42
let ratio = 0.75
let ready = True
let name = "Taipan"
print(answer, ratio, ready, name)
```

```output
42 0.75 True Taipan
```

## `let`, `var` and `const`

- `let` declares a name that can't be reassigned.
- `var` declares one that can.
- `const` declares a top-level constant, visible everywhere, whose value is computed from literals.

```taipan
const LIMIT = 3

var total = 0
for i in range(LIMIT):
    total += i
let message = f"total is {total}"
print(message)
```

```output
total is 3
```

A type annotation is optional on `let` and `var`, and needed when the initializer doesn't say enough, as with an empty list:

```taipan
var names: list[str] = []
names.append("ada")
let limit: float = 10
print(names, limit)
```

```output
["ada"] 10.0
```

Each block has its own scope. A name is visible from its declaration to the end of its block, and a name in an inner block can't shadow one from an outer block.

## Numbers

`int` arithmetic is checked: overflow raises `OverflowError` rather than wrapping. `/` always gives a `float`, `//` and `%` floor as in Python, and `**` is a power.

```taipan
print(7 / 2, 7 // 2, -7 // 2, 7 % 3, 2 ** 10)
print(1_000_000 * 3, 0.1 + 0.2)
print(int(3.9), float(2), round(2.5), abs(-4))
```

```output
3.5 3 -4 1 1024
3000000 0.30000000000000004
3 2.0 2 4
```

An `int` is accepted where a `float` is expected. No other conversion is implicit.

## Strings

Strings are immutable Unicode text, in `"…"` or `'…'`. F-strings take expressions and Python's format specs:

```taipan
let lang = "taipan"
let version = 0.4
print(lang.upper(), len(lang), lang[0], lang[-2:])
print(f"{lang!r} {version:.2f} {42:>5} {1234567:,}")
print(f"{lang=}")
print(", ".join(["a", "b", "c"]), "a,b".split(","))
```

```output
TAIPAN 6 t an
"taipan" 0.40    42 1,234,567
lang="taipan"
a, b, c ["a", "b"]
```

Inside containers, and with `!r`, strings are written with double quotes.

## Conditions

`if`, `elif`, `else` and `while` work as in Python, and a condition must be a `bool`. `and`, `or` and `not` take `bool`s too.

```taipan
def describe(n: int) -> str:
    if n < 0:
        return "negative"
    elif n == 0:
        return "zero"
    elif n % 2 == 0 and n < 100:
        return "small and even"
    else:
        return "other"

for n in [-5, 0, 8, 7]:
    print(n, describe(n))
```

```output
-5 negative
0 zero
8 small and even
7 other
```

Comparisons don't chain: write `0 < x and x < 10` rather than `0 < x < 10`.

## Loops

`for` goes over anything iterable: lists, strings, dicts, ranges and generators. `while` repeats while its condition holds, and `break` and `continue` work as in Python.

```taipan
for i in range(10, 0, -3):
    print(i, end=" ")
print()

var n = 27
var steps = 0
while n != 1:
    if n % 2 == 0:
        n = n // 2
    else:
        n = 3 * n + 1
    steps += 1
print(steps)
```

```output
10 7 4 1 
111
```

## Printing and input

`print` takes Python's `sep`, `end`, `file` and `flush` arguments; `print(x, file=sys.stderr)` writes to standard error, after `import sys`. `input()` reads a line from standard input.

```taipan
print("a", "b", sep="-")
print("no newline", end="")
print()
```

```output
a-b
no newline
```

There's no conditional expression (`a if c else b`). Use an `if` statement, or a [`match` expression](../enums-and-match/#match-expressions):

```taipan
let n = 7
let parity = match n % 2 == 0:
    True: "even"
    False: "odd"
print(parity)
```

```output
odd
```

Next: [Collections](../collections/).
