---
title: Functions
description: Parameters, defaults, keyword arguments, lambdas and closures.
sidebar:
  order: 3
---

## Declaring functions

A function's parameters are annotated, and so is its result, unless it returns nothing. Every path must return a value of the declared type.

```taipan
def clamp(x: int, lo: int, hi: int) -> int:
    if x < lo:
        return lo
    if x > hi:
        return hi
    return x

def report(name: str, value: int):
    print(f"{name}: {value}")

report("clamped", clamp(15, 0, 10))
```

```output
clamped: 10
```

Top-level functions are hoisted, so they can be called before they're defined and can call each other.

## Defaults and keywords

Arguments may be passed by position or by name, and parameters may have defaults. A default is evaluated on each call that leaves it out, so a list default is a new list every time.

The `match` expression below picks the scheme, since Taipan has no `a if c else b`.

```taipan
def connect(host: str, port: int = 8080, secure: bool = False) -> str:
    let scheme = match secure:
        True: "https"
        False: "http"
    return f"{scheme}://{host}:{port}"

print(connect("localhost"))
print(connect("example.com", secure=True, port=443))

def tags(xs: list[str] = []) -> list[str]:
    xs.append("new")
    return xs

print(tags(), tags())
```

```output
http://localhost:8080
https://example.com:443
["new"] ["new"]
```

A wrong argument is a compile error, not a `TypeError` at run time:

```taipan
def connect(host: str, port: int = 8080) -> str:
    return f"{host}:{port}"

print(connect("localhost", prot=80))
```

```error
main.tpn:4:28: error TP0218: 'connect' has no parameter 'prot'
```

## `*args`, `**kwargs`, `/` and `*`

Python's parameter kinds all work: positional-only parameters before `/`, keyword-only ones after `*`, and `*args` and `**kwargs`, annotated with their element type.

```taipan
def total(*xs: float) -> float:
    return sum(xs)

def tag(name: str, /, *, sep: str = "=", **attrs: str) -> str:
    return name + " " + " ".join(f"{k}{sep}{v}" for k, v in attrs.items())

print(total(1, 2, 3.5))
print(tag("a", href="/", title="home"))
let nums = [1.0, 2.0]
print(total(*nums))
```

```output
6.5
a href=/ title=home
3.0
```

## Functions as values

A function's type is written `Callable[[A, B], R]`. Top-level functions, nested functions and lambdas are values that can be passed, stored and returned.

```taipan
def apply_twice(f: Callable[[int], int], x: int) -> int:
    return f(f(x))

def double(n: int) -> int:
    return n * 2

print(apply_twice(double, 3))
print(apply_twice(lambda n: n + 10, 3))

let ops: dict[str, Callable[[int, int], int]] = {
    "add": lambda a, b: a + b,
    "mul": lambda a, b: a * b,
}
print(ops["mul"](6, 7))
```

```output
12
23
42
```

A lambda's parameter types come from where it's used, so it needs no annotations.

## Closures

Nested functions and lambdas capture the variables around them. A captured `var` is shared, so the function sees later changes and may change it itself. There's no `nonlocal`: assignment never declares, so it's not needed.

```taipan
def counter() -> Callable[[], int]:
    var count = 0
    def step() -> int:
        count += 1
        return count
    return step

let tick = counter()
tick()
tick()
print(tick())

let fs = [lambda: i for i in range(3)]
print([f() for f in fs])
```

```output
3
[0, 1, 2]
```

Each loop iteration has its own variable, so each lambda above sees its own `i`, where in Python all three would see `2`.

Methods aren't values by themselves: wrap them in a lambda, as in `lambda s: s.strip()`.

Next: [Optionals](../optionals/).
