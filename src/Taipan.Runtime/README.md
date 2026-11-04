# TaipanLang.Runtime

The runtime library that programs compiled by [Taipan](https://taipan-lang.org) reference: Taipan's built-in functions, collections, exceptions, display rules and Python interop.

You need this package when .NET code uses a library written in Taipan: reference the library's assembly and `TaipanLang.Runtime` at the version of the compiler that built it. Taipan SDK projects (`TaipanLang.Sdk`) add it for you, and the `taipan` tool carries its own copy, so programs run with `taipan run` don't need it.

- [Building .NET libraries with Taipan](https://taipan-lang.org/guides/dotnet-libraries/)
- [Source and specification](https://github.com/taipan-org/taipan)
