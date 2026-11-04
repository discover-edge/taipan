---
title: Enums and match
description: Sum types with data, and exhaustive pattern matching.
sidebar:
  order: 6
---

## Enums

An `enum` lists the forms a value can take. Each variant may carry named fields:

```taipan
enum Health:
    Ok
    Degraded(healthy: int, total: int)
    Down

let checks = [Ok, Degraded(2, 3), Down, Health.Degraded(healthy=0, total=1)]
print(checks)
print(Degraded(2, 3) == Degraded(2, 3))
```

```output
[Ok, Degraded(healthy=2, total=3), Down, Degraded(healthy=0, total=1)]
True
```

Variants are used by name, or qualified as `Health.Ok` when two enums share a variant name. Enums compare structurally, and their fields are reached through `match`. Enums can be generic: `enum Result[T]: Success(value: T)`, `Failure(message: str)`.

## `match`

`match` picks the first arm whose pattern fits. There's no `case` keyword: each arm is a pattern, a colon, and a statement or an indented block.

```taipan
enum Health:
    Ok
    Degraded(healthy: int, total: int)
    Down

def describe(h: Health) -> str:
    match h:
        Ok:
            return "all good"
        Degraded(healthy, total) if healthy * 2 >= total:
            return f"mostly up ({healthy}/{total})"
        Degraded(_, total):
            return f"mostly down (of {total})"
        Down:
            return "down"

for h in [Ok, Degraded(2, 3), Degraded(1, 4), Down]:
    print(describe(h))
```

```output
all good
mostly up (2/3)
mostly down (of 4)
down
```

## Exhaustiveness

A `match` must handle every value its subject can have. Leave one out and the compiler says which:

```taipan
enum Health:
    Ok
    Degraded(healthy: int, total: int)
    Down

def describe(h: Health) -> str:
    match h:
        Ok: return "all good"
        Degraded(up, total): return f"{up}/{total}"
```

```error
main.tpn:7:5: error TP0204: non-exhaustive match (missing: Down)
```

So when you add a variant, the compiler finds every `match` that needs a new arm. `_` matches anything, for when you really mean "everything else".

## Patterns

Patterns nest, as in Python's `match`:

| Pattern | Matches |
|---|---|
| `0`, `"api"`, `True`, `1.5`, `None` | a value equal to the literal |
| `x` | anything, binding it to `x` |
| `_` | anything |
| `Degraded(h, t)` | a variant, with sub-patterns for its fields |
| `Point(x=0, y=y)`, `Point(0, y)` | an instance of a class, with sub-patterns for its fields |
| `(a, b)`, `[first, *rest]` | a tuple, or a list, by its elements |
| `{"name": n}` | a dict with those keys |
| `A \| B` | either pattern |
| `p as name` | `p`, binding the whole value |
| `p if condition` | `p`, when the guard holds |

```taipan
def classify(command: list[str]) -> str:
    match command:
        []:
            return "nothing"
        ["go", ("north" | "south") as direction]:
            return f"walk {direction}"
        ["take", item, *rest] if len(rest) == 0:
            return f"pick up {item}"
        [verb, *_]:
            return f"don't know how to {verb}"

let commands: list[list[str]] = [[], ["go", "north"], ["take", "lamp"], ["take", "a", "b"], ["dance"]]
for c in commands:
    print(classify(c))
```

```output
nothing
walk north
pick up lamp
don't know how to take
don't know how to dance
```

## `match` expressions

`match` is also an expression, whose arms are values. It's Taipan's conditional expression too:

```taipan
enum Health:
    Ok
    Degraded(healthy: int, total: int)
    Down

let h = Degraded(1, 4)
let label = match h:
    Ok: "up"
    Degraded(healthy, total): f"{healthy}/{total} up"
    Down: "down"
print(label)

let n = 7
let parity = match n % 2 == 0:
    True: "even"
    False: "odd"
print(parity)
```

```output
1/4 up
odd
```

An arm may also `raise`, as in `_: raise ValueError("unknown")`.

Next: [Errors](../errors/).
