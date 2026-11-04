" Taipan's indenting (SPEC §16.12): a level more after a line ending in `:`, a level less after
" `return`, `pass`, `break`, `continue` or `raise`, and `elif`, `else`, `except` and `finally`
" a level less than the line above when their `:` is typed.
if exists('b:did_indent')
  finish
endif
let b:did_indent = 1

setlocal autoindent
setlocal indentexpr=TaipanIndent(v:lnum)
setlocal indentkeys=!^F,o,O,<:>

let b:undo_indent = 'setlocal autoindent< indentexpr< indentkeys<'

if exists('*TaipanIndent')
  finish
endif

function TaipanIndent(lnum) abort
  let above = prevnonblank(a:lnum - 1)
  if above == 0
    return 0
  endif
  let line = getline(a:lnum)
  let previous = getline(above)
  if line =~# '^\s*\%(elif\|else\|except\|finally\)\>'
    return min([indent(a:lnum), indent(above) - shiftwidth()])
  endif
  " A line with code keeps its indentation: the end of a block can't be told from the code above.
  if line =~# '\S'
    return -1
  endif
  if previous =~# ':\s*\%(#.*\)\=$'
    return indent(above) + shiftwidth()
  endif
  if previous =~# '^\s*\%(return\|pass\|break\|continue\|raise\)\>'
    return indent(above) - shiftwidth()
  endif
  return indent(above)
endfunction
