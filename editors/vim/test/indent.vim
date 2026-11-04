" Types each line of a file into an empty buffer without its indentation, ending each with Enter,
" as a person would, and writes the buffer (SPEC §16.12). Lines ending in `:` are typed key by
" key, so their `:` reindents them. Blank lines are typed as an empty line, which keeps nothing.
" Run as `vim -Nu NONE -Es --cmd 'set rtp^=editors/vim' -S test/indent.vim <file> <out>`.
filetype plugin indent on
let s:lines = readfile(argv(0))
let s:out = argv(1)
enew
setfiletype taipan
let s:keys = ''
for s:line in s:lines
  let s:keys .= substitute(s:line, '^\s*', '', '') . "\r"
endfor
call feedkeys('i' . s:keys . "\<Esc>", 'xt')
" Typing leaves an empty line after the last Enter.
$delete _
call writefile(getline(1, '$'), s:out)
qall!
