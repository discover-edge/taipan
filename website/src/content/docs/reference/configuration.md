---
title: taipan.json
description: The configuration file beside a main file.
sidebar:
  order: 3
---

`taipan.json`, in the main file's directory, configures a program or library built with the `taipan` command. Every key is optional, and so is the file. Any other key is an error.

```json
{
  "references": ["lib/Acme.Geometry.dll"],
  "packages": { "Humanizer.Core": "2.14.1" },
  "assembly": "Acme.Shapes",
  "namespace": "Acme.Shapes",
  "version": "1.2.0",
  "authors": "Acme",
  "description": "Shapes and their areas."
}
```

| Key | Meaning |
|---|---|
| `references` | Paths to .NET assemblies, relative to `taipan.json`, whose types the program may import |
| `packages` | NuGet package IDs and their exact versions. A package's dependencies come with it |
| `assembly` | A library's assembly name and package ID (default: the main file's name) |
| `namespace` | A library's .NET namespace (default: the main file's name in PascalCase) |
| `version` | A library's assembly and package version (default `1.0.0`) |
| `authors`, `description` | A package's metadata |

Packages are restored with the .NET SDK's `dotnet restore`, from the NuGet sources that apply to the directory, the first time a list is used. The result is cached under `$TAIPAN_CACHE`, or `taipan` in your user cache directory.

In an [SDK project](../../guides/projects/), MSBuild's properties and items take the place of these keys, and `taipan.json` may not list `references` or `packages`.
