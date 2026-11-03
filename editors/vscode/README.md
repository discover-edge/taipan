# Taipan for VS Code

Language support for [Taipan](https://github.com/discover-edge/taipan) `.tpn` files (SPEC §16):

- Highlighting, from a TextMate grammar, as soon as a file opens.
- Diagnostics as you type, hover, go to definition, completion, signature help, find references, rename across modules, and semantic highlighting, from `taipan lsp`.

The extension starts `taipan lsp --stdio`. Set `taipan.path` if `taipan` isn't on your `PATH`.

## Building

```
npm ci
npx vsce package
```

`npm run tokens` prints the grammar's tokens for `test/sample.tpn`, which the repository's tests compare with `test/sample.tokens`.
