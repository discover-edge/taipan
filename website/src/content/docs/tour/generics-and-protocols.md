---
title: Generics and protocols
description: Generic functions, classes and enums; protocols as interfaces; type aliases.
sidebar:
  order: 8
---

## Generic functions

Type parameters are written in brackets, as in Python 3.12. They're inferred at each call:

```taipan
def first[T](xs: list[T]) -> T?:
    if len(xs) == 0:
        return None
    return xs[0]

def pair_up[A, B](xs: list[A], ys: list[B]) -> list[tuple[A, B]]:
    return list(zip(xs, ys))

print(first([3, 4]), first(["a"]), pair_up([1, 2], ["x", "y"]))
```

```output
3 a [(1, "x"), (2, "y")]
```

A generic body is checked once, for every type it might be given. So inside `first`, a `T` can be stored, passed, compared with `==` and printed, but not added or called: nothing is known about it.

## Generic classes and enums

```taipan
class Stack[T]:
    items: list[T]

    def __init__(self):
        self.items = []

    def push(self, x: T):
        self.items.append(x)

    def pop(self) -> T?:
        if len(self.items) == 0:
            return None
        return self.items.pop()

enum Result[T]:
    Success(value: T)
    Failure(message: str)

def parse(text: str) -> Result[int]:
    match int(text):
        None: return Failure(f"not a number: {text}")
        n: return Success(n)

let s: Stack[str] = Stack()
s.push("a")
s.push("b")
print(s.pop(), s.pop(), s.pop())
print(parse("12"), parse("x"))
```

```output
b a None
Success(value=12) Failure(message="not a number: x")
```

## Protocols

A protocol describes the methods a type must have. A class satisfies it by having them, without declaring it, as in Python's `typing.Protocol`:

```taipan
class Shape(Protocol):
    def area(self) -> float: ...

class Square:
    side: float

    def __init__(self, side: float):
        self.side = side

    def area(self) -> float:
        return self.side * self.side

@dataclass
class Circle:
    radius: float

    def area(self) -> float:
        return 3.14159 * self.radius * self.radius

def total_area(shapes: list[Shape]) -> float:
    return sum(s.area() for s in shapes)

print(total_area([Square(2), Circle(1)]))
```

```output
7.14159
```

A protocol can also bound a type parameter: `def largest[T: Comparable](xs: list[T]) -> T?` takes a list of anything with `<`. `Comparable` is built in; it's satisfied by `int`, `float`, `str`, tuples of those, and classes that define `__lt__`.

## Constraints

A type parameter can instead list the types it may be, and the body is checked for each of them:

```taipan
def total[N: (int, float)](xs: list[N]) -> N:
    var sum: N = xs[0]
    for x in xs[1:]:
        sum = sum + x
    return sum

print(total([1, 2, 3]), total([0.5, 0.25]))
```

```output
6 0.75
```

## Type aliases

`type` names a type, as in Python 3.12:

```taipan
type Grid = list[list[int]]
type Pair[T] = tuple[T, T]

def corners(g: Grid) -> Pair[int]:
    return g[0][0], g[-1][-1]

print(corners([[1, 2], [3, 4]]))
```

```output
(1, 4)
```

Next: [Modules](../modules/).
