-- Enables the Taipan language server with lsp/taipan.lua, changing only its command, opens a file
-- and writes its diagnostics, one per line: `<line>:<column> <severity> <code>: <message's first
-- line>` (SPEC §16.12). The file, the output and the command (a JSON list) come from the
-- environment, as TAIPAN_FILE, TAIPAN_OUT and TAIPAN_COMMAND. Run as
-- `nvim --clean --headless --cmd 'set rtp^=editors/vim' -c 'luafile test/lsp.lua'`.
vim.lsp.config('taipan', { cmd = vim.json.decode(vim.env.TAIPAN_COMMAND) })
vim.lsp.enable('taipan')
vim.cmd.edit(vim.fn.fnameescape(vim.env.TAIPAN_FILE))

local published = vim.wait(60000, function() return #vim.diagnostic.get(0) > 0 end, 50)
local lines = {}
if not published then
  lines = { 'no diagnostics were published' }
end
local severities = { [vim.diagnostic.severity.ERROR] = 'error', [vim.diagnostic.severity.WARN] = 'warning' }
for _, d in ipairs(vim.diagnostic.get(0)) do
  local message = vim.split(d.message, '\n')[1]
  table.insert(lines, string.format('%d:%d %s %s: %s', d.lnum + 1, d.col + 1, severities[d.severity] or '?', d.code or '', message))
end
vim.fn.writefile(lines, vim.env.TAIPAN_OUT)
vim.cmd(published and 'qall!' or 'cquit! 1')
