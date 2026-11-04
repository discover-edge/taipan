# TaipanLang.Sdk

The MSBuild project SDK for [Taipan](https://taipan-lang.org) projects. A `.tpnproj` file builds with `dotnet build`, runs with `dotnet run`, tests with `dotnet test` and packs with `dotnet pack`, alongside C# projects in one solution:

```xml
<Project Sdk="TaipanLang.Sdk/0.4.0">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
</Project>
```

The main file is `<project name>.tpn`, and the project's other `.tpn` files are its modules. `PackageReference`, `ProjectReference` and `Reference` work as in C# projects. The SDK carries its own copy of the compiler, so nothing else needs installing.

- [Projects guide](https://taipan-lang.org/guides/projects/)
- [Source and specification](https://github.com/taipan-org/taipan)
