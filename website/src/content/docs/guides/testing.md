---
title: Testing
description: assert, taipan test, and test projects.
sidebar:
  order: 7
---

## `assert`

`assert condition` raises `AssertionError` when the condition is false, with an optional message. Unlike Python's, it's never compiled away.

```taipan
def mean(xs: list[float]) -> float:
    assert len(xs) > 0, "mean of an empty list"
    return sum(xs) / len(xs)

print(mean([1, 2, 3]))
print(mean([]))
```

```output
2.0
```

```error
Traceback (most recent call last):
  File "main.tpn", line 6, in <module>
    print(mean([]))
  File "main.tpn", line 2, in mean
    assert len(xs) > 0, "mean of an empty list"
AssertionError: mean of an empty list
```

## `taipan test`

`taipan test` finds test files, `test_*.tpn` and `*_test.tpn`, and runs each one's `test_*` functions, as pytest does:

```taipan title="shapes.tpn"
def area(width: float, height: float) -> float:
    return width * height
```

```taipan title="test_shapes.tpn"
from shapes import area

def test_area():
    assert area(2, 3) == 6.0

def test_negative():
    assert area(-1, 2) == 0.0, "a negative side has no area"
```

```sh
taipan test
```

```
PASS test_shapes.tpn::test_area
FAIL test_shapes.tpn::test_negative
Traceback (most recent call last):
  File "test_shapes.tpn", line 7, in test_negative
    assert area(-1, 2) == 0.0, "a negative side has no area"
AssertionError: a negative side has no area
1 passed, 1 failed
```

- `taipan test` takes files and directories, `.` by default.
- Each test file is a main file: its top level runs first, then its tests, in order. Tests may be `async`.
- A test passes if it returns, and fails if it raises anything.
- The exit code is `0` when every test passed, and `1` when one failed, a file had an error, or there were no tests.

## Test projects

In an SDK project, a test project is a `.tpnproj` with `IsTestProject`, so `dotnet test` runs it beside your C# tests:

```xml
<Project Sdk="TaipanLang.Sdk/0.4.1">
  <PropertyGroup>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../Acme.Shapes/Acme.Shapes.tpnproj" />
  </ItemGroup>
</Project>
```

Its main file is a test file, and `dotnet test` writes the same report. See [Projects](../projects/).

## Formatting in CI

`taipan fmt --check .` lists the files that aren't formatted and exits `1` if there are any.
