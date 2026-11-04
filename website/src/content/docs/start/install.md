---
title: Install Taipan
description: Install the taipan command and run your first program.
sidebar:
  order: 1
---

Taipan is a single command, `taipan`, distributed as a .NET tool. It compiles and runs programs on the .NET 10 runtime.

## Requirements

- **The .NET 10 SDK**, from [dot.net](https://dotnet.microsoft.com/download), which installs .NET tools. Running programs needs only the runtime, but building libraries, native executables and extension modules, and restoring NuGet packages, need the SDK.
- **Optional:** a CPython 3.10 or later, to use Python packages from Taipan. See [Calling Python](../../guides/python/).
- **Optional:** a C toolchain (`clang` on Linux, Xcode's command-line tools on macOS, Visual Studio's C++ tools on Windows), for [native executables](../../guides/native/).

## Install

Taipan is on nuget.org as the `TaipanLang` tool:

```sh
dotnet tool install -g TaipanLang
```

That puts `taipan` on your path. Check it:

```sh
taipan --version
```

Update it with `dotnet tool update -g TaipanLang`.

## Install from source

To use the latest code, build the tool from the repository and install it from the package that builds:

```sh
git clone https://github.com/taipan-org/taipan.git
cd taipan
dotnet pack -c Release -o packages
dotnet tool install -g TaipanLang --add-source packages
```

## Without installing

From a clone, you can also run the compiler straight from source:

```sh
dotnet run --project src/Taipan.Cli -- run examples/health.tpn
```

## Next

Write and run [your first program](../first-program/), and set up [your editor](../editor/).
