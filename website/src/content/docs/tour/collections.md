---
title: Collections
description: Lists, dicts, sets, tuples, slicing and comprehensions.
sidebar:
  order: 2
---

## Lists

A `list[T]` is a growable sequence of `T`s. A literal's element type is inferred, and every element must fit it.

```taipan
var xs = [3, 1, 2]
xs.append(5)
xs.sort()
print(xs, len(xs), xs[0], xs[-1])
print(xs[1:3], xs[::-1], 2 in xs)
xs[0] = 10
print(xs.pop(), xs)
```

```output
[1, 2, 3, 5] 4 1 5
[2, 3] [5, 3, 2, 1] True
5 [10, 2, 3]
```

An index out of range raises `IndexError`. `int` and `float` elements mix to `list[float]`, and `None` mixes in as an optional: `[1, None]` is a `list[int?]`.

## Dicts

A `dict[K, V]` maps keys to values and keeps insertion order. Keys are `int`, `str`, `bool`, tuples of those, or classes that define `__hash__`.

```taipan
var stock = {"tea": 3, "cake": 0}
stock["jam"] = 5
stock["tea"] += 1
print(stock)
print(stock["tea"], stock.get("milk"), stock.get("milk", 0))
for name, count in stock.items():
    if count == 0:
        print("out of", name)
print("cake" in stock, list(stock.keys()))
```

```output
{"tea": 4, "cake": 0, "jam": 5}
4 None 0
out of cake
True ["tea", "cake", "jam"]
```

`d[k]` raises `KeyError` for a missing key. `d.get(k)` gives a `V?` instead, which you check before use (see [Optionals](../optionals/)).

## Sets

A `set[T]` holds distinct values. It keeps insertion order, so it prints the same way every run.

```taipan
let seen = {3, 1, 3, 2}
print(seen, len(seen), 2 in seen)
print({1, 2, 3} & {2, 3, 4}, {1, 2} | {3}, {1, 2, 3} - {2})
let empty: set[str] = set()
print(empty)
```

```output
{3, 1, 2} 3 True
{2, 3} {1, 2, 3} {1, 3}
set()
```

`{}` is an empty dict, as in Python.

## Tuples

A tuple groups a fixed number of values, each with its own type: `tuple[str, int]`. Tuples are immutable and are unpacked into names.

```taipan
def bounds(xs: list[int]) -> tuple[int, int]:
    return min(xs), max(xs)

let lo, hi = bounds([4, 9, 2])
print(lo, hi)
let point = (1.5, "north")
print(point[0], point)

var a = 1
var b = 2
a, b = b, a
print(a, b)

let first, *rest = [1, 2, 3, 4]
print(first, rest)
```

```output
2 9
1.5 (1.5, "north")
2 1
1 [2, 3, 4]
```

Tuples compare element by element, so they make good sort keys.

## Comprehensions

List, dict and set comprehensions, and generator expressions, work as in Python:

```taipan
let words = ["apple", "fig", "banana", "kiwi"]
print([w.upper() for w in words if len(w) > 3])
print({w: len(w) for w in words})
print({len(w) for w in words})
print(sum(len(w) for w in words))
print([(x, y) for x in range(3) for y in range(x)])
```

```output
["APPLE", "BANANA", "KIWI"]
{"apple": 5, "fig": 3, "banana": 6, "kiwi": 4}
{5, 3, 6, 4}
18
[(1, 0), (2, 0), (2, 1)]
```

## Sorting and friends

```taipan
let people = [("ada", 36), ("grace", 45), ("alan", 41)]
print(sorted(people, key=lambda p: p[1], reverse=True))
print(max(people, key=lambda p: p[1])[0])
print(list(zip(["a", "b"], [1, 2])))
print(list(map(lambda p: p[0], people)))
print(any(age > 40 for _, age in people), all(age > 40 for _, age in people))
```

```output
[("grace", 45), ("alan", 41), ("ada", 36)]
grace
[("a", 1), ("b", 2)]
["ada", "grace", "alan"]
True False
```

`map`, `filter`, `zip` and `enumerate` are lazy, as in Python 3: wrap them in `list(…)` to get a list.

## Invariance

A `list[Dog]` isn't a `list[Animal]`, even when `Dog` derives from `Animal`: code holding it as a `list[Animal]` could append a `Cat`. The same goes for dicts, sets and tuples. A literal takes its expected type, so `let pets: list[Animal] = [Dog("rex")]` works.

Next: [Functions](../functions/).
