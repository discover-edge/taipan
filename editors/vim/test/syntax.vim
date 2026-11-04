" Writes the groups the syntax gives a file (SPEC §16.12), one token per line:
" `<line>:<column> <text as JSON> <innermost group, or ->`. A token is a run of characters with
" the same group, and a run with no group is also split at spaces; whitespace-only tokens are
" left out. Run as `vim -Nu NONE -Es --cmd 'set rtp^=editors/vim' -S test/syntax.vim <file> <out>`.
filetype on
syntax on
let s:file = argv(0)
let s:out = argv(1)
execute 'edit' fnameescape(s:file)
let s:tokens = []
for s:lnum in range(1, line('$'))
  let s:text = getline(s:lnum)
  let s:start = 0
  let s:group = ''
  for s:col in range(len(s:text) + 1)
    let s:here = s:col < len(s:text) ? synIDattr(synID(s:lnum, s:col + 1, 1), 'name') : "\n"
    let s:here = s:here ==# '' && s:text[s:col] =~# '\s' ? ' ' : s:here
    if s:col > 0 && (s:here !=# s:group || s:group ==# ' ')
      let s:token = s:text[s:start : s:col - 1]
      if s:token =~# '\S'
        call add(s:tokens, printf('%d:%d %s %s', s:lnum, s:start + 1, json_encode(s:token), s:group ==# '' ? '-' : s:group))
      endif
      let s:start = s:col
    endif
    let s:group = s:here
  endfor
endfor
call writefile(s:tokens, s:out)
qall!
