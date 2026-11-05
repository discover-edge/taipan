---
title: Editor setup
description: Use Taipan in VS Code, Neovim, Vim, or any editor that speaks the Language Server Protocol.
sidebar:
  order: 3
---

Taipan's editor support comes from its language server, `taipan lsp`, which comes with `taipan` itself. It gives:

- errors as you type, hover, go to definition, completion and signature help;
- find references, and renaming across modules (renaming a module renames its file);
- formatting, and quick fixes for common mistakes, such as adding an import or `@override`;
- semantic highlighting.

Every editor below starts `taipan lsp --stdio`, so [install Taipan](../install/) first and make sure `taipan` is on your path.

## VS Code

Install **Taipan** (`discover-edge.taipan`) from the [Visual Studio Marketplace](https://marketplace.visualstudio.com/items?itemName=discover-edge.taipan), or from the Extensions view: search for "Taipan". VSCodium, Cursor and other editors built on VS Code install it from [Open VSX](https://open-vsx.org/extension/discover-edge/taipan).

Besides the language server, the extension gives:

- highlighting, as soon as a file opens;
- **Taipan: Run File**, also the run button in the editor's title bar, which saves the file and runs it in a terminal;
- **Taipan: Restart Language Server**.

If `taipan` isn't on your path, set **Taipan: Path** (`taipan.path`) in the settings. When it can't be found, the extension offers to install it.

## Neovim

The repository's `editors/vim` directory gives Neovim the `taipan` filetype, highlighting, indenting, and the language server's configuration. Clone the repository and add the directory to your `runtimepath` in `init.lua`, then enable the server (Neovim 0.11 or later):

```lua
vim.opt.runtimepath:append('~/src/taipan/editors/vim')
vim.lsp.enable('taipan')
```

The configuration it enables, `editors/vim/lsp/taipan.lua`, starts `taipan lsp --stdio` for Taipan files, with the project's root at the nearest `taipan.json` or `.git`. Change it as for any server, with `vim.lsp.config('taipan', { ... })`.

## Vim

The same `editors/vim` directory works in Vim 9. Add it to your `runtimepath` in your `vimrc`, before `filetype plugin indent on`:

```vim
set runtimepath+=~/src/taipan/editors/vim
filetype plugin indent on
syntax on
```

With [vim-plug](https://github.com/junegunn/vim-plug), name the directory instead of cloning by hand:

```vim
Plug 'taipan-org/taipan', { 'rtp': 'editors/vim' }
```

Vim has no language server client of its own. With [yegappan/lsp](https://github.com/yegappan/lsp):

```vim
call LspAddServer([#{name: 'taipan', filetype: ['taipan'], path: 'taipan', args: ['lsp', '--stdio'], rootSearch: ['taipan.json', '.git']}])
```

With [vim-lsp](https://github.com/prabirshrestha/vim-lsp):

```vim
autocmd User lsp_setup call lsp#register_server({'name': 'taipan', 'cmd': {_ -> ['taipan', 'lsp', '--stdio']}, 'allowlist': ['taipan']})
```

With [coc.nvim](https://github.com/neoclide/coc.nvim), in `coc-settings.json`:

```json
{
  "languageserver": {
    "taipan": {
      "command": "taipan",
      "args": ["lsp", "--stdio"],
      "filetypes": ["taipan"],
      "rootPatterns": ["taipan.json", ".git"]
    }
  }
}
```

## Other editors

`taipan lsp` is a standard [Language Server Protocol](https://microsoft.github.io/language-server-protocol/) server over stdin and stdout. Any editor with an LSP client can use it: configure it to start `taipan lsp --stdio` for `.tpn` files, with the project's root at the nearest `taipan.json`. In Helix's `languages.toml`, for example:

```toml
[language-server.taipan]
command = "taipan"
args = ["lsp", "--stdio"]

[[language]]
name = "taipan"
scope = "source.taipan"
file-types = ["tpn"]
language-servers = ["taipan"]
```

The TextMate grammar in `editors/vscode/syntaxes/taipan.tmLanguage.json` works in editors that read TextMate grammars, and is what highlights the code on this site.

## Formatting

`taipan fmt` formats files in place, and `taipan fmt --check` reports the files that aren't formatted, for CI:

```sh
taipan fmt src/
taipan fmt --check src/
```

The formatter only changes spacing. It keeps your line breaks, never changes what a program means, and formatting twice changes nothing.
