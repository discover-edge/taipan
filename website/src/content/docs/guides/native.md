---
title: Native builds
description: Single-file native executables, other architectures, and C libraries.
sidebar:
  order: 5
---

## Native executables

`taipan build --native` compiles a program ahead of time, with .NET's NativeAOT, into one executable that runs without .NET installed and starts quickly:

```sh
taipan build health.tpn --native -o out
./out/health
```

It needs the .NET SDK and the platform's C toolchain: clang (or gcc) on Linux, Xcode's command-line tools on macOS, Visual Studio's C++ tools on Windows. A native program behaves exactly as under `taipan run`, tracebacks included.

What can't work natively is an error when you compile, not when it runs: Python interop, and the few .NET members that need run-time code generation ([TP0233](../../diagnostics/tp0233/)).

## Other architectures

`--runtime` builds for another architecture of the same operating system:

```sh
taipan build health.tpn --native --runtime linux-arm64 -o out
```

The runtimes are `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`, `win-x64` and `win-arm64`. NativeAOT can't build for another operating system.

On Linux, cross-compiling needs the target's GCC, binutils and C library. On Debian and Ubuntu, for arm64:

```sh
sudo apt-get install gcc-aarch64-linux-gnu binutils-aarch64-linux-gnu libc6-dev-arm64-cross
```

On macOS, Xcode builds both architectures; on Windows, install Visual Studio's C++ tools for the target.

## Smaller executables

- `--invariant-globalization` leaves out culture data (ICU on Linux), so the program is smaller and needs no ICU where it runs. Taipan's own operations don't depend on culture.
- `--optimize-size` prefers smaller code to faster code.

## C libraries

`taipan build --library --native` compiles a module into a shared library with C functions, and writes a header for it:

```taipan title="geometry.tpn"
def area(width: float, height: float) -> float:
    return width * height

def label(name: str, sides: int) -> str:
    return f"{name} with {sides} sides"

def checked_sqrt(x: float) -> float:
    if x < 0.0:
        raise ValueError(f"{x} is negative")
    return x ** 0.5
```

```sh
taipan build geometry.tpn --library --native -o out
```

That writes `out/geometry.so` (`.dylib` on macOS, `.dll` on Windows) and `out/geometry.h`:

```c
#include "geometry.h"
#include <stdio.h>

int main(void) {
    printf("%f\n", geometry_area(2.0, 3.0));
    char *text = geometry_label("square", 4);
    printf("%s\n", text);
    geometry_free(text);
    geometry_checked_sqrt(-1.0);
    printf("%s\n", geometry_last_error());   // ValueError: -1.0 is negative
}
```

- Functions are exported as `<module>_<function>`, with `int64_t` for `int`, `double` for `float`, `bool` for `bool`, and UTF-8 strings for `str`.
- A returned string is the caller's, freed with `<module>_free`.
- An exception can't cross into C: the function returns `0`, `false` or `NULL`, and `<module>_last_error()` gives `<Class>: <message>` for the calling thread.
- Functions with other types aren't exported, with warning [TP0302](../../diagnostics/tp0302/) saying why.
