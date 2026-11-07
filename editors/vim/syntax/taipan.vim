" Vim syntax for Taipan (SPEC §16.12). It colors what the VS Code extension's TextMate grammar
" colors (SPEC §16.8), with the same rules.
"
" Where two matches or regions start at the same column, Vim takes the one defined last, so the
" longer operators come after the shorter ones they start with.

if exists('b:current_syntax')
  finish
endif

let s:cpo_save = &cpo
set cpo&vim

syn cluster taipanCode contains=taipanFString,taipanString,taipanNumber,taipanFloat,taipanDecorator,
      \taipanFunctionName,taipanTypeName,taipanFor,taipanConstant,taipanStatement,
      \taipanImport,taipanOperatorWord,taipanFunctionKeyword,taipanClassKeyword,taipanDeclaration,
      \taipanDel,taipanSelf,taipanBuiltin,taipanBuiltinType,taipanArithmetic,taipanComparison,
      \taipanAssignment,taipanArrow

syn match taipanComment "#.*$" contains=@Spell

" Strings end at their quote, or at the end of the line when they aren't closed.
syn match taipanEscape "\\\%([ntrabfv\\\"']\|\o\{1,3}\|x\x\{2}\|u\x\{4}\|U\x\{8}\|$\)" contained
syn region taipanString start=+"+ skip=+\\\\\|\\"+ end=+"\|$+ contains=taipanEscape,@Spell
syn region taipanString start=+'+ skip=+\\\\\|\\'+ end=+'\|$+ contains=taipanEscape,@Spell
syn region taipanString start=+"""+ skip=+\\.+ end=+"""+ contains=taipanEscape,@Spell
syn region taipanString start=+\'\'\'+ skip=+\\.+ end=+\'\'\'+ contains=taipanEscape,@Spell
syn region taipanString start=+\<r"+ skip=+\\.+ end=+"\|$+ contains=@Spell
syn region taipanString start=+\<r'+ skip=+\\.+ end=+'\|$+ contains=@Spell
syn region taipanString start=+\<r"""+ skip=+\\.+ end=+"""+ contains=@Spell
syn region taipanString start=+\<r\'\'\'+ skip=+\\.+ end=+\'\'\'+ contains=@Spell

" An f-string's holes hold code, with a conversion and a format spec at their end.
syn region taipanFString start=+\<f"+ skip=+\\\\\|\\"+ end=+"\|$+ contains=taipanEscape,taipanHole,taipanFBraces
syn region taipanFString start=+\<f'+ skip=+\\\\\|\\'+ end=+'\|$+ contains=taipanEscape,taipanHole,taipanFBraces
syn region taipanFString start=+\<f"""+ skip=+\\.+ end=+"""+ contains=taipanEscape,taipanHole,taipanFBraces
syn region taipanFString start=+\<f\'\'\'+ skip=+\\.+ end=+\'\'\'+ contains=taipanEscape,taipanHole,taipanFBraces
syn region taipanFString start=+\<\%(rf\|fr\)"+ skip=+\\[^{}]+ end=+"\|$+ contains=taipanHole,taipanFBraces
syn region taipanFString start=+\<\%(rf\|fr\)'+ skip=+\\[^{}]+ end=+'\|$+ contains=taipanHole,taipanFBraces
syn region taipanFString start=+\<\%(rf\|fr\)"""+ skip=+\\[^{}]+ end=+"""+ contains=taipanHole,taipanFBraces
syn region taipanFString start=+\<\%(rf\|fr\)\'\'\'+ skip=+\\[^{}]+ end=+\'\'\'+ contains=taipanHole,taipanFBraces
syn region taipanHole matchgroup=taipanHoleBrace start=+{+ end=+}+ contained contains=@taipanCode,taipanFormat
syn match taipanFBraces "{{\|}}" contained
syn match taipanFormat "![rs]\ze[:}]" contained
syn match taipanFormat ":[^{}()\[\]'\"]*\ze}" contained

syn match taipanNumber "\<\d\%(_\=\d\)*\>"
syn match taipanFloat "\<\d\%(_\=\d\)*\%(\.\d\%(_\=\d\)*\%([eE][+-]\=\d\+\)\=\|[eE][+-]\=\d\+\)\>"

syn match taipanDecorator "@\h\w*\%(\.\h\w*\)*"

syn match taipanFunctionName "\%(\<def\s\+\)\@<=\h\w*"
syn match taipanTypeName "\%(\<\%(class\|enum\)\s\+\)\@<=\h\w*"
" `type` is a declaration only where it starts an alias.
syn match taipanDeclaration "^\s*\zstype\ze\s\+\h\w*\s*[=[]"

" A `for` header's `in` is part of the statement, not the operator.
syn region taipanFor matchgroup=taipanStatement start="\<for\>" end="\<in\>\|\ze:\|$" oneline transparent contains=@taipanCode

syn keyword taipanConstant True False None
syn keyword taipanStatement if elif else while match return pass break continue nonlocal try except finally
syn keyword taipanStatement raise yield await with assert
syn keyword taipanImport import from as
syn keyword taipanOperatorWord and or not is in some
syn keyword taipanFunctionKeyword def lambda async
syn keyword taipanClassKeyword class enum
syn keyword taipanDeclaration let var const
syn keyword taipanDel del
syn keyword taipanSelf self cls
syn keyword taipanBuiltin print len range reversed repr hash sorted map filter enumerate zip iter next
syn keyword taipanBuiltin isinstance super abs min max sum any all round divmod input open

" `int`, `float`, `str`, `list` and `set` are functions only when called, and types otherwise.
syn match taipanBuiltinType "\<\%(int\|float\|str\|bool\|list\|dict\|set\|tuple\|Iterator\|Iterable\|Callable\|Awaitable\|TextIO\)\>"
syn match taipanBuiltin "\<\%(int\|float\|str\|list\|set\)\>\ze\s*("

syn match taipanArithmetic "[-+*/%|&^]"
syn match taipanAssignment "="
syn match taipanComparison "[<>]"
syn match taipanArithmetic "\*\*\|//\|??"
syn match taipanComparison "==\|!=\|<=\|>="
syn match taipanAssignment "+=\|-=\|\*=\|/=\|%=\|//=\|\*\*="
syn match taipanArrow "->"

hi def link taipanComment Comment
hi def link taipanString String
hi def link taipanEscape SpecialChar
hi def link taipanFBraces SpecialChar
hi def link taipanFString String
hi def link taipanHoleBrace Delimiter
hi def link taipanFormat Special
hi def link taipanNumber Number
hi def link taipanFloat Float
hi def link taipanConstant Constant
hi def link taipanStatement Statement
hi def link taipanImport Include
hi def link taipanOperatorWord Operator
hi def link taipanFunctionKeyword Keyword
hi def link taipanClassKeyword Structure
hi def link taipanDeclaration StorageClass
hi def link taipanDel Keyword
hi def link taipanFunctionName Function
hi def link taipanTypeName Type
hi def link taipanDecorator PreProc
hi def link taipanSelf Special
hi def link taipanBuiltin Function
hi def link taipanBuiltinType Type
hi def link taipanArithmetic Operator
hi def link taipanComparison Operator
hi def link taipanAssignment Operator
hi def link taipanArrow Delimiter

let b:current_syntax = 'taipan'

let &cpo = s:cpo_save
unlet s:cpo_save
