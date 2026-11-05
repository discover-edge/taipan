" Taipan's filetype settings (SPEC §16.12): `#` comments, 4-space indenting with spaces, and
" `gf` on a module's name opening its file.
if exists('b:did_ftplugin')
  finish
endif
let b:did_ftplugin = 1

setlocal comments=:# commentstring=#\ %s
setlocal expandtab shiftwidth=4 softtabstop=4
setlocal suffixesadd=.tpn

let b:undo_ftplugin = 'setlocal comments< commentstring< expandtab< shiftwidth< softtabstop< suffixesadd<'
