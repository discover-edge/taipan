# Taipan for VS Code

Language support for [Taipan](https://taipan-lang.org), a statically typed language with Python's syntax that compiles to .NET.

- **Highlighting** as soon as a `.tpn` file opens.
- **Errors as you type**, hover, go to definition, completion, signature help, find references, rename across modules, formatting, quick fixes and semantic highlighting, from the Taipan language server.
- **Taipan: Run File**, also the run button in the editor's title bar, which saves the file and runs it in a terminal.
- **Taipan: Restart Language Server**.

## Requirements

The extension runs the `taipan` command, which comes with Taipan. Install it with the [.NET SDK](https://dotnet.microsoft.com/download):

```
dotnet tool install -g TaipanLang
```

If `taipan` isn't on your `PATH`, set **Taipan: Path** (`taipan.path`) to it. When it can't be found, the extension offers to install it. After installing, run **Taipan: Restart Language Server**.

## Learn more

- [Get started with Taipan](https://taipan-lang.org/start/install/)
- [A tour of the language](https://taipan-lang.org/tour/basics/)
- [Report an issue](https://github.com/taipan-org/taipan/issues)

## Building from source

```
npm ci
npx vsce package
code --install-extension taipan-*.vsix
```

`npm test` runs the extension's scripted test, and `npm run tokens` prints the grammar's tokens for `test/sample.tpn`, which the repository's tests compare with `test/sample.tokens` (SPEC §16.8).
