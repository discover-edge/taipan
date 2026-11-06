---
title: Taipan for Python developers
description: What's the same as Python, what's stricter, and what's different.
sidebar:
  order: 4
---

Taipan is written in Python's syntax, so most Python code reads the same. The differences come from one decision: **types are checked when you compile, not when a line runs**. This page lists what that changes.

## What stays the same

Indentation, `def`, `class`, `@dataclass`, `for` and `while`, `if`/`elif`/`else`, `try`/`except`/`finally`, `with`, `lambda`, comprehensions, f-strings with format specs, slicing, `*args` and `**kwargs`, generators with `yield`, `async`/`await`, and the built-ins you know: `print`, `len`, `range`, `enumerate`, `zip`, `sorted`, `min`, `max`, `sum`, `any`, `all`, `map`, `filter`, `isinstance`, `open`.

```taipan
@dataclass
class Item:
    name: str
    price: float
    count: int

def total(items: list[Item]) -> float:
    return sum(i.price * i.count for i in items)

let cart = [Item("tea", 4.5, 2), Item("cake", 3.0, 1)]
for i, item in enumerate(cart, start=1):
    print(f"{i}. {item.name:<6}{item.price * item.count:>6.2f}")
print(f"total {total(cart):.2f}")
```

```output
1. tea     9.00
2. cake    3.00
total 12.00
```

`@dataclass`, `@property`, `@override` and the other decorators are built in, so they need no import.

## Variables are declared

A name is declared with `let` (it can't be reassigned) or `var` (it can). Plain assignment only changes an existing variable, so a typo can't silently create a new one:

```taipan
var count = 0
for word in ["a", "b"]:
    cuont = count + 1
```

```error
main.tpn:3:5: error TP0201: undefined name 'cuont'
```

`const` declares a top-level constant. Blocks have their own scope, as in C#, and a name in an inner block can't shadow one in an outer block.

## Functions don't see top-level variables

A top-level `let` or `var` belongs to the script's top level, so a function can't use it, where Python would read a module variable:

```taipan
let limit = 3

def check(n: int) -> bool:
    return n < limit

print(check(2))
```

```error
main.tpn:4:16: error TP0211: 'check' cannot use top-level variable 'limit'
  help: pass it as a parameter, or make it a const
```

Pass the value in as a parameter, or make it a `const`. A `const` is visible everywhere, but its value must be a literal or arithmetic on literals, so it can't hold a list or a dict. For a lookup table, a class attribute works:

```taipan
class Ports:
    BY_NAME: ClassVar[dict[str, int]] = {"api": 8080, "admin": 9000}

def port(name: str) -> int:
    return Ports.BY_NAME.get(name) ?? 80

print(port("api"), port("web"))
```

```output
8080 80
```

It's an ordinary mutable dict, and its value may only be plain list, dict and tuple literals, without enum variants, constructors or sets. For a table that needs those, a function with no parameters that returns it works, though it builds the table again on every call.

## Annotate functions; the rest is inferred

Parameters and results are annotated, as in typed Python. Locals take their type from their initializer. An empty `[]` or `{}` needs a type from somewhere, such as `let names: list[str] = []`.

## No `None` surprises

A value that might be `None` has an optional type, written `T?` (Python's `T | None`). It can't be used as a `T` until you've checked it:

```taipan
let ages = {"ada": 36}
let age = ages.get("bob")
print(age + 1)
```

```error
main.tpn:3:7: error TP0203: int? must be narrowed before use
  help: narrow it with 'is some', or give a default with '??'
```

Check it with `is not None`, bind it with `is some`, or give a default with `??`:

```taipan
let ages = {"ada": 36}
if ages.get("ada") is some age:
    print(age + 1)
print(ages.get("bob") ?? 0)
```

```output
37
0
```

## No truthiness

Conditions must be `bool`. Write what you mean: `if xs:` is `if len(xs) > 0:`, and `if name:` is `if name != "":` or `if name is not None:`.

```taipan
let xs: list[int] = []
if xs:
    print("not empty")
```

```error
main.tpn:2:4: error TP0205: condition must be bool, found list[int]
```

## Enums with data, and exhaustive `match`

`enum` declares a sum type, whose variants may carry fields. A `match` must handle every variant, so adding one shows you every place to update. There's no `case` keyword: each arm is a pattern and a colon.

```taipan
enum Shape:
    Circle(radius: float)
    Rect(width: float, height: float)

def area(s: Shape) -> float:
    match s:
        Circle(r): return 3.14159 * r * r
        Rect(w, h): return w * h

print(area(Rect(2.0, 3.0)))
```

```output
6.0
```

## Numbers

- `int` is 64-bit. Overflow raises `OverflowError` instead of growing.
- `/` gives a `float` and `//` an `int`, as in Python. An `int` widens to a `float` where one is expected, and nothing else converts implicitly: use `int(x)`, `float(x)` and `str(x)`.
- `int("42")` and `float("2.5")` give an optional, `None` when the text isn't a number, rather than raising.

## Smaller differences

- **Defaults are evaluated on every call**, so `def f(xs: list[int] = [])` gets a new list each time. Python's shared-default trap doesn't exist.
- **A nested function needs `nonlocal` to assign to an outer variable**, as in Python, but a new local of the same name isn't possible, so forgetting it is a compile error (`TP0236`). Top-level variables have no `global`: a nested function can read them but not assign to them.
- **Each loop iteration has its own variable**, so `[lambda: i for i in range(3)]` gives functions returning 0, 1 and 2.
- **No conditional expression**: `a if c else b` is written as an `if` statement or a `match` expression.
- **Strings in containers print with double quotes**: `print(["a"])` is `["a"]`.
- **An `int` that widens to a `float` is a `float`**: `let x: float = 1` prints `1.0`, and `[1, 2.5]` prints `[1.0, 2.5]`.
- **Calling an async function starts it**, as a .NET task starts, rather than waiting to be awaited.
- **`keys()`, `values()` and `items()` give new lists**, not live views.
- **Exception messages are Taipan's own**, so `str(e)` can differ from Python's.
- **Infinite recursion ends the process**, as in .NET, and can't be caught, so there's no `RecursionError`. Recursion can go much deeper than Python's limit of 1,000.
- **Lists are invariant**: a `list[Dog]` isn't a `list[Animal]`, since code holding it as one could add a `Cat`.
- **Imports are of Taipan modules**, files beside the main file. Python modules are imported with `from python import json` and .NET namespaces with `from dotnet.System.IO import File`.
- **No dynamic features**: no `eval`, `setattr`, monkey-patching or multiple inheritance. Protocols cover what multiple inheritance usually does.

## Using Python libraries

Python packages are still there, through `from python import …`. Their values have the type `py`, which Python checks at run time, and you convert them back with `as`:

```taipan
from python import json

let data = json.loads("{\"ok\": true, \"items\": [1, 2, 3]}")
let items = data["items"] as list[int]
print(sum(items))
```

```output
6
```

See [Calling Python](../../guides/python/).
