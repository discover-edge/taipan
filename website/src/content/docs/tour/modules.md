---
title: Modules
description: Splitting a program into files, imports, and the built-in modules.
sidebar:
  order: 9
---

A program is a main file and the modules it imports. A module is a `.tpn` file beside the main file, and holds declarations only: functions, classes, enums, constants, protocols and type aliases. Only the main file has a top level that runs.

```taipan title="shapes.tpn"
const UNIT = "cm"

enum Shape:
    Circle(radius: float)
    Square(side: float)

def area(s: Shape) -> float:
    match s:
        Circle(r): return 3.14159 * r * r
        Square(side): return side * side
```

```taipan title="main.tpn"
import shapes
from shapes import Circle, Square, area as area_of

let items = [Circle(1), Square(2), shapes.Shape.Square(3)]
for item in items:
    print(item, f"{area_of(item):.2f} {shapes.UNIT}²")
```

```output
Circle(radius=1.0) 3.14 cm²
Square(side=2.0) 4.00 cm²
Square(side=3.0) 9.00 cm²
```

- `import shapes` binds the module; its names are then `shapes.area`, `shapes.Shape`, `shapes.UNIT`.
- `from shapes import area, Shape` binds names directly, and importing an enum binds its variants too.
- `as` renames: `import shapes as s`, `from shapes import area as area_of`.
- A module exports every name it defines. Imports can be cyclic, since definitions are hoisted.

Each module is checked on its own terms, and an error names the file it's in.

## Built-in modules

`sys`, `os`, `types` and `asyncio` are part of Taipan:

```taipan
import sys
from os import environ, getenv

print(len(sys.argv) >= 1, getenv("TAIPAN_DOCS_UNSET") ?? "unset", "PATH" in environ)
```

```output
True unset True
```

- `sys`: `argv`, `exit`, `stdin`, `stdout` and `stderr`.
- `os`: `environ` and `getenv`.
- `types`: `TracebackType`, for `__exit__`.
- `asyncio`: `run`, `gather`, `sleep`, `wait_for` and `timeout`. See [Async](../async/).

Two more roots are reserved: `from python import json` imports a Python module ([Calling Python](../../guides/python/)), and `from dotnet.System.IO import File` imports .NET types ([Using .NET](../../guides/dotnet/)).

Next: [Iterators and generators](../iterators-and-generators/).
