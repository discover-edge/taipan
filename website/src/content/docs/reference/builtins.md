---
title: Built-ins
description: Built-in functions, methods, modules and exception classes.
sidebar:
  order: 2
---

Taipan's built-ins follow Python's names and behavior, with types. The [specification](../specification/#6-built-ins-v01) gives every rule.

## Functions

| Function | Notes |
|---|---|
| `print(*args, sep=" ", end="\n", file=sys.stdout, flush=False)` | |
| `input()`, `input(prompt)` | raises `EOFError` at the end of input |
| `open(file, mode="r", *, encoding="utf-8", newline=None)` | text files; modes `r`, `w`, `a`, `x` |
| `len(x)` | lists, dicts, sets and strings (in code points) |
| `range(stop)`, `range(start, stop[, step])` | in `for` loops |
| `str(x)`, `repr(x)`, `hash(x)` | |
| `int(x)`, `float(x)` | from a number; from a `str`, an optional: `None` if it isn't a number |
| `abs`, `round`, `divmod`, `min`, `max`, `sum` | `min` and `max` take `key=` |
| `sorted(xs, key=…, reverse=False)` | stable |
| `any(xs)`, `all(xs)` | of `bool`s |
| `map`, `filter`, `zip`, `enumerate` | lazy iterators |
| `list(xs)`, `set(xs)`, `iter(xs)`, `next(it[, default])`, `anext(it[, default])` | |
| `isinstance(x, C)`, `isinstance(x, (A, B))` | narrows `x` |
| `super()` | as `super().method(…)` |

## Methods

- **`list[T]`**: `append`, `extend`, `insert`, `pop`, `remove`, `index`, `count`, `sort`, `reverse`, `clear`, `copy`, `contains`; and, since a list is a .NET `List<T>`, its methods too.
- **`dict[K, V]`**: `get`, `keys`, `values`, `items`, `pop`, `setdefault`, `update`, `remove`, `clear`, `copy`.
- **`set[T]`**: `add`, `remove`, `discard`, `pop`, `union`, `intersection`, `difference`, `issubset` and the others, and the operators `|`, `&`, `-`, `^`.
- **`str`**: `upper`, `lower`, `strip`, `lstrip`, `rstrip`, `split`, `rsplit`, `splitlines`, `join`, `replace`, `find`, `rfind`, `index`, `count`, `startswith`, `endswith`, `partition`, `rpartition`, `removeprefix`, `removesuffix`, and more, as in Python.

## Modules

| Module | Names |
|---|---|
| `sys` | `argv`, `exit`, `stdin`, `stdout`, `stderr` |
| `os` | `environ`, `getenv` |
| `types` | `TracebackType` |
| `asyncio` | `run`, `gather`, `sleep`, `wait_for`, `timeout` |

## Types

`int`, `float`, `bool`, `str`, `list[T]`, `dict[K, V]`, `set[T]`, `tuple[A, B, …]`, `tuple[T, ...]`, `T?`, `Callable[[A, B], R]`, `Iterable[T]`, `Iterator[T]`, `Generator[Y, S, R]`, `AsyncIterable[T]`, `AsyncIterator[T]`, `AsyncGenerator[Y, S]`, `Awaitable[T]`, `TextIO`, `object`, `py`, and the protocols `Comparable` and `Protocol`.

## Exception classes

As in Python: `BaseException`, `SystemExit`, `GeneratorExit`, `Exception`, `ArithmeticError`, `ZeroDivisionError`, `OverflowError`, `LookupError`, `IndexError`, `KeyError`, `ValueError`, `UnicodeError` (with `UnicodeDecodeError` and `UnicodeEncodeError`), `TypeError`, `RuntimeError`, `AssertionError`, `StopIteration`, `StopAsyncIteration`, `EOFError`, `OSError` with `FileNotFoundError`, `FileExistsError`, `PermissionError`, `IsADirectoryError` and `TimeoutError`, and `asyncio.CancelledError`; and Taipan's `PythonError` and `DotNetError`, for exceptions from Python and .NET.
