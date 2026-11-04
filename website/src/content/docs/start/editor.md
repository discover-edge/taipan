---
title: Editor setup
description: Use Taipan in VS Code, or any editor that speaks the Language Server Protocol.
sidebar:
  order: 3
---

## VS Code

The repository has a VS Code extension in `editors/vscode`. It gives:

- highlighting, as soon as a file opens;
- errors as you type, hover, go to definition, completion and signature help;
- find references, and renaming across modules (renaming a module renames its file);
- formatting, and quick fixes for common mistakes, such as adding an import or `@override`;
- semantic highlighting.

Build and install it from a clone:

```sh
cd editors/vscode
npm ci
npx vsce package
code --install-extension taipan-*.vsix
```

The extension runs `taipan lsp`, so `taipan` must be on your path, or set **Taipan: Path** (`taipan.path`) in the settings.

## Other editors

`taipan lsp` is a standard [Language Server Protocol](https://microsoft.github.io/language-server-protocol/) server over stdin and stdout. Any editor with an LSP client can use it: configure it to start `taipan lsp --stdio` for `.tpn` files.

The TextMate grammar in `editors/vscode/syntaxes/taipan.tmLanguage.json` works in editors that read TextMate grammars, and is what highlights the code on this site.

## Formatting

`taipan fmt` formats files in place, and `taipan fmt --check` reports the files that aren't formatted, for CI:

```sh
taipan fmt src/
taipan fmt --check src/
```

The formatter only changes spacing. It keeps your line breaks, never changes what a program means, and formatting twice changes nothing.
