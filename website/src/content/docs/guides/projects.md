---
title: Projects
description: Build Taipan with dotnet build, alongside C# projects, and pack NuGet packages.
sidebar:
  order: 6
---

`taipan run` and `taipan build` work on a file and its `taipan.json`. For larger work, Taipan projects are MSBuild projects, so `dotnet build`, `dotnet run`, `dotnet test` and `dotnet pack` work, and Taipan and C# projects reference each other in one solution.

## A project

A Taipan project is a `.tpnproj` file using the `TaipanLang.Sdk` project SDK:

```xml
<Project Sdk="TaipanLang.Sdk/0.4.2">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../Acme.Core/Acme.Core.csproj" />
    <PackageReference Include="Humanizer.Core" Version="2.14.1" />
  </ItemGroup>
</Project>
```

- **The main file** is `<project name>.tpn` beside the project file, or the one `TaipanMain` names. The project's other `.tpn` files are its modules.
- **`OutputType`** is `Library` (the default) for a [.NET library](../dotnet-libraries/), or `Exe` for a program that `dotnet run` runs.
- **References** are MSBuild's: `PackageReference`, `ProjectReference` and `Reference`. A Taipan file imports their types with `from dotnet.<namespace> import …`, so `taipan.json` doesn't list references or packages in a project ([TPN002](../../diagnostics/tpn002/)).
- **Naming** is MSBuild's too: `AssemblyName`, `RootNamespace` and `Version`.
- **Errors** appear in `dotnet build`'s output and your IDE's error list, in MSBuild's format, and fail the build. Warnings don't, unless `TreatWarningsAsErrors` is set.

The SDK package is `TaipanLang.Sdk`, at the version of the compiler it builds with. It adds a reference to `TaipanLang.Runtime`, so anything that references the project gets the runtime too.

## With C#

A C# project references a Taipan library with a `ProjectReference` to its `.tpnproj`; each Taipan module is then a public class (see [Building .NET libraries](../dotnet-libraries/#what-c-sees)). A Taipan project references a C# project the same way, and imports its public types.

```sh
dotnet new sln -n Acme
dotnet sln add Acme.Shapes/Acme.Shapes.tpnproj Acme.App/Acme.App.csproj
dotnet build
```

## Packing

`dotnet pack` packs a Taipan library as a NuGet package, with MSBuild's package metadata (`PackageId`, `Version`, `Authors`, `Description`), as for C#. Outside a project, `taipan pack` does the same from `taipan.json`.

## Tests

A project with `<IsTestProject>true</IsTestProject>` is a test project, run by `dotnet test`. See [Testing](../testing/#test-projects).
