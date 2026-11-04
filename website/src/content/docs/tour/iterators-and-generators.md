---
title: Iterators and generators
description: Iterable, Iterator, generator functions and generator expressions.
sidebar:
  order: 10
---

## Iterables and iterators

`Iterable[T]` is anything a `for` loop can go over: lists, strings, dicts, sets, ranges, files and generators. An `Iterator[T]` is a single pass over one, which `next` advances:

```taipan
let it = iter(["a", "b"])
print(next(it), next(it), next(it, None))
```

```output
a b None
```

`map`, `filter`, `zip` and `enumerate` give iterators, computed as they're read.

## Generators

A function with `yield` is a generator. Its body runs a step at a time, as values are asked for, so it can describe a sequence that never ends:

```taipan
def naturals(start: int) -> Iterator[int]:
    var n = start
    while True:
        yield n
        n += 1

def take[T](n: int, xs: Iterable[T]) -> list[T]:
    var out: list[T] = []
    for x in xs:
        if len(out) == n:
            break
        out.append(x)
    return out

let squares = (n * n for n in naturals(1))
print(take(5, squares))
print(take(3, filter(lambda n: n % 7 == 0, naturals(1))))
```

```output
[1, 4, 9, 16, 25]
[7, 14, 21]
```

`yield from` yields everything another iterable does.

## Generators as coroutines

Declared as `Generator[Y, S, R]`, a generator yields `Y`s, receives `S`s through `send`, and returns an `R`:

```taipan
def averager() -> Generator[float, float, int]:
    var total = 0.0
    var count = 0
    var average = 0.0
    while count < 3:
        let value = yield average
        total += value
        count += 1
        average = total / count
    return count

let avg = averager()
next(avg)
print(avg.send(10.0), avg.send(20.0))
```

```output
10.0 15.0
```

## Your own iterators

A class with `__iter__` is iterable, and one with `__next__` too is an iterator. Raise `StopIteration` to end it:

```taipan
class Countdown:
    n: int

    def __init__(self, n: int):
        self.n = n

    def __iter__(self) -> Iterator[int]:
        return self

    def __next__(self) -> int:
        if self.n == 0:
            raise StopIteration()
        self.n -= 1
        return self.n + 1

print(list(Countdown(3)))
```

```output
[3, 2, 1]
```

Next: [Async](../async/).
