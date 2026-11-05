-- Neovim's configuration for the Taipan language server (SPEC §16.12), read by Neovim 0.11 and
-- later for `vim.lsp.enable('taipan')`.
return {
  cmd = { 'taipan', 'lsp', '--stdio' },
  filetypes = { 'taipan' },
  root_markers = { 'taipan.json', '.git' },
}
