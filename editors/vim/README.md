# Taipan for Vim and Neovim

A Vim runtime directory for [Taipan](https://taipan-lang.org) `.tpn` files (SPEC §16.12), for Vim 9 and Neovim:

- `ftdetect/`: the `taipan` filetype;
- `syntax/`: highlighting, coloring what the VS Code extension's grammar colors;
- `ftplugin/` and `indent/`: `#` comments, 4-space indenting, and indenting after a line ending in `:`;
- `lsp/taipan.lua`: Neovim's configuration for the language server, `taipan lsp --stdio`.

Add this directory to your `runtimepath`. In Neovim 0.11 or later:

```lua
vim.opt.runtimepath:append('~/src/taipan/editors/vim')
vim.lsp.enable('taipan')
```

In Vim, before `filetype plugin indent on`:

```vim
set runtimepath+=~/src/taipan/editors/vim
```

[Editor setup](https://taipan-lang.org/start/editor/) shows how to set up Vim's LSP plugins.

## Tests

The repository's tests (`tests/Taipan.Tests/VimTests.cs`) run the scripts in `test/`:

- `syntax.vim` writes the groups the syntax gives the grammar's sample, `../vscode/test/sample.tpn`, which must be `test/sample.syntax`;
- `indent.vim` types `test/indent.tpn` without its indentation, which must give the file back;
- `lsp.lua` opens a file in Neovim with the language server enabled, and writes its diagnostics, which must be those `taipan check` reports.

To see the syntax list after changing the syntax:

```
vim -Nu NONE -i NONE -Es --cmd 'set rtp^=.' -S test/syntax.vim ../vscode/test/sample.tpn /dev/stdout
```
