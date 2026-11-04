---
title: Calling Python
description: Use Python packages from Taipan through a checked boundary.
sidebar:
  order: 2
---

Taipan programs can import Python modules, from the standard library or any installed package. Python's values have the type `py`, which Python checks at run time, and are converted to Taipan's types with `as`.

## Importing Python modules

```taipan
from python import json, statistics

let data = json.loads("{\"name\": \"api\", \"latencies\": [12, 15, 11, 30]}")
let name = data["name"] as str
let latencies = data["latencies"] as list[int]
print(name, statistics.median(latencies), max(latencies))
print(json.dumps({"ok": True, "count": len(latencies)}, sort_keys=True))
```

```output
api 13.5 30
{"count": 4, "ok": true}
```

- `from python import json, os as system` imports Python modules, as Python's `import` does.
- A `py` value supports what Python does: attributes, calls with keyword arguments, indexing, operators, `in`, `len` and `for`. Python checks each one when it runs.
- Every operation on a `py` gives a `py`, comparisons included. Use `as bool` to test one in a condition.

## Converting values

Taipan values convert to Python when they're passed in: `int`, `float`, `bool`, `str`, `None`, and lists, dicts, tuples and optionals of those. Lists and dicts are copied.

A `py` becomes a Taipan value only through `as`. `x as T` converts or raises `TypeError`, and `x as T?` gives `None` instead:

```taipan
from python import json

let value = json.loads("[1, 2, \"three\"]")
print(value[0] as int, value[2] as str)
print(value as list[int]?)
print(value as list[int])
```

```output
1 three
None
```

```error
Traceback (most recent call last):
  File "main.tpn", line 6, in <module>
    print(value as list[int])
TypeError: cannot convert Python str to list[int]
```

A conversion is all or nothing: one element that doesn't fit, here the `str`, fails the whole conversion.

## Python exceptions

An exception raised in Python reaches Taipan as a `PythonError`, and can be caught by its Python class:

```taipan
from python import json

try:
    json.loads("{not json")
except json.JSONDecodeError as e:
    print("bad input:", e)
```

```output
bad input: Expecting property name enclosed in double quotes: line 1 column 2 (char 1)
```

## Finding Python

Taipan embeds CPython 3.10 or later. It uses the library named by the `PYTHONNET_PYDLL` environment variable, or else the one belonging to the first `python3` or `python` on your `PATH`, so an activated virtual environment's packages are available. A program that never imports Python never loads it.

## The other direction

To call Taipan from Python, build a Taipan module as a Python extension module: see [Python extension modules](../python-extensions/).
