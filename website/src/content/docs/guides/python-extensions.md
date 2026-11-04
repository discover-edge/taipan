---
title: Python extension modules
description: Compile Taipan into a native CPython module that Python imports.
sidebar:
  order: 4
---

`taipan build --python-extension` compiles a Taipan module into a native CPython extension module. Python imports it like any other module, and needs no .NET runtime to do so.

```taipan title="shapes.tpn"
const PI = 3.14159

class NegativeRadius(ValueError):
    pass

@dataclass
class Point:
    x: float
    y: float

    def moved(self, dx: float) -> Point:
        return Point(self.x + dx, self.y)

enum Kind:
    Round
    Square

def area(r: float) -> float:
    if r < 0.0:
        raise NegativeRadius(f"radius {r} is negative")
    return PI * r * r

def apply(f: Callable[[float], float], x: float) -> float:
    return f(x)
```

```sh
taipan build shapes.tpn --python-extension -o out
```

```python
import shapes

print(shapes.area(2.0))                 # 12.56636
p = shapes.Point(1.0, 2.0)
print(p.moved(2.0), p.x)                # Point(x=3.0, y=2.0) 1.0
print(shapes.Kind.Round)                # Round
print(shapes.apply(lambda x: x * 10, 4.0))  # 40.0
try:
    shapes.area(-1.0)
except ValueError as e:
    print(type(e).__name__, e)          # NegativeRadius radius -1.0 is negative
```

## The module

- It's `out/shapes.abi3.so` (`shapes.pyd` on Windows), built with NativeAOT. It uses CPython's stable ABI, so one build imports into any CPython from 3.10 on, on the platform it was built for. Python isn't needed to build it.
- Building needs what [native builds](../native/) need: the .NET SDK and a C toolchain. `--runtime` builds for another architecture.
- The module holds declarations only, and runs natively, so it can't import Python modules itself.

## What Python sees

- **Functions** are Python functions, taking positional and keyword arguments, defaults, `*args` and `**kwargs` as Taipan declares them. A wrong call raises Python's own `TypeError`.
- **Arguments** are converted to their Taipan types, or raise `TypeError` (`area() argument 'r' must be float, not str`). `int`, `float`, `bool`, `str`, `None`, lists, dicts, tuples and optionals convert, and so do the module's classes and enums, Python callables (for function types), and any Python object (for `py`).
- **Constants** are module attributes.
- **Classes** are Python types: constructors, fields as attributes, properties, methods, `str`, `repr`, `==` and `hash`, and inheritance between them.
- **Enums** are Python types whose variants are their values.
- **Exceptions** raise Python exceptions: Taipan's built-in classes as Python's built-ins, and the module's classes as Python classes deriving from them, so `except ValueError` catches `NegativeRadius`.

A definition that can't be exported, such as a generic or async function, is warning [TP0302](../../diagnostics/tp0302/), saying why.
