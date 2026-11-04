---
title: Errors
description: Exceptions, try/except/finally, tracebacks and with.
sidebar:
  order: 7
---

Taipan's exceptions are Python's: classes deriving from `Exception`, raised with `raise` and handled with `try`. Every run-time error raises one, so it can be caught.

## Raising and catching

```taipan
class NotFound(Exception):
    key: str

    def __init__(self, key: str):
        self.key = key
        super().__init__(f"no service named {key!r}")

def lookup(ports: dict[str, int], name: str) -> int:
    try:
        return ports[name]
    except KeyError:
        raise NotFound(name)

let ports = {"api": 8080}
for name in ["api", "db"]:
    try:
        print(name, lookup(ports, name))
    except NotFound as e:
        print("error:", e, "key:", e.key)
    finally:
        print("checked", name)
```

```output
api 8080
checked api
error: no service named "db" key: db
checked db
```

`except (KeyError, IndexError) as e` catches several classes, binding `e` as their nearest common base. `try` takes `else`, which runs when nothing was raised, and `raise … from …` chains exceptions as in Python.

The built-in classes follow Python's hierarchy: `ArithmeticError` (with `ZeroDivisionError` and `OverflowError`), `LookupError` (with `IndexError` and `KeyError`), `ValueError`, `TypeError`, `RuntimeError`, `OSError` and its subclasses, and others.

## Checked by the compiler

A clause that can never run is a warning, and so is a bare `except:` that throws the exception away:

```taipan
try:
    print(1 // 0)
except ArithmeticError:
    print("arithmetic")
except ZeroDivisionError:
    print("zero")
```

```output
arithmetic
```

```error
main.tpn:5:8: warning TP0300: unreachable except clause
  help: 'ArithmeticError' is already caught above
```

## Uncaught exceptions

An exception nothing catches prints a traceback, as CPython does, and the program exits with code 101:

```taipan
def average(xs: list[int]) -> float:
    return sum(xs) / len(xs)

print(average([1, 2, 3]))
print(average([]))
```

```output
2.0
```

```error
Traceback (most recent call last):
  File "main.tpn", line 5, in <module>
    print(average([]))
  File "main.tpn", line 2, in average
    return sum(xs) / len(xs)
ZeroDivisionError: division by zero
```

`sys.exit(code)` raises `SystemExit`, which ends the program with that code and no traceback.

## `with`

`with` runs a context manager's `__enter__` and `__exit__`, so cleanup happens however the block ends. Files are context managers:

```taipan
import os

let path = os.getenv("TMPDIR", "/tmp") + "/taipan-docs-with.txt"
with open(path, "w") as f:
    f.write("first\nsecond\n")
with open(path) as f:
    for line in f:
        print(line.strip())
```

```output
first
second
```

A class becomes a context manager by defining `__enter__` and `__exit__`, whose parameters are typed as in Python's `typing`:

```taipan
from types import TracebackType

class Timer:
    label: str

    def __init__(self, label: str):
        self.label = label

    def __enter__(self) -> Timer:
        print("start", self.label)
        return self

    def __exit__(self, kind: type[BaseException]?, error: BaseException?, tb: TracebackType?) -> bool:
        print("stop", self.label)
        return False

with Timer("load"):
    print("loading")
```

```output
start load
loading
stop load
```

Next: [Generics and protocols](../generics-and-protocols/).
