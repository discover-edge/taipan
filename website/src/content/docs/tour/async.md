---
title: Async
description: async functions, await, asyncio, and async generators.
sidebar:
  order: 11
---

`async def` declares an async function, and `await` waits for one. An async function's call has the type `Awaitable[T]`, where `T` is its declared result.

```taipan
import asyncio

async def fetch(name: str, delay: float) -> str:
    await asyncio.sleep(delay)
    return f"{name} done"

async def main():
    let results = await asyncio.gather(fetch("a", 0.02), fetch("b", 0.01))
    print(results)

asyncio.run(main())
```

```output
["a done", "b done"]
```

- **Calling an async function starts it**, as in C# (in Python, a coroutine waits until it's awaited). It runs until its first `await` of something unfinished.
- `await` works in an `async def`, and at the main file's top level, which then runs as an async program.
- An awaitable that's never awaited is an error, since its exceptions would be lost. Store it to await it later.
- Async code runs on one thread, on an event loop, as in Python.

## asyncio

`asyncio.run`, `gather`, `sleep`, `wait_for` and `timeout` work as in Python. A timeout cancels what it's waiting for, including .NET work an async function awaits:

```taipan
import asyncio

async def slow() -> int:
    await asyncio.sleep(10)
    return 1

async def main():
    try:
        async with asyncio.timeout(0.05):
            print(await slow())
    except TimeoutError:
        print("timed out")

asyncio.run(main())
```

```output
timed out
```

## Async generators

An `async def` with `yield` is an async generator, read with `async for`:

```taipan
import asyncio

async def ticks(name: str, n: int) -> AsyncIterator[str]:
    for i in range(n):
        await asyncio.sleep(0)
        yield f"{name}{i}"

async def main():
    async for tick in ticks("t", 3):
        print(tick)

asyncio.run(main())
```

```output
t0
t1
t2
```

.NET tasks are awaitable too: `await Task.Delay(100)`, `await client.GetStringAsync(url)`. See [Using .NET](../../guides/dotnet/#async).

That's the tour. Next, see what Taipan does with the rest of .NET in the [guides](../../guides/dotnet/).
