---
title: Optionals
description: T?, None, narrowing with is some and is not None, and ??.
sidebar:
  order: 4
---

A value that may be missing has an optional type, `T?`. Only optionals can be `None`, and an optional must be checked before it's used as a `T`. So a missing value can't get as far as an `AttributeError`.

```taipan
def find(names: list[str], prefix: str) -> str?:
    for name in names:
        if name.startswith(prefix):
            return name
    return None

let names = ["ada", "grace", "alan"]
let found = find(names, "gr")
print(found.upper())
```

```error
main.tpn:9:7: error TP0203: str? must be narrowed before use
  help: narrow it with 'is some', or give a default with '??'
```

## Checking an optional

There are three ways to get the `T` out:

```taipan
def find(names: list[str], prefix: str) -> str?:
    for name in names:
        if name.startswith(prefix):
            return name
    return None

let names = ["ada", "grace", "alan"]

# is some binds the value, in that branch only.
if find(names, "gr") is some name:
    print(name.upper())

# is not None narrows a name that can't change.
let first = find(names, "a")
if first is not None:
    print(first.upper())

# ?? gives a default.
print(find(names, "z") ?? "nobody")
```

```output
GRACE
ADA
nobody
```

Narrowing follows the code's paths, as in mypy. After an early `return` or `raise`, the rest of the function sees the narrowed type:

```taipan
def shout(name: str?) -> str:
    if name is None:
        return "..."
    return name.upper() + "!"

print(shout("hey"), shout(None))
```

```output
HEY! ...
```

## Narrowing variables and fields

A `var` stays narrowed until it's assigned again, and a field path such as `self.config.port` stays narrowed until something could change it, like a call. When narrowing ends, the error says why:

```taipan
@dataclass
class Config:
    port: int?

def reset(c: Config):
    c.port = None

def next_port(c: Config) -> int:
    if c.port is None:
        return 8080
    reset(c)
    return c.port + 1
```

```error
main.tpn:12:12: error TP0203: int? must be narrowed before use
  help: the call on line 11 could have changed it; copy it to a 'let' first
```

## Where optionals come from

- `d.get(k)` gives `V?`, `None` for a missing key.
- `int(s)` and `float(s)` give `int?` and `float?`, `None` when the text isn't a number.
- `next(it, None)` gives `T?`.
- A function declared `-> T?`, and a parameter or field declared `T?`.

```taipan
for text in ["42", "4x"]:
    if int(text) is some n:
        print(n * 2)
    else:
        print("not a number:", text)
```

```output
84
not a number: 4x
```

`T?` never nests: a `dict[str, int?]`'s `get` gives `int?`, and a missing key and a stored `None` both give `None`.

Next: [Classes](../classes/).
