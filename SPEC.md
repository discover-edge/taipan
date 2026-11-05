# Taipan Language Specification — v0.1 (draft)

Taipan is a statically typed, compiled language with Python's surface syntax. The compiler is written in C# (.NET 10). It transpiles to C# and compiles the result in-process with Roslyn.

**Design goals, in priority order:**
1. Reads like Python. A Python developer should understand any Taipan program on sight.
2. Sound static typing. No `null` surprises, exhaustive `match`, no runtime type errors outside the Python boundary.
3. Annotate function boundaries only. Locals are inferred.
4. Compiled. The output is a .NET assembly, or a native executable through NativeAOT (M23, §15).
5. Python ecosystem access through an explicit, checked boundary (M10).

**Non-goals:** source compatibility with Python, dynamic features (`eval`, monkey-patching, `setattr`), and truthiness.

---

## 1. Lexical structure

### 1.1 Source files
- Files are UTF-8 with the extension `.tpn`.
- Line endings are `\n` or `\r\n`, both normalized to `\n`.

### 1.2 Indentation
- Indentation uses spaces only. A tab in leading whitespace is error `TP0001`.
- The lexer keeps an indent stack, starting at `[0]`.
  - On a logical line with greater indentation than the top of the stack, it pushes and emits `INDENT`.
  - On lesser indentation, it pops and emits one `DEDENT` per pop. If the new level doesn't match an earlier level on the stack, that's `TP0002`.
- Blank lines and comment-only lines are ignored for indentation.
- **Implicit line joining:** inside `()`, `[]`, or `{}`, newlines and indentation are ignored.
  - **Except for a `match` expression's arms (M29, §5.1.1).** Inside brackets, when a line ends with the `:` of a `match` at the current bracket depth, the lexer emits `NEWLINE` and treats the following lines as an indented block: `INDENT` on the first, which must be indented more than the line with `match` (`TP0100` otherwise), then `NEWLINE`, `INDENT` and `DEDENT` as usual. Brackets opened inside an arm join lines again until they close. The first line indented no more than the line with `match` ends the arms: the lexer emits the `DEDENT`s and returns to joining lines in the enclosing brackets.
- There is no backslash line continuation.
- At EOF, the lexer emits `NEWLINE` if needed, then a `DEDENT` for each remaining stack level, then `EOF`.

### 1.3 Tokens
- **Comments:** `#` to end of line.
- **Identifiers:** `[A-Za-z_][A-Za-z0-9_]*`. A lone `_` is the wildcard pattern only.
- **Keywords:**
  `def class enum let var const if elif else while for in match return pass break continue and or not is some None True False as from import lambda try except finally raise del yield async await with assert`
  - `yield` is a keyword since M31 (§3.13), `async` and `await` since M33 (§3.14), `with` since M37 (§3.17), and `assert` since M44 (§5.6).
  - `struct` was a keyword before M15. It's now an ordinary name, and records are declared with `@dataclass class` (§4.3).
  - `type` is a *soft keyword* (M18), as in Python 3.12: it starts a type alias (§3.12) only at the start of a line, followed by a name and then `=` or `[`. Anywhere else it's an ordinary name, so `type = "json"` and `e.type` still work.
  - `isinstance` and `super` are built-in functions (§6), not keywords. `Protocol`, `Self` and `Comparable` are built-in names (M17, §3.11), not keywords.
- **Integer literals:** decimal, with `_` separators allowed (`1_000`). They're 64-bit signed, and overflow at compile time is `TP0200`. The range check belongs to the type checker, not the lexer, so `-9223372036854775808` is valid.
- **Float literals:** `1.5`, `1e9`, `2.5e-3`. A leading digit is required, so `.5` is invalid. Formally: `digits "." digits [exp] | digits exp`, where `exp = ("e" | "E") ["+" | "-"] digits`. `1.` lexes as the int `1` followed by `.`.
- **Number literal rules:** `_` may appear only between two digits. A multi-digit integer part can't start with `0`. A number immediately followed by a letter, digit-less exponent, or `_` (`12abc`, `1e`, `1_`, `007`) is `TP0007`.
- **String literals:** `"..."` or `'...'`. Escapes are `\n \t \\ \" \' \{ \}`. Any other backslash sequence is `TP0005`. Strings are single-line only, and an unterminated string is `TP0003`.
- **F-strings:** `f"...{expr}..."` or `f'...'`, with a lowercase `f` immediately before the quote. A hole holds any expression except an f-string. `{{` and `}}` are literal braces, and literal text uses the string escapes. The lexer emits the f-string as a single token holding the raw text plus hole spans, and the parser re-lexes the holes.
  - A hole runs from `{` to its matching `}`. Inside a hole the lexer tracks nested brackets and skips over string literals of either quote, so `f"{d["k"]}"` is valid.
  - `TP0006`: a lone `}` in literal text, an empty or whitespace-only hole, or an f-string inside a hole.
  - **A hole's parts (M26)** are, in order: the expression; an optional `=`, which shows the expression's text before its value; an optional conversion, `!r` or `!s`; and an optional format spec, `:` followed by the spec up to the hole's closing `}`. So `{x}`, `{x=}`, `{x!r}`, `{x:>8.2f}` and `{x=!r:>10}`. The lexer finds them at the hole's top level only: a `:` or `!` inside brackets or a string, or one that's part of `!=`, `==`, `<=`, `>=`, belongs to the expression. A `lambda` in a hole must be parenthesized, as in Python, since its `:` would start a spec.
  - `TP0006` (M26): a conversion other than `!r` or `!s`, a format spec containing `{` or `}` (nested specs, `{x:{width}}`, aren't supported), and an `=` or conversion with nothing before it.
  - An f-string, or a hole, still open at end of line is `TP0003`.
- **Operators and punctuation:**
  `+ - * / // % ** == != < <= > >= = += -= *= /= //= %= **= -> ?? ? . ... , : ( ) [ ] { } @`
  - `**` and `**=` (M35) are operators. In a parameter list and a call, `**` still marks `**kwargs` and `**e` (M30).
  - `@` begins a decorator (M15, §4.3). It isn't an operator.
  - `...` is one token (M17), the body of a protocol method (§3.11). Three dots always lex as `...`, so `1...` is the int `1` followed by `...`.
- Any other character outside a string or comment, including a non-ASCII letter or a lone `\r`, is `TP0004`.

### 1.4 Lexical errors
- The lexer reports and keeps going, so one file can report several lexical errors.
- **Positions:** each error points at the offending character. `TP0002` points at the line's first non-space character, `TP0003` at the opening quote, `TP0005` at the backslash, and `TP0007` at the literal's first character.
- **Recovery:**
  - `TP0001`: the line keeps the current indentation level, with no `INDENT` or `DEDENT`.
  - `TP0002`: pop every level greater than the new indentation, emitting a `DEDENT` per pop, then treat the line as being at the current top of the stack.
  - `TP0003`: the string ends at end of line.
  - `TP0004`: the character is skipped.
  - `TP0005`, `TP0006`, `TP0007`: the token is still produced.
- **Messages:**

| Code | Message |
|---|---|
| TP0001 | `tab in indentation` |
| TP0002 | `dedent does not match any outer indentation level` |
| TP0003 | `unterminated string` |
| TP0004 | `invalid character '<c>'`, or `invalid character U+XXXX` for control and whitespace characters |
| TP0005 | `invalid escape sequence '\<c>'` |
| TP0006 | `single '}' is not allowed in an f-string` / `empty f-string hole` / `f-strings cannot be nested` / `invalid conversion '!<c>'`, with help `use '!r' or '!s'` / `format specs can't be nested` / `invalid format spec '<spec>'` (M26) |
| TP0007 | `invalid number literal` |

---

## 2. Grammar (EBNF)

Terminal names in CAPS come from the lexer.

```ebnf
file          = { NEWLINE | top_item } EOF ;
top_item      = import_stmt | func_def | class_def | enum_def | const_def | type_alias | stmt ;

type_alias    = "type" NAME [ type_params ] "=" type NEWLINE ;           (* M18, §3.12; "type" is a soft keyword *)

import_stmt   = "import" module_path [ "as" NAME ] NEWLINE              (* M9, §4.7; dotted: a .NET namespace, M43 *)
              | "from" module_path "import" import_name { "," import_name } NEWLINE ;
module_path   = NAME { "." NAME } ;                                  (* dotted: .NET namespaces, M21, §13.1 *)
import_name   = NAME [ "as" NAME ] ;

const_def     = "const" NAME [ ":" type ] "=" expr NEWLINE ;

func_def      = { decorator } [ "async" ] "def" NAME [ type_params ] "(" [ params ] ")" [ "->" type ] ":" block ;   (* async: M33, §3.14 *)
decorator     = "@" NAME [ "." NAME ] [ "(" [ NAME "=" expr { "," NAME "=" expr } [ "," ] ] ")" ] NEWLINE ;   (* M15: @dataclass, @override; §4.3. M28: §4.8. Arguments: M39, §4.9 *)
params        = param_item { "," param_item } [ "," ] ;
param_item    = param | "/" | "*" [ NAME ":" type ] | "**" NAME ":" type ;   (* M30, §4.2 *)
param         = NAME ":" type [ "=" expr ] ;                         (* default: M14, §4.2 *)
type_params   = "[" type_param { "," type_param } "]" ;             (* M8, §3.6 *)
type_param    = NAME [ ":" ( "(" type { "," type } [ "," ] ")"     (* constraints: M8.1; fewer than two types is TP0220 *)
                           | type ) ] ;                            (* a bound: M17, §3.11; anything but a protocol is TP0220 *)

class_def     = { decorator } "class" NAME [ type_params ] [ "(" base { "," base } ")" ] ":"   (* M15, §4.3 *)
                NEWLINE INDENT class_member { class_member } DEDENT ;
base          = NAME [ "." NAME ] [ "[" type { "," type } "]" ] ;   (* more than one class base is TP0225; protocols may be added: M17 *)
class_member  = NAME ":" type [ "=" expr ] NEWLINE | method_def | type_alias | "pass" NEWLINE ;   (* type_alias: M40 *)
method_def    = { decorator } [ "async" ] "def" NAME "(" [ method_params ] ")" [ "->" type ] ":" ( block | "..." NEWLINE ) ;   (* M5; "...": M17; async: M33 *)
method_params = ( "self" | "cls" ) { "," param } [ "," ] | params ;  (* M28: "cls", or neither, for class and static methods, §4.8 *)

enum_def      = "enum" NAME [ type_params ] ":" NEWLINE INDENT variant { variant } DEDENT ;
variant       = NAME [ "(" named_fields ")" ] NEWLINE ;
named_fields  = NAME ":" type { "," NAME ":" type } [ "," ] ;

block         = NEWLINE INDENT stmt { stmt } DEDENT ;

stmt          = simple_stmt NEWLINE | compound_stmt | type_alias ;   (* type_alias in a block: M40, §3.18 *)
simple_stmt   = let_stmt | var_stmt | assign_stmt | return_stmt | raise_stmt | del_stmt | yield_stmt
              | assert_stmt | "pass" | "break" | "continue" | expr ;
assert_stmt   = "assert" expr [ "," expr ] ;                         (* M44, §5.6 *)
del_stmt      = "del" target { "," target } ;                       (* M26, §5.5 *)
let_stmt      = "let" ( NAME [ ":" type ] | names ) "=" ( expr_list | yield_expr ) ;   (* yield_expr: M42 *)
var_stmt      = "var" ( NAME [ ":" type ] | names ) "=" ( expr_list | yield_expr ) ;
names         = unpacked "," unpacked { "," unpacked } [ "," ] ;      (* unpacking: M12, §3.8 *)
unpacked      = NAME | "*" NAME | "(" names ")" ;                    (* "*" and nesting: M29 *)
assign_stmt   = target ( "=" | "+=" | "-=" | "*=" | "/=" | "//=" | "%=" | "**=" ) ( expr | yield_expr )   (* "//=", "%=", "**=": M35; yield_expr: M42 *)
              | targets "=" expr_list ;                              (* M12 *)
targets       = unpack_target "," unpack_target { "," unpack_target } [ "," ] ;
unpack_target = target | "*" target | "(" targets ")" ;              (* "*" and nesting: M29 *)
target        = NAME | postfix "." NAME | postfix "[" subscript "]" ;   (* a slice: M26, §5.5 *)
return_stmt   = "return" [ expr_list ] ;
match_expr    = "match" expr ":" NEWLINE INDENT value_arm { value_arm } DEDENT ;   (* M19, §5.1.1; an atom since M29 *)
value_arm     = pattern [ guard ] ":" ( ( expr | raise_stmt ) NEWLINE | block ) ;   (* block: M29; guard: M38 *)
expr_list     = expr { "," expr } ;                                  (* two or more make a tuple: M12 *)
raise_stmt    = "raise" [ expr [ "from" expr ] ] ;                   (* M16, §3.10; "from": M41, §3.19 *)
yield_stmt    = yield_expr ;                                         (* M31, §3.13 *)
yield_expr    = "yield" [ [ "from" ] expr ] ;                        (* M31; a value, no value, and as an expression: M42, §3.20 *)

compound_stmt = if_stmt | while_stmt | for_stmt | match_stmt | try_stmt | with_stmt | func_def ;   (* in a block: nested function, M11 *)
with_stmt     = [ "async" ] "with" with_item { "," with_item } ":" block ;   (* M37, §3.17; async: M42, §3.20 *)
with_item     = expr [ "as" NAME ] ;                              (* a top-level 'as' here is the item's, not a conversion (§10.4) *)
try_stmt      = "try" ":" block { except_clause } [ "else" ":" block ] [ "finally" ":" block ] ;   (* M16, §3.10 *)
except_clause = "except" [ except_types [ "as" NAME ] ] ":" block ;
except_types  = qual_name | "(" qual_name { "," qual_name } [ "," ] ")" ;
if_stmt       = "if" condition ":" block { "elif" condition ":" block } [ "else" ":" block ] ;
condition     = expr "is" "some" NAME | expr ;
while_stmt    = "while" condition ":" block ;
for_stmt      = [ "async" ] "for" ( NAME | names ) "in" expr ":" block ;   (* nested and starred names: M29; async: M42 *)
match_stmt    = "match" expr ":" NEWLINE INDENT arm { arm } DEDENT ;
arm           = pattern [ guard ] ":" ( simple_stmt NEWLINE | block ) ;
guard         = "if" expr ;                                          (* M38, §5.1 *)
pattern       = or_pattern [ "as" NAME ] ;                           (* "as": M38 *)
or_pattern    = closed_pattern { "|" closed_pattern } ;              (* M38 *)
closed_pattern = "_" | [ "-" ] INT | [ "-" ] FLOAT | STRING | "True" | "False" | "None"   (* FLOAT, None: M38 *)
              | qual_name [ "(" [ pat_args ] ")" ]                   (* a bare NAME: a variant or a binding, M29, §5.1 *)
              | "(" pattern ")"                                      (* a group, M38 *)
              | "(" list_pat "," [ list_pat { "," list_pat } [ "," ] ] ")"   (* tuple: M29; "*": M38 *)
              | "[" [ list_pat { "," list_pat } [ "," ] ] "]"        (* list: M29 *)
              | "{" [ map_pat { "," map_pat } [ "," ] ] "}" ;        (* mapping: M38 *)
list_pat      = pattern | "*" NAME ;                                 (* "*_" ignores the rest *)
map_pat       = map_key ":" pattern | "**" NAME ;                    (* "**rest" last, M38 *)
map_key       = [ "-" ] INT | STRING | "True" | "False" ;
pat_args      = pat_arg { "," pat_arg } [ "," ] ;
pat_arg       = pattern | NAME "=" pattern ;                         (* keyword form: class patterns, M15; sub-patterns: M29 *)
qual_name     = NAME [ "." NAME [ "." NAME ] ] ;                    (* module.Enum.Variant: M9 *)

type          = base_type { "?" } ;                               (* more than one "?" is TP0213, §3.1 *)
base_type     = "int" | "float" | "bool" | "str" | "py"           (* py: M10 *)
              | "list" "[" type "]"
              | "dict" "[" type "," type "]"
              | "tuple" "[" type ( { "," type } | "," "..." ) "]"   (* M12; fewer than two types is TP0209; "...": M41, §3.19 *)
              | "Callable" "[" "[" [ type { "," type } ] "]" "," ( type | "None" ) "]"   (* M11, §3.7 *)
              | NAME [ "." NAME ] [ "[" type { "," type } "]" ] ;  (* module.Type: M9 *)

(* Expressions, lowest to highest precedence *)
expr          = lambda_expr | coalesce ;
lambda_expr   = "lambda" [ lambda_param { "," lambda_param } ] ":" expr ;   (* M11, §3.7 *)
lambda_param  = NAME | "_" ;
coalesce      = or_expr [ "??" coalesce ] ;                        (* right-assoc *)
or_expr       = and_expr { "or" and_expr } ;
and_expr      = not_expr { "and" not_expr } ;
not_expr      = "not" not_expr | comparison ;
comparison    = additive [ comp_op additive | "is" [ "not" ] ( "None" | additive ) ] ;   (* no chaining; 'is not None': M15; 'is' between objects: M40 *)
comp_op       = "==" | "!=" | "<" | "<=" | ">" | ">=" | "in" | "not" "in" ;
additive      = term { ( "+" | "-" ) term } ;
term          = unary { ( "*" | "/" | "//" | "%" ) unary } ;
unary         = "-" unary | "await" unary | power ;                (* await: M33, §3.14 *)
power         = cast [ "**" unary ] ;                               (* M35: right-associative, binding tighter than a unary minus on its left *)
cast          = postfix [ "as" type ] ;                            (* M10 *)
postfix       = atom { call | "." NAME | "[" subscript "]" } ;
subscript     = expr { "," expr } [ "," ] | [ expr ] ":" [ expr ] [ ":" [ expr ] ] ;   (* a slice: M12, §5.4; "x[a, b]" indexes with the tuple (a, b): M33 *)
call          = "(" [ arg { "," arg } [ "," ] ] ")"
              | "(" expr comp_for ")" ;                              (* a generator expression as the only argument: M36 *)
arg           = expr | NAME "=" expr | "*" expr | "**" expr ;        (* "*" and "**": M30, §4.2 *)
atom          = INT | FLOAT | STRING | FSTRING | "True" | "False" | "None"
              | NAME | "self" | "(" expr ")" | "..."                 (* "...": M17, a protocol method's body *)
              | "(" yield_expr ")"                                    (* M42, §3.20 *)
              | "(" expr "," [ expr { "," expr } [ "," ] ] ")"         (* a tuple: M12; one element is TP0209 *)
              | "[" [ expr { "," expr } [ "," ] ] "]"
              | "{" [ expr ":" expr { "," expr ":" expr } [ "," ] ] "}"
              | "{" expr { "," expr } [ "," ] "}"                 (* a set: M35, §3.15; "{}" is a dict *)
              | "[" expr comp_for "]" | "{" expr ":" expr comp_for "}" | "{" expr comp_for "}"   (* comprehensions: M36, §3.16 *)
              | "(" expr comp_for ")"                              (* a generator expression: M36 *)
              | match_expr ;                                         (* anywhere: M29, §5.1.1 *)
comp_for      = "for" ( NAME | names ) "in" coalesce { "for" ( NAME | names ) "in" coalesce | "if" coalesce } ;   (* M36, §3.16 *)
```

**Grammar notes**
- `a < b < c` is `TP0212`. Write `a < b and b < c`.
- An `arm` may take a simple statement on the same line (`Ok: print("ok")`) or an indented block.
- **`match` expressions (M19)** were only the whole right-hand side of a `let`, `var` or assignment, or the whole value of a `return`, before M29 (`TP0100`, `'match' can only be the whole value of 'let', 'var', an assignment or 'return'`). Since M29 a `match_expr` is an atom, so it may appear anywhere an expression may (§5.1.1). Its `:` must end its line (`TP0100`, `expected newline, found <token>`), and its arms are indented under it. Outside brackets, a statement whose last expression is a `match_expr` has no `NEWLINE` of its own: the arms' `DEDENT` ends it. A `match` at the start of a statement is always a `match_stmt`.
- A `value_arm` is an expression or a `raise` on the same line as its pattern, `Ok: "up"`, `_: raise ValueError("bad status")`, or since M29 an indented block (§5.1.1). Before M29 a block was `TP0100`, `expected expression, found newline`.
- **Unpacking (M29):** a parenthesized group in `names` or `targets` nests, and `*` marks the name or target that takes the rest. In `for`, `names` may also be parenthesized as a whole: `for (a, b) in pairs`. Two starred names at one level are `TP0100`, `an unpacking can have only one '*' at each level`; a starred name alone is `TP0100`, `a starred name can't be unpacked to alone`; and `*x` anywhere else is `TP0100`, `'*' can only mark a name or target being unpacked`. A trailing comma needs two names before it, so `let a, = t` is still `TP0100`.
- Keyword arguments are allowed in calls to functions and methods (M14, §4.2), class and enum-variant constructors (§4.3), `py` values (§10.2), and the keyword parameters of built-ins (`key` and `reverse` of `sorted` and `list.sort`, `start` of `enumerate`; §6). Keyword arguments may follow positional ones, as in Python. A positional argument after a keyword argument is `TP0100`.
- In a parameter list, a parameter without a default after one with a default is `TP0100`, as in Python. Since M30 that applies only before `*`, and the order of `/`, `*`, `*args` and `**kwargs` is checked as §4.2 says (`TP0100`).
- **Decorators (M15)** are only `@dataclass` on a `class` and `@override` on a method (§4.3), and since M28 `@property`, `@<name>.setter`, `@staticmethod`, `@classmethod` and `@abstractmethod` on a method (§4.8). A decorator takes no arguments, except `@dataclass(frozen=…, order=…)` since M39 (§4.9); arguments on any other decorator are `TP0224`. Since M39, `@final` on a class or method and `@total_ordering` on a class are valid too. Any other decorator name, a decorator in any other place, a repeated one, or one that can't be combined with another (§4.8) is `TP0224`. A dotted decorator other than `@<property>.setter` is `TP0224`.
- **Receivers (M28):** an instance method, property or setter must take `self` first, a `@classmethod` must take `cls` first, and a `@staticmethod` takes neither. Otherwise it's `TP0225`, `'<m>' must take 'self' first` (or `'cls'`, or `a static method takes neither 'self' nor 'cls'`). As before M28, `self` and `cls` are written without a type.
- **`is`:** `x is None` and `x is not None` are comparisons that give `bool`, usable in any expression (M15). `x is some v` binds a name, so it's only a whole `if`/`elif`/`while` condition. Before M40, any other right operand of `is`, including another expression (`x is y`), was `TP0100`, `expected 'some', 'not' or 'None'`. Since M40, `x is y` and `x is not y` compare identity (§3.18).
- **`try` (M16):** a `try` needs at least one `except` clause or a `finally`, and an `else` needs at least one `except`. A bare `except:` must be the last clause. Otherwise it's `TP0100`. An `except` clause names its types (`except KeyError`, `except zoo.NotFound`, `except json.JSONDecodeError`) rather than taking an expression, so in `except KeyError as e` the `as` names the binding rather than starting a cast.
- A class body holds field declarations, methods and `pass`. Classes are top-level only, so a `class` inside a block is `TP0100`.
- **Type aliases (M18)** were top-level only too until M40: a line that starts with `type`, a name and `=` or `[` inside a block or class body was `TP0100`, `a type alias must be at the top level`. Since M40 an alias may be a statement in a block or a member of a class body (§3.18).
- **Protocols (M17)** are written as classes with the base `Protocol`, and their methods' bodies as `...` (§3.11). The parser treats them as any other class, and `...` as any other atom. The type checker applies the protocol rules.
- **Type parameter bounds (M17):** in `[T: Comparable]` the bound is a type, and in `[N: (int, float)]` a parenthesized constraint tuple. `[T: (Comparable)]` is a one-type constraint (`TP0220`), not a bound.
- **`...` (M17)** is an atom, so it parses wherever an expression may appear, and a method's body may be `...` on the same line as its colon: `def area(self) -> float: ...`. Only a protocol method's body may be `...` (§3.11).
- A `def` at the top level is a `top_item`, a hoisted global function. A `def` inside a block is a nested function (§3.7).
- An `expr_list` of two or more expressions is a tuple, as if parenthesized (§3.8): `return lo, hi`, `let p = 1, 2`, `a, b = b, a`. An expression statement can't be a bare tuple: `a, b` alone is `TP0100`.
- In a cast (`x as T`), a `??` after the type is the coalescing operator, so `x as int? ?? 0` is `(x as int?) ?? 0`.
- In a type, the lexer's `??` token counts as two `?`, so `int??` parses and the type checker reports `TP0213`.
- An f-string hole is parsed as exactly one `expr`. Its tokens come from re-lexing the hole's text (§1.3), and positions in its diagnostics refer to the original file.

### 2.1 Syntax errors
- A syntax error is `TP0100`. The message is `expected X, found Y`, or one of the specific messages below.
  - **X** is a token, or one of the categories `expression`, `type`, `statement`, `pattern`, `indented block`.
  - **Y** describes the token found: a keyword or punctuation token in quotes (`':'`, `'else'`), `name 'x'`, `integer`, `float`, `string`, `f-string`, `newline`, `indent`, `dedent`, or `end of file`.
  - The error points at the start of the token found.
  - Specific messages: `cannot assign to this expression` (an `assign_stmt` target that isn't a `target`), `positional argument follows keyword argument`, `parameter without a default follows parameter with a default` (M14), `a bare 'except' must be the last except clause` (M16, at the bare `except`, after which parsing continues normally), `a type alias must be at the top level` (M18, at `type`, until M40), and `'match' can only be the whole value of 'let', 'var', an assignment or 'return'` (M19, at `match`). A `try` with no `except` or `finally`, or an `else` with no `except`, is `expected 'except' or 'finally', found …`.
- A chained comparison is reported by the parser as `TP0212`, with message `chained comparison not supported` and help `write 'a < b and b < c'`. Parsing continues normally.
- **Recovery:** after a `TP0100`, the parser skips tokens up to and including the next `NEWLINE` at the current block depth. If the next line is indented further (an `INDENT` follows), that whole block is skipped as well. A `DEDENT` that closes the current block stops the skip without being consumed. Parsing then resumes at the next statement. So each statement reports at most one `TP0100`, and independent errors on separate statements are all reported.

---

## 3. Types

### 3.1 Built-in types

| Taipan | Meaning | C# mapping |
|---|---|---|
| `int` | 64-bit signed integer; overflow panics | `long` (checked) |
| `float` | IEEE 754 double | `double` |
| `bool` | `True` / `False` | `bool` |
| `str` | immutable Unicode string | `string` |
| `list[T]` | growable array | `List<T>` |
| `dict[K, V]` | hash map that keeps insertion order; `K` ∈ {`int`, `str`, `bool`}, a tuple of valid keys (M12), or a plain class, compared by identity (M15), or since M28 any hashable class (§4.8), otherwise `TP0200` | `OrderedDictionary<K, V>` |
| `tuple[A, B, …]` | fixed-size, immutable group of values (M12, §3.8) | `ValueTuple<A, B, …>` |
| `set[T]` | unordered collection of distinct values, kept in insertion order (M35, §3.15); `T` as a dict key | `Taipan.Runtime.OrderedSet<T>` |
| `T?` | optional `T` | `T?` (C# nullable) |
| `py` | dynamic Python object (M10, §10) | `Python.Runtime.PyObject` |
| `C` | instance of a class `C` (M15, §4.3) | a C# class (§8) |
| `Exception`, `ValueError`, … | built-in exception classes (M16, §3.10) | classes in `Taipan.Runtime` (§8) |
| `TextIO` | an open text file, or a standard stream (M37, §6.1) | `Taipan.Runtime.TextFile` |
| `type[C]` | the class `C` or a class deriving from it, as a value; only `__exit__` receives one (M37, §3.17) | `Taipan.Runtime.ClassValue` |
| `TracebackType` | where an exception was raised, from `types` (M37, §3.17) | `Taipan.Runtime.Traceback` |
| `Callable[[A, B], R]` | function value (M11, §3.7) | `Taipan.Runtime.Function<D>` (§8) |
| `Iterator[T]`, `Iterable[T]` | single-pass stream, and anything a `for` loop goes over (M31, §3.13) | `Taipan.Runtime.IIterator<T>`, `IEnumerable<T>` (§8) |
| `Generator[Y, S, R]` | a generator that also receives `S`s and returns an `R` (M42, §3.20) | `Taipan.Runtime.IGenerator<Y, S, R>`, which `Taipan.Runtime.Generator<Y, S, R>` implements |
| `AsyncIterator[T]`, `AsyncIterable[T]`, `AsyncGenerator[Y, S]` | async streams, for `async for` (M42, §3.20) | `Taipan.Runtime.IAsyncIterator<T>`, `IAsyncIterable<T>`, `AsyncGenerator<Y, S>` |
| `object` | any value; `StopIteration.value` gives one (M42, §3.20), and since M43 .NET members (§13.3) | `object?` |
| `Comparable`, user protocols | structural interface: a type parameter bound (M17, §3.11), and since M40 a type (§3.18) | a C# interface for a protocol used as a type (M40); nothing for a bound, since bounded generics are monomorphized |

- A function without `-> type` returns nothing (C# `void`). Using its result as a value is `TP0200`. Since M26, `-> None` says the same explicitly, as in Python's type hints, for functions, methods (including `__init__`) and nested `def`s; a function declared `-> None` is the same as one without a return type in every way, its display and function type (`Callable[[…], None]`) included.
- `T??` is `TP0213`. Optionals don't nest.

### 3.2 Inference
- **Local inference only.** A `let` or `var` without an annotation takes the type of its initializer.
- **Function parameters must be annotated.** The return type is declared, or the function returns nothing.
- `[]` and `{}` without an expected type are `TP0206`. The expected type can come from an annotation, a parameter, a return type, or an assignment target.
- List literals need all elements to have the same type, except that `int` and `float` mix to `float`, and classes with a common base mix to their nearest common base (M15): `[Dog("a"), Cat("b")]` is `list[Animal]`.

### 3.3 Assignability
`S` is assignable to `T` when one of these holds:
- `S == T`
- `S` is `int` and `T` is `float` (implicit widening)
- `T` is `U?` and `S` is assignable to `U`
- `S` is the type of `None` and `T` is any optional
- `S` is a class that derives from `T`, directly or indirectly, or `S` is `V?` and `T` is `U?` for such classes (M15, §4.3). So a `Dog` can be passed where an `Animal` is expected, and a `Dog?` where an `Animal?` is.
- `T` is a protocol type that `S` satisfies, or `S` is `V?` and `T` is `P?` for such a `V` and `P` (M40, §3.18). A type parameter bounded by a class is assignable to that class (M40).
- `S` is `Callable[[P1, …, Pn], R]` and `T` is `Callable[[Q1, …, Qn], U]`, where each `Qi` is assignable to `Pi`, and `R` is assignable to `U` or both are `None` (M15). Parameters go the opposite way to results: a function that handles any `Animal` can stand in for one that handles a `Dog`, and one that returns a `Dog` for one that returns an `Animal`.

Other generic types are invariant. `list[Dog]` isn't assignable to `list[Animal]`, since code holding it as a `list[Animal]` could append a `Cat`. The same holds for `dict`, tuples and type arguments of user generics. This is C#'s rule and mypy's (except that mypy lets tuples vary), but Python itself never checks it. A literal is checked against its expected type, so `let xs: list[Animal] = [Dog("rex")]` is still valid.

There are no other implicit conversions. Explicit conversions use the built-ins `int(x)`, `float(x)`, and `str(x)` (§6), and `as` converts between related classes (§4.3).

### 3.4 Optionals and narrowing
- A value of type `T?` can't be used where `T` is expected (`TP0203`). Member access and indexing on `T?` are also `TP0203`.
- These forms narrow it:
  - `if x is some v:` binds `v: T` in that branch only.
  - `if x is not None:` and `if x is None: … else: …` narrow `x` itself, when `x` is a name that can't change (M15, §3.9).
  - `x ?? default` has type `T` when `default: T`.
  - Narrowing of `x` after an early return (`if x is None: return`) is post-M15.
- `x == None` is allowed and returns `bool`. Before M40 it didn't narrow; since M40 it narrows as `x is None` does (§3.18).
- **Using an optional:**
  - A `T?` is `TP0203` wherever a `T` is expected. That includes operands of operators other than `==`/`!=`, the target of member access, indexing or a method call, and the iterable of a `for`.
  - It's `TP0200` where some unrelated type is expected.
  - `==` and `!=` may compare a `T?` with a `T`, a `T?` or `None`. The result is `True` only when both are `None`, or both hold equal values.
- **`is some` / `is None` / `is not None`:**
  - The tested expression is evaluated once and must be optional. Otherwise it's `TP0200`.
  - `x is None` and `x is not None` give `bool` and may appear in any expression (M15): `return x is None`, `if x is not None and x > 0:`.
  - The binding `v` is a `let` and follows the usual shadowing rules (`TP0207`).
  - In `while x is some v:`, `x` is re-tested on each iteration and `v` is bound in the body.
  - In an `elif` clause, the binding is visible only in that clause's branch.
- **`??`:**
  - The left operand must be optional (`TP0200` otherwise) and is evaluated first. The right operand is evaluated only when the left is `None`.
  - With `x: T?`, the default is checked against `T` as its expected type, and the result is `T`. If the default is itself a `T?`, the result is `T?`, so `a ?? b ?? 0` works.
- **`None` and inference:**
  - `None` has its own type, written `None`, which is assignable to every optional.
  - `None` is spelled as in Python (M13). Lowercase `none` is an ordinary name, so an undefined `none` is `TP0201`, with help `use 'None'`.
  - `let x = None` without an annotation is `TP0206`.
  - In list and dict literals without an expected type, `None` and `T` mix to `T?`, and `T` and `T?` mix to `T?`. So `[1, None]` is `list[int?]`.
- **Nesting (`TP0213`):** `T??` is reported at the type. Optionals never nest. `d.get(k)` on a dict whose values are already optional returns that same optional type: on a `dict[str, int?]` it gives `int?`, and a missing key and a stored `None` both give `None` (as in Python, and as with generics, §3.6).

### 3.5 Conditions
`if`, `elif`, and `while` conditions and the operands of `and`, `or`, and `not` must be `bool`. There's no truthiness, so `if xs:` is `TP0205`.

### 3.6 Generics (M8)
- **Declarations:** functions, classes and enums may take type parameters, in PEP 695 style: `def first[T](xs: list[T]) -> T?:`, `class Box[T]:`, `enum Maybe[T]:`.
  - Type parameter names live in the type namespace of their definition, including a generic class's methods. Methods take no type parameters of their own.
  - A repeated type parameter is `TP0214`. One that shadows an existing type name is `TP0207`.
- **Instantiation in types:** a generic class or enum must be given exactly as many type arguments as it has parameters (`Box[int]`, `Maybe[list[str]]`). A count mismatch, including a bare `Box` or arguments on a non-generic type, is `TP0209`.
- **Type parameters are opaque** unless they have a bound (M17, §3.11; a class bound since M40, §3.18) or a constraint (M8.1, below). Bodies are checked once, with each type parameter treated as an opaque type `T`:
  - A `T` may be assigned, passed, returned, stored in lists and fields, compared with `==`/`!=` against another `T`, tested with `in` on a `list[T]`, and displayed (`print`, `str`, f-strings).
  - Anything else on a `T` is `TP0200` or `TP0210`, as for any type lacking the operation: arithmetic, ordering, member access, method calls, indexing, iteration, `match`, or use as a dict key.
  - So a body that checks is valid for every type argument. Instantiation never reports new errors (apart from `TP0219`).
- **Type arguments are always inferred.** There's no explicit call-site syntax.
  - **Expected type first:** if the call has an expected type (§4.6), the return type is unified with it.
  - **Then arguments:** the remaining parameters are inferred from the argument types, in order. Lambda arguments come last (§3.7).
  - An argument whose type doesn't fit what the expected type inferred overrides it. So `let b: Box[float] = Box(1)` keeps `T = float`, but `let x: int? = first(xs)` with `xs: list[int?]` infers `T = int?`.
  - Arguments are then checked against the substituted parameter types, with the usual widening.
  - When two arguments infer `int` and `float` for the same parameter, it's `float`. When they infer two classes with a common base, it's their nearest common base (M15), so `pick(dog, cat)` infers `Animal`. Any other disagreement is `TP0200`.
  - A `None` argument infers nothing, and an argument of type `U?` for a `T?` parameter infers `T = U`.
  - A parameter left undetermined is `TP0206`. For example, `Nothing` from `enum Maybe[T]` has no expected type to infer from.
- Generic enum variants and generic class constructors infer their type arguments the same way: `Box(5)` is `Box[int]`, and `Just("x")` is `Maybe[str]`.
- **Optionals don't nest, even through substitution:** `T?` with `T = int?` is `int?`, as in Python's `Optional[Optional[int]]`. Only a literal `T??` in source is `TP0213`.
- **Constraints (M8.1):** a function's type parameter may list the types it can stand for, as a PEP 695 constraint tuple: `def total[N: (int, float)](xs: list[N]) -> N:`.
  - **Valid tuples:** at least two distinct types, none of them optional and none mentioning a type parameter. Otherwise it's `TP0220`.
  - **Functions only:** constraints on a class's or enum's type parameters are also `TP0220` in v0.1.
  - **Checking:** the body is checked once for every combination of its constrained parameters' types, with each one replaced by that concrete type. Unconstrained parameters stay opaque.
    - So a constrained parameter allows exactly what all of its types support: `+` for `(int, float)`, `<` for `(int, str)`, and so on.
    - A diagnostic that only some combinations produce is reported once, with ` (when 'N' is str)` appended to its message. One that every combination produces is reported once, as usual.
  - **Signatures:** `dict[K, V]` is a valid type when every type in `K`'s tuple is a valid key, as with `[K: (int, str, bool)]`.
  - **Calls:**
    - After inference (above), each constrained parameter must be one of its listed types. An inferred `int` becomes `float` when `float` is listed and `int` isn't.
    - Anything else is `TP0200`, at the callee. That includes an opaque type parameter from an enclosing generic body.
  - **Instances:** each instantiation emits the body checked for its combination.
- **Polymorphic recursion:** a generic function that, through its own calls, needs ever-larger instantiations (for example, `f[T]` calling `f` with a `list[T]`) is `TP0219`. It's reported once the instantiation chain is 32 deep.

### 3.7 Functions as values (M11)
- **Function types** are written as in Python's `typing`. `Callable[[int, str], bool]` takes an `int` and a `str` and returns a `bool`, and `Callable[[], int]` takes nothing. A result of `None` means the function returns nothing: `Callable[[str], None]`.
  - `Callable` is a built-in type name, like `list`. A bare `Callable` is `TP0209`.
  - A function type is assignable to another whose parameter types are assignable to its own and whose result type its own is assignable to (§3.3, M15). Before M15 they had to match exactly. A lambda is checked against its expected type, so `lambda x: x` fits `Callable[[int], float]`.
  - A function value converted this way is still the same value: it compares equal (`==`) to the original and displays the same way.
  - Function types may appear wherever a type can: as `T?`, in lists, as dict values, as fields, and as type arguments. They can't be dict keys, and they aren't convertible to Python (§10.3).
- **Function values** are:
  - the name of a top-level function that isn't generic, used without calling it (`apply(double, xs)`)
  - a nested `def`
  - a `lambda`
  - Methods aren't values (`TP0200`, §9.1). Wrap them in a lambda instead: `lambda s: s.total()`. Generic functions, built-in functions, class constructors and enum variants weren't values either until M40; since M40 they are (§3.18).
- **Calls:** any expression of function type can be called: `f(x)`, `handlers[0](req)`, or `s.on_done()` for a field. Arguments are positional only (`TP0218`), since a function type has no parameter names or defaults, as in Python's typing. They're checked against the parameter types. A wrong count is `TP0209`. Calling a `T?` is `TP0203`.
- **Lambdas:** `lambda x, y: x + y`. The body is a single expression, whose value is the result.
  - A lambda parameter can't have a default: `TP0100`, `lambda parameters can't have defaults` (M26). Python's `lambda i=i: i`, which captures a loop variable's current value, isn't needed, since each iteration has its own variable (below), so the help says `each loop iteration has its own variable, so 'lambda: i' already sees this iteration's value`.
  - Parameters take no annotations. Their types come from the expected type (§4.6), which must be a function type with as many parameters as the lambda has (`TP0209` otherwise).
  - The body is checked against the expected result type. When the result is `None`, the body is checked as an expression statement, and its value is discarded.
  - Without an expected type, a lambda with no parameters takes its body's type (`let f = lambda: 0` is `Callable[[], int]`). One with parameters is `TP0206`.
  - Where the expected type isn't a function type, a lambda is `TP0200`.
  - Parameters are immutable and follow the shadowing rules (`TP0207`). A `_` parameter is ignored and may repeat. Any other repeated name is `TP0214`.
- **Nested functions:** a `def` inside a block, whether a function body or a block of a top-level statement, declares a local function.
  - Its name is a `let` binding, visible from the `def` to the end of the block and inside its own body, so it may recurse. It follows the usual shadowing rules.
  - A nested function can't have type parameters (`TP0223`), but it may use those of an enclosing generic function.
  - `return`, `break` and `continue` in its body belong to the nested function. A loop around the `def` isn't its loop (`TP0215`).
- **Capture:** a lambda or nested function may use any binding visible where it's written. That includes parameters, `self`, and top-level `let`/`var` bindings when it's written at the top level. A function nested inside a top-level function still can't see top-level variables (`TP0211`).
  - A captured `var` is shared, not copied. The function sees later assignments to it and may assign to it itself. There is no `nonlocal`, since assignment never declares.
  - A `let`, a parameter or a loop variable can't change, so capturing one captures its value.
  - Each loop iteration gets its own loop variable and its own body bindings. So the functions made by `for i in range(3): fs.append(lambda: i)` return 0, 1 and 2, where in Python they'd all return 2.
- **Equality and display:**
  - Function values compare by identity, as in Python. A top-level function is always the same value, and each evaluation of a `lambda` or nested `def` makes a new one.
  - Ordering operators on functions are `TP0200`.
  - Functions display as `<function name>`, and lambdas as `<function <lambda>>`.
- **Inference with function arguments:** in a call to a generic function (§3.6), lambda arguments are checked after all other arguments, in order.
  - Each lambda's expected type is its parameter's type, with the type arguments inferred so far substituted. Its parameter types must then be fully known, otherwise it's `TP0206`.
  - The body's type then infers type parameters in the result position, so `map(lambda s: s.name, services)` is `list[str]`.
  - A body that returns nothing can't determine a type parameter (`TP0200`).
  - A function-valued argument that isn't a lambda infers from its type like any other argument, matching parameter and result types exactly.

### 3.8 Tuples (M12)
- **Types:** `tuple[int, str]` holds an `int` and a `str`, as in Python.
  - A tuple has a fixed number of elements, at least two. `tuple[int]` is `TP0209`. Variable-length `tuple[int, ...]` exists since M41 (§3.19); the rules below are for fixed tuples.
  - `tuple` is a built-in type name, like `list`.
  - Elements may have any type that has values, including optionals, lists, functions and other tuples.
  - A tuple type is assignable only to an identical one, as for lists. A literal is checked against its expected type, though, so `let p: tuple[float, float] = (1, 2)` is valid.
- **Literals:** `(1, "a")`, with an optional trailing comma.
  - `(x,)` is `TP0209`. `()` is `TP0100`.
  - Without parentheses, a tuple can follow `return` or the `=` of a `let`, `var` or assignment (§2): `return lo, hi`, `a, b = b, a`.
  - Elements are evaluated left to right. With an expected tuple type, each element is checked against its element type. A literal with the wrong number of elements is `TP0200`.
- **Element access:** `t[0]`, or `t[-1]` from the end.
  - The index must be an integer literal, optionally negative, within the tuple's length. Otherwise it's `TP0200`.
  - Tuples are immutable: `t[0] = x` is `TP0202`.
- **Unpacking:**
  - `let lo, hi = bounds(xs)` and `var a, b = pair` declare one binding per element.
  - `for i, x in enumerate(xs)` unpacks each element into loop variables.
  - `a, b = b, a` assigns to existing targets.
  - The value must be a tuple, or since M29 a list (`TP0200` otherwise). A tuple must have exactly as many elements as there are names (`TP0209`).
  - `_` ignores an element and may repeat. Unpacking declarations take no annotation: each binding gets its element's type.
  - **Nesting (M29):** a parenthesized group unpacks an element in turn: `let (a, b), c = (pair, 3)`, `for i, (k, v) in enumerate(d.items())`, `(x, y), z = …`. Each level follows these rules.
  - **Lists (M29):** `let first, second = xs` unpacks a `list[T]`, each name a `T`. Its length is checked at run time: too few elements raises `ValueError`, `not enough values to unpack (expected 2, got 1)`, and too many `ValueError`, `too many values to unpack (expected 2)`, as in Python 3.12.
  - **A starred name (M29)** takes the rest as a new list: `let first, *rest = xs`, `head, *_, last = line.split(",")`. At most one name per level may be starred (`TP0100`). It needs at least as many elements as the other names, or raises `ValueError`, `not enough values to unpack (expected at least 2, got 1)`. In a tuple, the starred name gets a `list` of the remaining elements' joined type, as a list literal's elements join (§4.6), or `TP0200` if they don't. A starred name alone, `*rest = xs`, is `TP0100`, as in Python.
  - In an assignment, the right side is evaluated first. Then, left to right, each target's own sub-expressions are evaluated and the value is stored, so `a, b = b, a` swaps. Every target follows the rules for `=` (`TP0202`). A compound form such as `a, b += 1` is `TP0100`.
- **Equality and ordering:**
  - `==` and `!=` compare tuples element by element, structurally, as for lists. Two tuple types are comparable when they have the same length and each pair of element types is comparable.
  - `<`, `<=`, `>` and `>=` compare lexicographically, as in Python, when every element type is ordered: `int`, `float`, `str`, or a tuple of those. So `sorted(xs, key=lambda s: (s.healthy, s.name))` works.
- **Dict keys:** a tuple of valid keys is itself a valid key: `dict[tuple[int, int], str]`.
- **`match`:** since M29, tuple patterns match tuples (§5.1). Before M29, a tuple couldn't be matched on (`TP0200`).
- **Display:** `(1, "a")`, with elements written as inside a list.
- **Generics:** tuple types may be type arguments, and inference matches them element by element.

### 3.9 Narrowing (M15, M27)
Some conditions narrow a name's type in the code they guard, as mypy does, and since M27 in the code that follows them when the other path can't continue:

```python
def label(s: Service?) -> str:
    if s is None:
        return "none"
    return s.name                # s is a Service here

def first_port(config: Config) -> int:
    if config.port is None:
        raise ValueError("no port")
    return config.port + 1       # config.port is an int here
```

- **The conditions:**
  - `isinstance(x, C)` narrows `x` to the class `C` (§4.3). On an `x` of type `A?`, it also rules out `None`, since `isinstance(None, C)` is `False`.
  - `x is not None` narrows `x: T?` to `T`.
  - `x is some v` (M27) narrows `x` as `x is not None` does, besides binding `v`.
- **What can be narrowed:**
  - A name: a `let`, a parameter (including `self`), a loop variable, a `match` or `is some` binding, and since M27 a `var` (below).
  - Since M27, a *path* of fields, `p.x` or `self.config.port`, whose root is a name that can't change: a `let`, a parameter, `self`, a loop variable or a binding.
  - Since M40, an element with a fixed key (`xs[0]`), and a path rooted at a `var` (§3.18).
  - Nothing else, such as a call's result. Copy it to a `let` first.
- **When a condition is true**, these narrowings hold:
  - `isinstance(x, C)` and `x is not None` narrow as above.
  - `a and b` narrows by both `a` and `b`.
  - `not a` narrows by whatever holds when `a` is false.
  - `(a)` narrows as `a` does.
- **When a condition is false**, these hold:
  - `x is None` narrows `x: T?` to `T`.
  - `a or b` narrows by whatever holds when `a` and `b` are both false.
  - `not a` narrows by whatever holds when `a` is true.
- **Where narrowing applies:**
  - In `if c:`, the `if` block sees what holds when `c` is true. Each later `elif` condition and block, and the `else` block, see what holds when `c` is false. So does everything after earlier `elif` conditions that were false.
  - **After an `if` statement (M27)**, the rest of the block sees what holds at the end of every branch that can finish normally (§4.5): a branch ending in `return`, `raise`, `break` or `continue` doesn't count. A missing `else` is a branch that finishes with every condition false. So after `if x is None: return`, `x` is narrowed. If no branch can finish, nothing after the `if` runs (and when every branch terminates, §4.5's `TP0300` applies).
  - **After a `match` statement (M27)**, likewise: what holds at the end of every arm that can finish normally.
  - In `while c:`, the body sees what holds when `c` is true. **After the loop (M27)**, if its body has no `break` for it, the rest sees what holds when `c` is false.
  - **Loops (M27):** a loop's body, and a `while` condition, run more than once, so they see what held before the loop less whatever the body can end (below). After a `for` loop, which may not run at all, the same holds.
  - **`try` (M27):** its `except`, `else` and `finally` blocks, and the code after it, see what held before the `try` less whatever any of its blocks can end. Narrowing found inside the `try` block doesn't reach them, since an exception could come from anywhere in it.
  - In `a and b`, `b` sees what holds when `a` is true. In `a or b`, `b` sees what holds when `a` is false. So `x is None or x > 0` is valid.
  - `match` class patterns narrow the scrutinee inside their arm (§5.1).
- **Lambdas and nested functions** written in a narrowed region see narrowed `let`s, parameters and bindings, which can't change. They don't see narrowed `var`s or paths, since they run later, when those may have changed.
- **What ends narrowing (M27):**
  - **A `var`** stays narrowed until it's assigned. Assigning a value whose type is narrower than the `var`'s narrows it to that type: a non-optional value to a `T?` `var`, or an instance of a subclass. So `var x: int? = None`, then `x = 5`, then `x + 1` is valid, and so is `var name: str? = "x"` then `name.upper()`, since the declaration assigns too. Unpacking (§3.8) and compound assignments assign too.
  - The optional tests, `is None`, `is not None`, `is some` and `??`, see a narrowed name or path as its declared type, so testing a `var` that's been assigned a value isn't an error.
  - A `var` that a nested function assigns (§3.7) isn't narrowed from that function's `def` on, nor anywhere in a loop containing it, since any call could run the function.
  - **A path** stays narrowed until something could change it:
    - any call that could run Taipan or foreign code: a Taipan function, method, constructor or function value, `super()`, any operation on a `py` value, and any .NET member; and a built-in given a function (`map`, `filter`, and `sorted` or `sort` with a `key`). Other built-ins (`len`, `print`, `str`, list and dict methods) don't end it.
    - since M28, anything that runs a special method or property (§4.8): reading or setting a property, and an operator, `==`, `in`, `contains`, `print`, `str()`, `repr()`, `hash()`, an f-string hole, or a dict access, on a class value, or a list, dict, tuple or optional holding one, when that class or a class derived from it defines the special method used.
    - any assignment to a field with the name of one of the path's fields, on any object, since another name could refer to the same object.
  - Ending is ordered as the code runs: in `p.x is not None and check(p)`, the call ends the narrowing of `p.x`, so the `if` block doesn't see it.
- **Errors:** using a name or path as if it were narrowed reports the usual error (`TP0203`, `TP0210`), with help saying why it isn't:
  - `it can be None again after the assignment on line <n>` for a `var`, or `it was assigned again on line <n>` for one narrowed by `isinstance`;
  - `a nested function assigns it, so it isn't narrowed` for a `var` a nested function assigns;
  - `a lambda or nested function runs later, when it may have changed; copy it to a 'let' first` for a narrowed `var` or path used in one;
  - `the call on line <n> could have changed it; copy it to a 'let' first`, or `the assignment to '.<f>' on line <n> could have changed it; copy it to a 'let' first`, for a path;
  - `copy it to a 'let' first` for anything else that can't be narrowed, such as `xs[i]` with a `var` index (M40, §3.18).
- A narrowed name or path has the narrowed type for every use: member access, calls, arguments and assignments. A name is still the same binding, so it follows the shadowing rules as before.
- `isinstance(x, C)` where `C` is `x`'s own class or a base of it doesn't narrow `x` further, apart from ruling out `None`.

### 3.10 Exceptions (M16)
Exceptions are Python's: classes deriving from `Exception`, raised with `raise` and handled with `try`/`except`/`finally`. Every runtime error of §7 raises one, so it can be caught. A panic is an exception that nothing catches.

```python
class NotFound(Exception):
    key: str

    def __init__(self, key: str):
        self.key = key
        super().__init__(f"no service named {key}")

def lookup(services: dict[str, int], name: str) -> int:
    try:
        return services[name]
    except KeyError:
        raise NotFound(name)

try:
    let doc = json.loads(text)
except json.JSONDecodeError as e:
    print("bad config:", e.message)
finally:
    print("done")
```

**Exception classes**
- The built-in exception classes are plain classes (§4.3), in Python's hierarchy:

  | Class | Base | Raised by Taipan for (§7) |
  |---|---|---|
  | `BaseException` (M37) | | |
  | `SystemExit` (M37) | `BaseException` | `sys.exit` (§6.1) |
  | `Exception` | `BaseException` (since M37) | |
  | `ArithmeticError` | `Exception` | |
  | `ZeroDivisionError` | `ArithmeticError` | division by zero |
  | `OverflowError` | `ArithmeticError` | int overflow, and `int(x)` of an infinite or out-of-range float |
  | `LookupError` | `Exception` | |
  | `IndexError` | `LookupError` | an index out of range, and `pop()` on an empty list |
  | `KeyError` | `LookupError` | a missing dict key; a missing set value in `remove` and `pop` from an empty set (M35) |
  | `ValueError` | `Exception` | `int(x)` of a NaN, `split("")` and `replace("", …)`, and a slice step of 0; since M35, `min`/`max` of an empty iterable, a missing value in `list.remove`/`index` and `str.index`, a `range` step of 0, and a negative `int` exponent |
  | `TypeError` | `Exception` | a failed `as` (§4.3, §10.4) |
  | `RuntimeError` | `Exception` | changing a list, dict or set (M35) while iterating over it; `StopIteration` escaping a generator (M31) |
  | `StopIteration` | `Exception` | `next()` on an exhausted iterator, or a class's `__next__` ending (M31, §3.13), and a generator's `send` when it finishes (M42) |
  | `GeneratorExit` (M42) | `BaseException` | a generator's `close()` (§3.20) |
  | `AssertionError` (M44) | `Exception` | a failed `assert` (§5.6) |
  | `StopAsyncIteration` (M42) | `Exception` | an async generator's `__anext__` when it finishes (§3.20) |
  | `CancelledError` (M42) | `BaseException` | cancelling an async function (§3.20); its name is `asyncio.CancelledError`, not a global |
  | `PythonError` | `Exception` | a Python exception from a `py` operation (§10.5) |
  | `DotNetError` | `Exception` | a .NET exception from a .NET call, caught as `Exception` (M21, §13.6) |
  | `EOFError` (M37) | `Exception` | `input()` at the end of its input |
  | `OSError` (M37) | `Exception` | a file operation the system refuses, and reading or writing a file not opened for it (§6.1) |
  | `TimeoutError` (M42) | `OSError` | `asyncio.wait_for` and `asyncio.timeout` running out (§3.20) |
  | `FileNotFoundError` (M37) | `OSError` | opening a missing file |
  | `FileExistsError` (M37) | `OSError` | opening an existing file with mode `x` |
  | `PermissionError` (M37) | `OSError` | opening a file without permission |
  | `IsADirectoryError` (M37) | `OSError` | opening a directory |
  | `UnicodeError` (M37) | `ValueError` | |
  | `UnicodeDecodeError` (M37) | `UnicodeError` | reading bytes that aren't valid in the file's encoding |
  | `UnicodeEncodeError` (M37) | `UnicodeError` | writing a character the file's encoding can't represent |

- `Exception` has one field, `message: str`, and an `__init__(self, message: str = "")`, both from `BaseException` since M37. Every built-in class except `PythonError` and `SystemExit` takes the same arguments: `raise ValueError("bad port")`. Python has no `message` field; it keeps the arguments in `args`, and `str(e)` gives the message. In Taipan, `str(e)` gives the message too (§6), and since M41 `args` holds it (§3.19).
- `PythonError` has two more fields: `type: str`, the Python exception's class name, and `value: py`, the Python exception object. Its `message` is Python's `str(e)`. Only the runtime creates one: constructing it is `TP0200`, and deriving from it is `TP0225`. The same holds for `DotNetError` (M21), whose one more field is `type: str`, the .NET exception's full class name (§13.6).
- The built-in exception classes' names are global, like other classes', so a definition with one of their names is `TP0214`.
- **`BaseException` (M37)** is the root, as in Python, so that `SystemExit` isn't an `Exception`: `except Exception` doesn't catch it, and a program's `sys.exit` passes through handlers meant for errors. `except BaseException` and `except SystemExit` do catch it. Unlike Python, a bare `except:` is still `except Exception:` (below). A user class may derive from `BaseException`, as from `Exception`.
- **`SystemExit` (M37)** has `__init__(self, code: int = 0)` and one more field, `code: int`; its `message` is `str(code)`, as Python's `str(e)` is. `sys.exit(message)` creates one whose `code` is `1` and whose `message` is `message` (in Python, `code` would be the string).
- **`OSError` (M37)** has three more fields, as in Python: `errno: int?`, `strerror: str?` and `filename: str?`. Those the runtime raises (§6.1) set all three, and their message is Python's, `[Errno 2] No such file or directory: 'data.txt'`, with the file name quoted as Python's `repr` quotes it. Constructed with `OSError(message)`, as the others are, all three are `None`.
- **User exceptions** are plain classes deriving, directly or not, from `Exception`. They follow every rule of §4.3: a subclass with its own fields sets them and then calls `super().__init__(message)`, and one without `__init__` takes `Exception`'s `message` argument. Since M41 an exception may also be a dataclass, `@dataclass class NotFound(Exception)` (§3.19); before, that was `TP0225`.
- Exceptions compare by identity, like other plain classes, and may be stored, passed and returned like any value.

**`raise`**
- `raise e` raises `e`, which must be an `Exception` (a class deriving from it), or since M37 any `BaseException`. Anything else is `TP0200`. Raising the class itself, as in `raise ValueError`, which Python allows, is `TP0200` with help `write 'ValueError(…)'`.
- A bare `raise` re-raises the exception being handled. It's valid only inside an `except` block, and not in a function nested in one (`TP0215`).
- A `raise` terminates (§4.5).

**`try`**
- The `try` block runs first. If it raises, the first `except` clause that matches handles the exception. If none matches, the exception propagates.
- The `else` block runs only if the `try` block finishes without raising, returning, breaking or continuing. An exception raised in an `except` or `else` block isn't caught by the same `try`'s clauses.
- The `finally` block runs last, on every path out of the `try`: normally, through `return`, `break` or `continue`, and while an exception propagates. If the `finally` block raises, its exception replaces the one in flight, as in Python.
- `return`, `break` and `continue` aren't allowed in a `finally` block (`TP0215`). In Python they silently discard a propagating exception, and Python 3.14 warns about them (PEP 765).

**`except` clauses**
- `except C` matches an exception of class `C` or a class deriving from it. `C` must be `Exception` or derive from it (since M37, `BaseException` or derive from it), and may be qualified with a module: `except zoo.NotFound`. A bare `except:` is `except Exception:`. `except (A, B)` matches either.
- `except m.E` where `m` is a Python module (`py`, §10) takes a Python exception class: `except json.JSONDecodeError`. It matches a `PythonError` whose `value` is an instance of that class, as tested by Python's `isinstance`. The class is looked up when an exception reaches the clause.
- Anything else, such as a class not deriving from `Exception`, an enum or a type parameter, is `TP0200`.
- `except … as e` binds `e`, a `let` visible only in that clause's block. Its type is the clause's class, `PythonError` for a Python class, or the nearest class all of the listed classes derive from (§3.3), so `except (KeyError, IndexError) as e` gives a `LookupError`.
- A clause whose classes all derive from those of an earlier clause can never match, and is warning `TP0300`. Python classes aren't checked.
- **Broad clauses (`TP0301`).** A bare `except:`, or a clause listing `Exception` (or since M37 `BaseException`), also catches mistakes such as an `IndexError`. Such a clause is warning `TP0301`, at `except`, unless its block contains a `raise` (outside any nested function), or the clause binds the exception with `as e` and the block uses `e`. So logging the exception, or re-raising it, is deliberate handling. A clause that silently discards everything is warned about, as Python linters do (flake8's E722, pylint's `broad-exception-caught`).

**Return paths and initialization**
- A `try` terminates (§4.5) if its `finally` block terminates, or if every `except` block terminates and the `try` block or the `else` block terminates.
- In `__init__` (§4.3), a `raise` before the object is complete is valid, since the object is never seen. Fields assigned in a `try` block count as assigned only after the whole `try`, and only if the `else` block or the `try` block assigns them, every `except` block assigns them too, or the `finally` block assigns them. `super().__init__(…)` inside a `try` isn't directly in the body (`TP0226`).

**Uncaught exceptions** are panics (§7), and exit with 101. Since M41 they print a traceback, as Python does (§3.19).
- Before M41, one printed `panic: <message>` and the line where it was raised: a Taipan built-in exception its message alone (`panic: missing key "a"`), a `PythonError` `Python <type>: <message>`, and a user exception its class name and message (`panic: NotFound: no service named db`). `raise e` reported its own line, and a bare `raise` kept the line where the exception was first raised.
- **An uncaught `SystemExit` (M37)** isn't a panic. The program ends with its `code` as the exit code, printing nothing, or for `sys.exit(message)` the message and a newline to stderr, as Python does. Output the program printed is flushed first, and open files are flushed and closed (§6.1). As in Python, on Unix only the code's lowest 8 bits reach the parent process.

**What isn't catchable**
- Importing Python modules happens at startup (§10.1), before any `try` runs, so a failure to load Python or to import a module is always a panic. So is a failure to load Python at a program's first conversion to `py`, when it imports no Python module (§10.5).
- Running out of stack ends the process, as in .NET; it isn't an exception. Internal compiler errors aren't exceptions either.

### 3.11 Protocols (M17)
A protocol is a structural interface, as in Python's `typing.Protocol` (PEP 544): a set of method signatures that a type satisfies by having them, without declaring it. Until M40 a protocol was used only as the bound of a function's type parameter. Since M40 it may also be a type, have fields and take type parameters (§3.18).

```python
class Shape(Protocol):
    def area(self) -> float: ...
    def scaled(self, k: float) -> Self: ...

class Square:
    side: float

    def __init__(self, side: float):
        self.side = side

    def area(self) -> float:
        return self.side * self.side

    def scaled(self, k: float) -> Square:
        return Square(self.side * k)

def total_area[S: Shape](shapes: list[S]) -> float:
    var sum = 0.0
    for s in shapes:
        sum += s.area()
    return sum

def largest[T: Comparable](xs: list[T]) -> T?:
    var best: T? = None
    for x in xs:
        if best is some b:
            if x > b:
                best = x
        else:
            best = x
    return best

print(total_area([Square(1.0), Square(2.0)]))   # 5.0
print(largest([3, 9, 4]), largest(["b", "a"]))  # 9 b
```

**Declaring a protocol**
- A protocol is a top-level class whose only base is `Protocol`: `class Shape(Protocol):`. Python imports `Protocol` from `typing`; in Taipan it's a built-in name, like `Exception`.
- Its body holds methods and `pass`. Each method takes `self` first, annotates its other parameters, and has `...` as its body, written after the colon (`def area(self) -> float: ...`) or alone in an indented block. A method's parameters have no defaults.
- In a protocol method's signature, `Self` stands for the type that satisfies the protocol: `other: Self`, `-> Self`, `list[Self]`, `Self?`.
- Each `TP0225`:
  - a field (until M40; since then a field with a default, §3.18), a method body other than `...`, or a parameter default
  - type parameters on a protocol (until M40, §3.18)
  - another base besides `Protocol`
  - any special method other than `__lt__` (below), including `__init__`. A protocol's `__lt__` must be `def __lt__(self, other: Self) -> bool`.
- A decorator on a protocol (`@dataclass`) or on one of its methods (`@override`) is `TP0224`.
- A method name repeated in a protocol is `TP0214`.
- `...` anywhere but a protocol method's body is `TP0200`. A function or method whose body is just `...` reports only that, and not also `TP0208`. `Self` outside a protocol method's signature is `TP0201`, `undefined type 'Self'`, with help `'Self' can only be used in a protocol`. Since M39, it's also valid in a class's method signatures (§4.9).
- Protocol names are global, as class names are (§4.6), and modules export them (§4.7). A definition with the name `Protocol`, `Self` or `Comparable` is `TP0214`.

**Satisfying a protocol.** A type `X` satisfies a protocol `P` when, for each method `m` of `P`, with `Self` replaced by `X`:
- `X` has a method `m`, its own or inherited.
- It takes the same parameters, with the same names, in the same order. Each parameter's type is the protocol's, or, when the protocol's is a class, a base of it, since a method that accepts any `Animal` can be called with a `Dog`.
- It returns what the protocol's method returns, or, when both are classes or optional classes, something assignable to it (§3.3). If the protocol's method returns nothing, `m` must too.
- `m` may have defaults, which a call through the bound never uses, since it passes every argument.

This is checked at compile time, wherever a type argument meets a bound (below), and wherever a class declares the protocol.

**Declaring a protocol on a class (optional)**
- A class may list protocols among its bases, as PEP 544 allows: `class Version(Comparable):`, `class Square(Shape, Comparable):`, or with a base class, `class Dog(Animal, Comparable):`, in any order. The one-base rule of §4.3 counts only classes.
- A declaration is a check and documentation. It doesn't change what the class satisfies: a class that satisfies a protocol may be used with it whether or not it declares it.
- The class is checked against each protocol it declares at its definition, so a missing or mismatched method is reported there, rather than at the first call that needs it. It's `TP0200` at the protocol's name in the base list, with the messages used for calls: `'Version' does not satisfy 'Comparable': it has no method '__lt__'`.
- **Subclasses** inherit their base's declarations and are checked against them too, since an inherited method may not fit once `Self` is the subclass (a `-> Self` method returning the base class). The message names where the declaration came from: `'Cube' does not satisfy 'Shape' (declared by 'Square'): 'scaled' must be 'def scaled(self, k: float) -> Cube'`.
- Listing a protocol twice, including one a base class already declares, is `TP0225`. Dataclasses, exception classes and generic classes may declare protocols. In a generic class, `Self` is the class with its own type parameters: `Box[T]`.

Built-in types have no methods for this purpose except `__lt__`, so `str` doesn't satisfy a protocol with `def upper(self) -> str` in v0.1.

**`Comparable` and `__lt__`**
- `Comparable` is a built-in protocol, as if declared:
  ```python
  class Comparable(Protocol):
      def __lt__(self, other: Self) -> bool: ...
  ```
- `int`, `float` and `str`, and tuples of them (§3.8), satisfy it, as does a class with a suitable `__lt__`. `bool` and `None` don't, since Taipan doesn't order them (§5.3).
- **`__lt__` on a class** was the one special method besides `__init__` that a class could define before M28 (§4.3), and took the class or one of its bases. Since M28 it takes any type and is one of the comparison methods of §4.8. A class satisfies `Comparable` when its `__lt__` takes the class itself.
- `a < b` on a class value `a` calls `a.__lt__(b)`, and `b` must fit its parameter. `a > b` evaluates `a` and then `b`, and calls `b.__lt__(a)`, as Python does when `a` has no `__gt__`. Since M28 these follow §4.8's resolution and run-time protocol, so `a > b` calls `a.__gt__(b)` when `a`'s class has one.
- `<=` and `>=` aren't defined by `__lt__`: on a class without `__le__` and `__ge__` they're `TP0200` (§4.8), and on a bounded type parameter `TP0200`, with help `'__lt__' defines only '<' and '>'`. Python raises `TypeError` for them at run time; `functools.total_ordering` is deferred.
- `sorted` and `list.sort` accept a list, or a key, of any `Comparable` type (§6), and call only `__lt__`, as Python's sort does.
- Calling `__lt__` directly (`a.__lt__(b)`) is `TP0200`, with help `use '<'`, except as `super().__lt__(…)` in an override, as for every special method (§4.8).

**Bounds**
- A function's type parameter may be bounded by a protocol, as in PEP 695: `def largest[T: Comparable](xs: list[T]) -> T?:`. The bound may be qualified with a module: `[S: shapes.Shape]`.
- In the body, a value of type `T` supports what an opaque `T` does (§3.6), plus the protocol's methods, called with `Self` read as `T`: `s.area()`, `s.scaled(2.0)` (a `T`). If the protocol has `__lt__`, `T` also supports `<` and `>`, and lists of `T` may be sorted.
- **Calls:** after type-argument inference (§3.6), each bounded parameter's type argument must satisfy its bound. Otherwise it's `TP0200` at the callee, naming the first method that's missing or doesn't fit: `'Point' does not satisfy 'Comparable': it has no method '__lt__'`.
  - A type parameter of an enclosing generic function satisfies a protocol when its own bound declares compatible methods. An unbounded one satisfies none.
  - A constrained parameter (`[N: (int, float)]`) is replaced by each of its types in turn (§3.6), and each must satisfy the bound.
- **Each `TP0220`:**
  - a bound that isn't a protocol, such as a built-in type, or until M40 a class (§3.18)
  - a bound on a class's or enum's type parameter, as for constraints
- So that every bounded body is valid for every type argument that satisfies its bound, instantiation still never reports new errors (§3.6).

**Protocols as types.** Until M40, a protocol couldn't be the type of a binding, parameter, field, return value or element (`TP0229`, with help `use a bounded type parameter, e.g. 'def f[T: Shape](x: T)'`). Since M40 it can, when it uses `Self` only as a whole result (§3.18). It still can't be constructed, given to `isinstance`, matched in a `class` pattern or caught in an `except` (`TP0229`).

There are no run-time checks: satisfaction and bounds are entirely static, and a bounded function is monomorphized like any generic (§8). A protocol used as a type is a C# interface that each class satisfying it implements (M40, §8).

### 3.12 Type aliases (M18)
A type alias gives a type a name, as Python 3.12's `type` statement does (PEP 695):

```python
type Handler = Callable[[Request], Response]
type Pair[T] = tuple[T, T]
type Table = dict[str, list[Pair[int]]]

def serve(handler: Handler, request: Request) -> Response:
    return handler(request)

def swap[T](p: Pair[T]) -> Pair[T]:
    return p[1], p[0]
```

**Declaring an alias**
- `type Name = T` and `type Name[A, B] = T` are top-level definitions (§2), and since M40 statements in a block and members of a class body (§3.18). Like classes, they're hoisted, so an alias may be used before its definition, and may refer to aliases defined after it.
- The alias's name lives in the type namespace and is global (§4.6). A clash with a class, enum, protocol, function, constant or another alias, or a name such as `int` or `Self`, is `TP0214`.
- A generic alias's type parameters follow §3.6: a repeated one is `TP0214`, and one that shadows a type name is `TP0207`. Constraints and bounds on an alias's type parameters are `TP0220`, as on classes. A type parameter the right-hand side doesn't use is allowed, as in Python.
- The right-hand side is any type, checked where the alias is defined, so `type Bad = list[Nope]` is `TP0201` there.

**Using an alias**
- An alias stands for its type wherever a type is written: annotations, type arguments, `as` casts, constraint tuples and bounds (§3.11). It is that type, not a new one. A `Handler` is assignable to and from `Callable[[Request], Response]`, and an alias of a class narrows, matches and converts as the class does.
- A generic alias takes exactly as many type arguments as it has parameters, which replace its parameters in the right-hand side: `Pair[int]` is `tuple[int, int]`. A count mismatch, including a bare `Pair`, is `TP0209`, `'Pair' takes 1 type argument, found 0`.
- Optionals flatten through an alias, as through type arguments (§3.6): with `type MaybeInt = int?`, `MaybeInt?` is `int?`. Only a literal `T??` in source is `TP0213`.
- **Only in types.** An alias isn't a value, so it can't be called, constructed, given to `isinstance`, named in an `except` clause or a pattern, used for an enum's variants (`Status.Ok`), or used as a base class. Each is `TP0200`, `'Handler' is a type alias, not a value` (or `TP0225`, `'Animal' cannot be a base class`, for a base), with help `use '<N>'` when the alias names a class or enum `N`, generic ones included (`type IntBox = Box[int]` suggests `use 'Box'`). Python's aliases (`TypeAliasType`) aren't classes either, so each of these fails in Python at run time.
- **Recursion (`TP0230`).** An alias's expansion can't contain itself, directly (`type Tree = list[Tree]`) or through other aliases (`type A = list[B]`, `type B = A?`). Python allows recursive aliases, which a type checker expands lazily; Taipan expands aliases eagerly, so they'd never end. A recursive structure is written with a class instead (`class Tree: children: list[Tree]`), since class names refer to the class rather than expanding. The cycle is reported once, at the name of its first alias in source order: `type alias 'A' refers to itself through 'B'`.
- **Modules:** a module exports its aliases (§4.7): `from shapes import Pair`, `shapes.Pair[int]`.
- **Display:** until M40, diagnostics showed the expanded type, not the alias's name: `expected Callable[[Request], Response], found int`. Since M40 they show the alias's name (§3.18).

There's no run-time form: aliases are expanded during type checking, and nothing is emitted for them (§8).

### 3.13 Iterators and generators (M31)
```python
def naturals(start: int) -> Iterator[int]:
    var n = start
    while True:
        yield n
        n += 1

def evens(xs: Iterable[int]) -> Iterator[int]:
    for x in xs:
        if x % 2 == 0:
            yield x

for i, name in zip(naturals(1), ["a", "b", "c"]):
    print(i, name)
```

**Types**
- **`Iterator[T]`** is a single-pass stream of `T` values: a generator, the result of `map`, `filter`, `zip`, `enumerate` and `iter`, or an instance of a class with `__next__` (below). Each value is produced when it's asked for, and once taken it's gone, as in Python.
- **`Iterable[T]`** is anything a `for` loop can go over: `list[T]`, `dict[T, V]` (its keys), `str` (as `Iterable[str]`, one character each), an `Iterator[T]`, an instance of a class with `__iter__` (below), or since M43 a .NET value implementing `IEnumerable<T>` (§13.13). Each loop over a list, dict, str or class starts again from its beginning; a loop over an iterator continues from where it is.
- `Iterator` and `Iterable` are built-in generic names, like `list`, written as in Python's `typing`. Each takes one type argument (`TP0209`).
- **Assignability (§3.3):** each type above is assignable to `Iterable[T]` of its element type, and a class with `__next__` to `Iterator[T]`. Since iterators only produce values, `Iterator[Dog]` and `Iterable[Dog]` are also assignable to `Iterator[Animal]` and `Iterable[Animal]`, for classes, as Python's are covariant. Nothing else converts: a `list[T]` isn't an `Iterator[T]` (use `iter(xs)`).
- **Inference (§3.6):** an `Iterable[T]` parameter infers `T` from the element type of whatever is passed.
- **What they support:** `for`, unpacking (§3.8, checked at run time as for a list), `in` and `not in` (which take values until one is equal, as in Python), the built-ins below, and `==` and `!=`, which compare identity. They can't be indexed, sliced, measured with `len` or used as dict keys. Each is `TP0200`, and for indexing, slicing and `len` the help is `convert it with 'list(…)' first`.
- **Display:** `<generator object naturals>`, `<map object>`, `<filter object>`, `<zip object>`, `<enumerate object>`, `<list_iterator object>` from `iter` on a list, and `<iterator object>` for the rest, without Python's address. A class's instance displays as its class does (§4.8).
- Iterators aren't convertible to Python (§10.3). A library exports `Iterable[T]` as .NET's `IEnumerable<T>`, and `Iterator[T]` as the runtime's `IIterator<T>`, which is one (§14.2).

**Generators**
- **A generator function** is one whose body has a `yield`, not counting nested functions. It's declared to return `Iterator[T]` or `Iterable[T]`, or since M42 `Generator[Y, S, R]` (§3.20), as in Python's type hints. Any other return type, or none, is `TP0200`, `'<f>' yields, so it must return 'Iterator[T]'`.
- **`yield e`** is a statement. It gives `e`, which is checked against `T`, to whatever asked for the next value, and pauses the function there until it's asked again. Before M42, the value of a `yield` expression (`x = yield e`, Python's `send`) was deferred, and a bare `yield` was `TP0100`, `'yield' needs a value`; since M42 both exist (§3.20).
- **`yield from e`** gives each value of `e`, an `Iterable[U]` with `U` assignable to `T`, in turn.
- **Calling a generator function** runs none of its body. It gives a new iterator; the body runs up to the first `yield` when the first value is asked for, and so on. The arguments are evaluated at the call, and the defaults too.
- **`return`** without a value ends the generator, and so does reaching the end of its body. Before M42, `return e` was `TP0200`, `a generator can't return a value`; since M42 a `Generator[Y, S, R]` returns an `R` (§3.20). Since a generator needn't return, `TP0208` doesn't apply.
- **Where:** `yield` outside a function is `TP0215`, `'yield' outside a function`. A lambda's body is an expression, so it can't yield. Methods may be generators, including `__iter__` (below).
- **Inside `try`:** before M42, `yield` in the body of a `try` that has an `except` clause, or in an `except` or `finally` block, was `TP0215`, `'yield' can't be inside a 'try' with 'except', or in 'finally'`; since M42 it may be anywhere (§3.20). A `finally` around a `yield` runs when the generator finishes. A `for` loop that stops early (`break`, `return` or an exception) leaves the generator where it is, so a later loop or `next` continues it, as in Python; one that's never finished never runs its `finally`, where Python would run it when the generator is collected.
- **`StopIteration`** raised inside a generator, and not caught there, is `RuntimeError`, `generator raised StopIteration`, as Python 3.7 and later do.

**The iterator protocol for classes (§4.8)**
- **`__iter__(self) -> Iterator[T]`** makes a class `Iterable[T]`: a `for` loop over an instance calls `__iter__` once, and takes values from its result. It's often a generator method.
- **`__next__(self) -> T`** makes a class `Iterator[T]`: each value asked for is a call to `__next__`, and it ends when `__next__` raises `StopIteration`. As in Python, an iterator class usually also has `def __iter__(self) -> Iterator[T]: return self`; Taipan treats a class with `__next__` as its own iterator either way.
- Both follow the rules for special methods (§4.8): calling them directly is `TP0200`, with help `use 'iter(x)'` and `use 'next(x)'`.
- A class's `__iter__` result and `__next__` result types give `T`. A class with both must give the same `T` (`TP0225`).

---

### 3.14 Async functions (M33)
```python
from dotnet.System.Net.Http import HttpClient

async def fetch(url: str) -> str:
    let body = await HttpClient().GetStringAsync(url)
    return body.strip()

let first = fetch("http://localhost:8080/a")
let second = fetch("http://localhost:8080/b")
print(await first, await second)
```

- **`Awaitable[T]`** is a value that will give a `T`: a running async function's result, or a .NET `Task<T>` (§13.11). `Awaitable[None]` gives no value, as a .NET `Task` does.
- **`async def`** declares an async function or method. Its declared return type is what `await` gives, so `async def fetch(url: str) -> str` is called as `fetch(url)`, of type `Awaitable[str]`, and `await fetch(url)` is a `str`. Without a return type, it's `Awaitable[None]`. A function value of it has type `Callable[[str], Awaitable[str]]`.
- **Calling an async function starts it**, as in C#, unlike Python, where a coroutine waits to be awaited: its body runs until its first `await` of something not yet finished, and the call returns then. The rest runs when what it awaits finishes. So above, both requests are in flight before either is awaited.
- **`await e`** waits for `e`, an `Awaitable[T]`, and gives its `T`, or raises the exception it ended with. Awaiting anything else is `TP0200`, `cannot await <T>`. `await` binds like unary `-`, so `await f(x).y` awaits `f(x).y`; write `(await f(x)).y` for the field of the result.
- **Where `await` may appear:** in the body of an `async def`, and at the main file's top level, whose statements then run as an async entry point. Anywhere else, including a lambda, a generator, a nested `def` that isn't `async`, and a `const`, is `TP0215`, `'await' outside an async function`.
- **Display:** an awaitable displays as `<awaitable>`, and equality is identity.
- **An awaitable is awaited.** An expression statement whose value is an `Awaitable` is `TP0235`, `this awaitable is never awaited`, with help `write 'await …'`, since its exceptions would be lost. Store it to await it later (`let pending = fetch(url)`).
- **Since M42**, async generators, `async for`, `async with` and the `asyncio` module exist, and async code runs on one thread (§3.20). Before, a `yield` in an `async def` was `TP0215`. Async lambdas are deferred.

### 3.15 Sets (M35)
```python
let seen = {"a", "b"}
var tags: set[str] = set()
tags.add("x")
print(seen | tags, "a" in seen, len(seen & {"b", "c"}))   # {"a", "b", "x"} True 1
```

- **`set[T]`** holds distinct values of a type that can be a dict key (§3.1): `TP0200`, `set elements must be hashable, found <T>`, otherwise. A set itself can't be a set element or a dict key, since it changes.
- **Literals:** `{a, b, …}`, with the element type inferred as a list's is (§3.2). `{}` is an empty dict, as in Python, so an empty set is `set()`, which needs an expected type as `[]` does (`TP0206`). `set(xs)` takes any iterable's values.
- **Order:** a set keeps its values in the order they were first added, for iteration and display. Python's set order is arbitrary, and for strings differs between runs, so Taipan's is one of the orders Python might give; code that depends on the order should sort.
- **Display:** `{1, 2, 3}`, with elements as inside a list (§6); `set()` when empty. **Equality** ignores order.
- **Operators:** `a | b` (union), `a & b` (intersection), `a - b` (difference), `a ^ b` (symmetric difference), each a new set in the order of `a`'s values, then `b`'s; `a <= b` and `a < b` (subset, proper subset), `a >= b` and `a > b` (superset); `x in a`, `x not in a`. Both operands must be sets of the same element type.
- **Methods:** `add(x)`, `remove(x)` (`KeyError` if absent), `discard(x)`, `pop() -> T` (the first value; `KeyError` if empty), `clear()`, `copy() -> set[T]`, `update(xs)`, `union(xs)`, `intersection(xs)`, `difference(xs)`, `symmetric_difference(xs)` (each taking any `Iterable[T]`, giving a new set), `issubset(xs)`, `issuperset(xs)`, `isdisjoint(xs) -> bool`.
- `len`, `for`, `sorted`, `list`, `min`, `max`, `sum`, `any` and `all` take sets as iterables (§3.13). Changing a set while iterating over it raises `RuntimeError`, `set mutated during iteration`, as for a list or dict.
- Sets aren't passed to Python (§10.3) or .NET (§13.3) yet.

### 3.16 Comprehensions (M36)
```python
let squares = [x * x for x in range(5) if x % 2 == 0]        # [0, 4, 16]
let pairs = [(c, n) for c in "ab" for n in [1, 2]]           # [("a", 1), ("a", 2), ("b", 1), ("b", 2)]
let lengths = {word: len(word) for word in ["hi", "taipan"]}
let total = sum(x * x for x in squares)
```

- **Syntax** (§2): `[e for … ]` is a list comprehension, `{k: v for …}` a dict comprehension, `{e for …}` a set comprehension, and `(e for …)` a generator expression. After `e` come one or more clauses, the first a `for`: `for <targets> in <iterable>`, with targets as a `for` loop's (§5.2, so `for k, v in d.items()` unpacks), and `if <condition>`. Clauses nest left to right, as in Python. A generator expression that's a call's only argument drops its own parentheses: `sum(x * x for x in xs)`.
- **Types:** a list comprehension is a `list[T]`, `T` the type of `e`; a dict comprehension a `dict[K, V]`; a set comprehension a `set[T]` (whose `T` must be hashable, §3.15); a generator expression an `Iterator[T]` (§3.13). With an expected type (§4.6), `e` (or `k` and `v`) is checked against its element type, so `let xs: list[float] = [n for n in ints]` widens each `int`. Each `for` takes any iterable (§3.13), and each `if` a `bool` (`TP0205`).
- **Scope:** a comprehension has its own scope, as in Python 3. Its targets are `let` bindings visible in the clauses after their `for` and in `e`, and nowhere after the comprehension (`TP0201`). They may reuse an outer name, which is untouched after the comprehension, so `[x for x in xs]` is valid where `x` exists. A function value made inside captures each iteration's variable, as a `for` loop's does (§3.7).
- **Narrowing:** an `if` narrows for the clauses after it and for `e`, as an `if` statement does (§3.9): `[n + 1 for n in maybe if n is not None]` is a `list[int]` for a `list[int?]`.
- **Evaluation:** a list, dict or set comprehension runs at once, in order, as nested loops would. A generator expression is lazy (§3.13): its first `for`'s iterable is evaluated when it's created, and everything else as values are asked for, as in Python. It displays as `<generator object <genexpr>>`.
- **Not allowed inside:** `await` (`TP0215`, `'await' can't be inside a comprehension`). `yield` and assignment are statements, so they can't be inside one. Async comprehensions are deferred.
- Changing a list, dict or set while a comprehension iterates over it raises `RuntimeError`, as for a loop.

### 3.17 Context managers and `with` (M37)
```python
from types import TracebackType

class Timer:
    name: str

    def __init__(self, name: str):
        self.name = name

    def __enter__(self) -> Timer:
        print("start", self.name)
        return self

    def __exit__(self, exc_type: type[BaseException]?, exc: BaseException?, tb: TracebackType?) -> bool:
        print("stop", self.name, exc)
        return False

with open(source) as src, open(target, "w") as dst:
    for line in src:
        dst.write(line.upper())
```

- **Syntax** (§2): `with e as v:` or `with e:`, then a block. Items separated by commas nest left to right, so `with a as x, b as y:` is `with a as x:` holding `with b as y:`. `v` is a `let` scoped to the block, as a `for` loop's variable is (§5.2); Python keeps it after the block. A top-level `as` in an item names its variable, so a conversion there (§10.4) needs parentheses: `with (x as Lock) as held:`. Parenthesized item lists (`with (a as x, b as y):`) and targets other than a name are deferred.
- **Context managers:** `e` must be a `TextIO` (§6.1), whose `__enter__` gives the file itself and whose `__exit__` closes it, or an instance of a class with `__enter__` and `__exit__`, its own or inherited (§4.8). Anything else is `TP0200`, `<T> is not a context manager`, with help `define '__enter__' and '__exit__'`, and an optional is `TP0203`. `v` has the type `__enter__` returns.
- **Evaluation**, as in Python:
  - `e` is evaluated, then its `__enter__` is called, and its result bound to `v`. An exception from either leaves `__exit__` uncalled.
  - When the block ends normally, or through `return`, `break` or `continue`, `__exit__(None, None, None)` is called. A `return`'s value is evaluated first.
  - When the block raises any `BaseException`, `SystemExit` included, `__exit__` is called with the exception's class, the exception and a `TracebackType`. If it returns `True`, the exception is suppressed and the program continues after the `with`. Otherwise the exception propagates unchanged, still reporting the line where it was raised (§3.10).
  - An exception raised by `__exit__` replaces the one in flight, as from a `finally` block.
  - With several items, each later item is inside the earlier ones, so an exception from the second item's `__enter__` calls the first's `__exit__`.
- **Suppression and checking.** An `__exit__` declared `-> bool` may suppress, so for termination, definite assignment and narrowing (§4.5, §3.9) its `with` statement may end after any statement of its block, as a `try` with an `except` clause may: a block that always returns doesn't make the `with` terminate, and a variable first assigned inside isn't assigned after. One declared `-> None` never suppresses, so the `with` is checked as its block alone. A `TextIO`'s `__exit__` never suppresses. (Python's type checkers make the same distinction.)
- **`yield` inside `with`** was `TP0215`, `'yield' can't be inside a 'with'`, before M42; since M42 it's valid (§3.20), and so is `async with`. `await` may be inside one (§3.14).
- **`type[C]`** is the type of `__exit__`'s first argument: a class as a value, `C` or one deriving from it. `type[A]` is assignable to `type[B]` when `A` derives from `B`. Only `__exit__` receives one; nothing constructs or calls one, and `type[T]` for a `T` that isn't a class is `TP0200`. Its one attribute is `__name__: str`, and it displays as Python's does: `<class 'ValueError'>` for a built-in class, `<class '__main__.Timer'>` for one in the main file, and `<class 'zoo.Timer'>` for one in module `zoo`.
- **`TracebackType`** comes from the built-in module `types` (§4.7), as in Python. It displays as `<traceback object>`. Since M41, `__exit__` receives the exception's `__traceback__`, whose entries can be read (§3.19).

### 3.18 Types II (M40)
```python
class Drawable(Protocol):
    name: str
    def draw(self) -> str: ...

class Circle:
    name: str
    r: float

    def __init__(self, r: float):
        self.name = "circle"
        self.r = r

    def draw(self) -> str:
        return f"({self.r})"

class Label:
    name: str

    def __init__(self, name: str):
        self.name = name

    def draw(self) -> str:
        return self.name

let items: list[Drawable] = [Circle(1.0), Label("hi")]
for d in items:
    print(d.name, d.draw())

def loudest[A: Animal](xs: list[A]) -> A:
    return max(xs, key=lambda a: a.volume)

print(list(map(str, [1, 2])), sorted(["bb", "a"], key=len))
```

**Protocols as types.** A protocol may be the type of a binding, parameter, field, return value, element or type argument, as in Python's typing: `list[Drawable]`, `Drawable?`. Before M40 each was `TP0229`.
- **Which protocols:** one whose methods use `Self` only as their whole result (`-> Self` or `-> Self?`). On a protocol value, such a result has the protocol's type. A protocol that uses `Self` in any other place, such as a parameter (`other: Self`) or inside another type (`-> list[Self]`), can still be a bound, but using it as a type is `TP0229`, `'<P>' uses 'Self' in '<m>', so it can only be a type parameter bound`. So `Comparable` isn't a type, since `__lt__` takes `other: Self`: a protocol value couldn't check that the other value has the same class.
- **Assignability (§3.3):** a type is assignable to a protocol type when it satisfies the protocol, by §3.11's rules: a class, another protocol type, or a type parameter bounded by a class that satisfies it. `S?` is assignable to `P?` for such an `S`. Built-in types satisfy no protocol that can be a type, since the only method they have for this purpose is `__lt__` (§3.11). Where a value converts and its type doesn't satisfy the protocol, it's `TP0200`, with the bound's message: `'Point' does not satisfy 'Drawable': it has no method 'draw'`.
- **Inference doesn't produce protocols.** The elements of a list literal don't join to a protocol. `[Circle(1.0), Label("hi")]` without an expected type is `TP0200`, as for unrelated classes, and with `list[Drawable]` expected, each element is checked against `Drawable`. A type argument inferred from a `Circle` is `Circle`.
- **Using a protocol value:** its methods and fields are used as on a type parameter bounded by the protocol (§3.11), and dispatch on the runtime class. Besides those:
  - `==` and `!=` with a value whose type is assignable to the protocol type, or the reverse, compare as the runtime classes do (§4.8). `is` and `is not` compare identity (below). Display is the runtime class's.
  - `isinstance(p, C)`, `p as C` and class patterns take a class `C` that satisfies the protocol or has a subclass that does. Otherwise it's `TP0200`, `<P> can never be a <C>`. Class patterns never make a `match` on a protocol value exhaustive, so it needs a `_` or binding arm.
  - A protocol value can't be a dict key or set element (`TP0200`), since a class that satisfies the protocol needn't be hashable.
  - A protocol still can't be constructed, given to `isinstance` or `as` as the class to test, named in a class pattern or an `except` clause, or be a base other than as a declaration (§3.11). Each is `TP0229`, `'<P>' is a protocol`, with help `construct a class that satisfies it` or `test for a class that satisfies it`.
- **A protocol value is the object itself.** Converting to a protocol type doesn't copy or wrap the object, so identity, `isinstance` and display stay the object's.

**Protocol fields.** A protocol body may declare fields, such as `name: str`, without a default (`TP0225`, `a protocol field can't have a default`). A field's type can't use `Self` (`TP0201`).
- A type satisfies the field when it has a field of that name and exactly that type, its own or inherited.
- Neither a property nor a field of a narrower type satisfies it. The field may be assigned through the protocol, and a property could give a different value at each read, which narrowing (§3.9) relies on.
- Through a protocol value or a type parameter bounded by the protocol, the field may be read and assigned. It can be narrowed as a path, by §3.9's rules.
- Messages: `'<T>' does not satisfy '<P>': it has no field '<f>'`, or `'<f>' must be '<f>: <F>'`.

**Generic protocols.** A protocol may take type parameters, as in PEP 695: `class Source[T](Protocol):`, with methods such as `def take(self) -> T?: ...`.
- `Source[int]` is satisfied as `Source` would be with `T` replaced by `int`. Constraints and bounds on a protocol's own type parameters are `TP0220`, as for classes.
- **As a bound**, a generic protocol's type arguments can't use type parameters: `TP0220`, `a bound can't use type parameters`, as in Python. `[S: Source[int]]` is valid.
- **Assignability** between protocol types is satisfaction, so it doesn't compare type arguments. A `Source[Dog]` is assignable to `Source[Animal]`, since its `take` returns a `Dog?`. A parameter or field of type `T` makes them unrelated.
- **Inference:** an argument for a parameter of a generic protocol type, as in `def drain[T](s: Source[T]) -> list[T]`, infers `T` by matching each of the protocol's members with the argument type's member of the same name. Parameter, result and field types are matched exactly, as for function types (§3.6). Satisfaction is then checked with the inferred arguments.

**Class bounds.** A function's type parameter may be bounded by a class: `def loudest[A: Animal](xs: list[A]) -> A:`.
- **In the body**, a value of type `A` is assignable to `Animal`, and to whatever `Animal` is assignable to. It has the class's fields, methods and properties, and its operators and special methods, with `Self` results read as `A`. `isinstance`, `as` and class patterns take it as they take an `Animal`.
- **At a call**, the type argument must be the class or derive from it, or be a type parameter whose bound does. Otherwise it's `TP0200` at the callee: `'<T>' does not derive from '<C>'`.
- The class may be a generic class with type arguments, such as `[B: Box[int]]`. As for protocols, those arguments can't use type parameters (`TP0220`).
- Bounds on the type parameters of classes, protocols and enums are still `TP0220`.

**Inheritance with generic classes.** A generic class may have a base class, and a base class may be generic: `class IntBox(Box[int])`, `class Labeled[T](Box[T])`, `class Keyed[K](Entry)`.
- A generic base needs its type arguments (a bare `Box` is `TP0209`), and they may use the class's own type parameters.
- Inherited fields, methods and properties have the base's type parameters replaced by those arguments. Overrides are checked after the replacement (§4.3), so in `IntBox`, an override of `def get(self) -> T` returns `int`.
- `Labeled[int]` is assignable to `Box[int]`, but not to `Box[float]`, since type arguments stay invariant (§3.3). Joins go through bases as before: `IntBox` and `Labeled[int]` join to `Box[int]`.
- `isinstance`, `as` and class patterns still take only classes that aren't generic (§6). So `isinstance(b, IntBox)` is valid, and `isinstance(b, Labeled)` is `TP0200`.
- An exception class can't be generic (`TP0225`, `an exception class can't be generic`). §4.3's other rules stay: one base class, and a dataclass derives only from a dataclass.

**Generic functions, built-ins and constructors as values.** Besides §3.7's function values, these are values when used without being called:
- a generic top-level function, such as `first`
- a built-in function or built-in type: `len`, `str`, `abs`, `sorted`, `int`, `list`, …. `range`, `super`, `isinstance` and `open` aren't values, since their calls need particular argument forms: each is `TP0200`, `'<f>' cannot be used as a value`, with help `wrap it in a lambda`.
- a class's constructor (`Dog`, `zoo.Dog`), or an enum variant that has fields (`Circle`, `Shape.Circle`).

How their types are found:
- **With an expected function type:** a generic function, a built-in, a generic class's constructor and a generic variant need one. Without it, it's `TP0206`, `cannot infer the type of '<f>'`, with help `give it a type, e.g. 'let f: Callable[[…], …] = <f>'`.
  - The type parameters are inferred as a lambda's are (§3.7). In a call, such an argument is checked after the others, against its parameter's type with the type arguments inferred so far, and its result type may infer the rest. So `apply(first, xs)` works.
  - The value calls the function with the expected type's parameters, and is valid when that call would be, with its result assignable to the expected result: `sorted(words, key=len)`, `list(map(str, xs))`. Parameters the expected type doesn't pass take their defaults, as in a call.
- **Without one:** a constructor or variant that isn't generic takes its parameters' types, all of them, defaults included, so `Dog` is `Callable[[str, str], Dog]`.
- A constructor or function with keyword-only parameters, `*args` or `**kwargs` is `TP0200`, as for top-level functions (§4.2). An abstract class's constructor is `TP0234`.
- **Equality and display:** each is one value per function and type, as a top-level function is (§3.7), so `len == len` is `True` at the same type. Display is Python's:
  - `<function first>`
  - `<built-in function len>`
  - `<class 'int'>` for a built-in type, and `<class '__main__.Dog'>` or `<class 'zoo.Dog'>` for a class, as `type[C]` displays (§3.17)
  - `<function Shape.Circle>` for a variant
- Methods as values (bound methods) are still deferred.

**Type aliases in functions and classes.** `type Name = T` may be a statement in any block, or a member of a class body.
- **In a block**, the alias is visible in the whole block, from its start, nested functions and lambdas included, as top-level aliases are hoisted (§3.12). It may use the type parameters of the functions and class around it, and other aliases visible there.
- **In a class body**, it's visible in the whole class: field types, method signatures and method bodies. It may use the class's type parameters. It isn't visible outside the class, where `C.Name` is `TP0201`. Python allows that; it's deferred.
- **Names:** an alias with the name of a type visible around it is `TP0207`. Two aliases with one name in a block or class, or an alias with the name of a field or method of its class, are `TP0214`.
- Recursion is `TP0230`, as for top-level aliases. There's no run-time form.

**Aliases in diagnostics.** A type written with an alias displays with the alias's name, with its type arguments: `expected Handler, found int`, `Pair[int]`. This replaces M18's rule, which always showed the expanded type.
- The name stays with the type as long as it's used unchanged: assigned, passed or returned, and inside other types written with it (`list[Handler]`, or an element of a `Table` whose right-hand side uses `Pair[int]`).
- A type the checker builds displays expanded: a narrowed or joined type, or a type argument it infers.
- Hover shows the name the same way (§16.3). An alias is still exactly the type it names (§3.12).

**Narrowing II** (adds to §3.9):
- **Elements:** `xs[k]` can be narrowed, by `is not None`, `isinstance` and the other conditions, when:
  - `xs` is a list, dict or tuple, and a name or path that can be narrowed, and
  - `k` is an `int` or `str` literal (an `int` may have a `-`), or a name that can't change: a `let`, a parameter, a loop variable or a binding.
  - So `if xs[0] is not None: print(xs[0] + 1)` is valid.
  - Two elements are the same when their roots are the same, and their keys are the same literal or name.
- **What ends an element's narrowing:**
  - whatever ends a path's narrowing (§3.9), or the narrowing of `xs` itself
  - any change to a list or dict of `xs`'s type, through any name, since another name could refer to the same one:
    - assigning an element (`ys[j] = …`, `op=` too), `del` of an element, and slice assignment
    - the methods that change one (§6): a list's `append`, `extend`, `insert`, `pop`, `remove`, `clear`, `sort` and `reverse`, and a dict's `pop`, `setdefault`, `update` and `clear`
  - The help is `the change to a <T> on line <n> could have changed it; copy it to a 'let' first`.
  - A tuple can't change, so a tuple element's narrowing ends only when its root's does.
- **Paths rooted at a `var`:** `v.x`, where `v` is a `var`, can be narrowed as other paths are (§3.9). Assigning `v` ends it, as it ends the narrowing of `v`. A `var` that a nested function assigns can't be a path's root.
- **`==` with a literal:**
  - `x == lit` narrows `x: T?` to `T` when it's true, as `x is not None` does, when `x` can be narrowed and `lit` is an `int`, `float`, `str` or `bool` literal (a number may have a `-`). `x != lit` narrows when it's false. The literal may be on either side.
  - `x == None` narrows as `x is None` does, and `x != None` as `x is not None`. This replaces §3.4's rule that they don't narrow.
  - A type that isn't optional isn't narrowed further, since there are no literal types.
- **`isinstance` on `py`:** `isinstance(v, T)` takes `v: py` (M10) and gives a `bool`:
  - With `int`, `float`, `str` or `bool`, it's `True` when `v as T` would succeed (§10.4). So a Python `bool` isn't an `int`, and a Python `int` is a `float`, unlike Python's `isinstance`. It narrows `v` to `T`, when `v` can be narrowed. Each read of a narrowed `v` converts it as `as T` does.
  - With `list`, `dict` or `tuple`, without type arguments, or a `py` value such as a Python class (`collections.OrderedDict`), it's Python's `isinstance`, and doesn't narrow.
  - Anything else, a tuple of types included, is `TP0200`.
- **`is` between objects:** `a is b` and `a is not b` compare identity, as in Python, and give a `bool`.
  - Both operands must be of types whose values have an identity: a class, a protocol, a list, dict or set, a function, an iterator, `TextIO`, `py`, or an optional of one of these.
  - One operand's type must be assignable to the other's, or both must be `py`.
  - Otherwise it's `TP0200`, `'is' compares identity, which <T> doesn't have` (`int`, `float`, `str`, `bool`, tuples and enums), with help `use '=='`. With unrelated types it's `TP0200`, `cannot compare <S> and <T>`.
  - It doesn't narrow. `x is None` and `x is not None` stay as before.

### 3.19 Exceptions II (M41)
Exceptions chain as in Python: `raise … from …` sets an exception's cause, and an exception raised while another is being handled keeps it as its context. Each exception has Python's `args`, `__cause__`, `__context__`, `__suppress_context__` and `__traceback__`. An uncaught exception prints a traceback as CPython does, chained exceptions first. Exceptions may be dataclasses.

```python
@dataclass
class NotFound(Exception):
    key: str

def lookup(services: dict[str, int], name: str) -> int:
    try:
        return services[name]
    except KeyError as e:
        raise NotFound(name) from e

def main():
    print(lookup({}, "db"))

main()
```
prints to stderr, and exits with 101:
```
Traceback (most recent call last):
  File "lookup.tpn", line 7, in lookup
    return services[name]
KeyError: missing key "db"

The above exception was the direct cause of the following exception:

Traceback (most recent call last):
  File "lookup.tpn", line 14, in <module>
    main()
  File "lookup.tpn", line 12, in main
    print(lookup({}, "db"))
  File "lookup.tpn", line 9, in lookup
    raise NotFound(name) from e
NotFound: db
```

**`raise … from …`**
- `raise e from c` evaluates `e`, then `c`, then sets `e.__cause__` to `c` and `e.__suppress_context__` to `True`, and raises `e`, as in Python. `c` must be an exception, an optional of one, or `None`: `raise NotFound(name) from None` hides the context.
- A `c` of any other type is `TP0200`, `'from' needs an exception or None, found <T>`. A class, as in `from KeyError`, is `TP0200` with help `write 'KeyError(…)'`, as for `raise`.
- `from` without an exception before it (`raise from e`) is `TP0100`.

**Context**
- An exception raised while another is being handled gets that one as its `__context__`, as in Python. An exception is being handled during its `except` block, during a `finally` block that runs because it's propagating, and during an `__exit__` called with it (§3.17), including in any function called from there. With several, it's the innermost.
- `__context__` is set when the exception is raised: by `raise`, or by the runtime error (§7) itself. A bare `raise` doesn't change it, and neither does raising the exception being handled. As in Python, if the exception being handled has the new one somewhere in its own chain of contexts, that link is cut, so contexts never form a cycle.
- `raise e from c` sets the context too. `__suppress_context__` only stops it from being printed.

**Attributes.** `BaseException`, and so every exception class, has these, as in Python:
- **`args: tuple[str, ...]`** is `(message,)`, or `()` when the message is empty, set by `__init__`. It's a read-only property (`TP0202`). Python's `args` holds the constructor's arguments, which needn't be strings. Taipan's exceptions take a message, so this is what a Python exception constructed with that message holds. For the runtime's own errors (§7) it holds Taipan's message (`missing key "db"`, where Python's `KeyError` holds `'db'`). `SystemExit`'s is `(str(code),)`.
- **`__cause__: BaseException?`**, set by `raise … from …`. Assigning it also sets `__suppress_context__` to `True`, as in Python.
- **`__context__: BaseException?`** (above). It may be assigned.
- **`__suppress_context__: bool`**. It may be assigned.
- **`__traceback__: TracebackType?`**, `None` until the exception is raised. It may be assigned another traceback or `None`. Its entries grow as the exception propagates (below).
- Since these are `BaseException`'s members, a class can't declare a field, method or property with one of their names (`TP0214`), as for `message`.

**Tracebacks**
- **Entries.** Each exception's traceback is a list of entries, each naming a file, a line and a function, from the outermost frame to the innermost:
  - When an exception is raised, the frame of the `raise` (or of the runtime error) is added. As it propagates out of each frame and into the one that called it, the calling frame is added in front. A frame where an `except` clause catches it is the first entry of `e.__traceback__` there.
  - The line is that of the statement each frame is running: the `raise`, or the statement whose call is in progress. Python gives the line of the call or operation itself, which in a statement written across several lines may be a later one.
  - **A bare `raise`** keeps the traceback and doesn't add its own frame again, so the entry for the handling frame keeps the line where the exception reached it. **`raise e`** of an exception that already has a traceback adds the new entries in front of the old ones, as in Python. So `raise e` in `e`'s own handler shows that frame twice.
- **Frames** are named as Python names them: a function or method by its name, a property's getter and setter by the property's name, a file's top level `<module>`, a lambda `<lambda>`, a nested `def` by its name, and a generator expression `<genexpr>`. A list, set or dict comprehension runs in the frame it's in, as since Python 3.12. A generator's body is its own frame, added when an exception leaves it. Code the compiler generates, such as a dataclass's `__init__`, `__eq__` and `__repr__`, defaults (evaluated in the frame of the call) and the runtime's built-ins, has no frames.
- **`TracebackType`** (§3.17) is one entry and the rest: `tb_lineno: int` and `tb_next: TracebackType?`, read-only. `tb_frame` and `tb_lasti` are deferred.
- **Printing.** An uncaught exception (other than `SystemExit`) prints to stderr, after the program's output is flushed, as CPython 3.12 does with `-X no_debug_ranges` (no `^` markers under the failing part of a line):
  - First its cause, printed the same way, then `\nThe above exception was the direct cause of the following exception:\n\n`. Without a cause, its context, unless `__suppress_context__` is set, then `\nDuring handling of the above exception, another exception occurred:\n\n`. An exception already printed in this chain isn't printed again.
  - Then, if its traceback has entries, `Traceback (most recent call last):`, and for each entry `  File "<file>", line <n>, in <name>` followed by the line's text, stripped of leading and trailing whitespace, indented four spaces. `<file>` is the source path as given on the command line (§9). The text is read from the file when the traceback is printed, as Python's is, and left out if the file can't be read.
  - The same entry (file, line and name) more than three times in a row prints three times, then `  [Previous line repeated <k> more times]` (`time` when `<k>` is 1), as in Python.
  - Last, the exception: `<class>: <str(e)>`, or `<class>` alone when `str(e)` is empty. `<class>` is the class's name, qualified with its module (`zoo.NotFound`) unless it's a built-in class or in the main file. A `PythonError` (§10.5) prints its Python class as Python would, `json.decoder.JSONDecodeError: Expecting value: line 1 column 1 (char 0)`, and a `DotNetError` (§13.6) its .NET class's full name, `System.FormatException: …`. If the class's `__str__` raises, the text is `<exception str() failed>`, as in Python.
  - The program then exits with 101 (§7, §9). Python exits with 1.
- A Python exception's own frames, inside Python code, aren't printed.

**Dataclass exceptions**
- A dataclass (§4.3) may derive from a built-in exception class other than `SystemExit`, `PythonError` and `DotNetError`, or from another dataclass exception. Before M41 that was `TP0225`. It may be frozen and ordered (§4.9), and may define `__post_init__`. It can't be generic (`TP0225`, §3.18). A plain exception class still can't be a dataclass's base, nor a dataclass exception a plain class's (`TP0225`, §4.3).
- Its generated constructor takes its fields, as any dataclass's does. Its `message` and `args` come from the values the constructor is given, before `__post_init__` runs, as Python's do when every field is given positionally: with no fields the message is empty; with one, it's `str()` of the field's value; with several, the tuple of the values, displayed as a tuple is (§6): `("db", 3)`. `args` holds `str()` of each value. (Python's `args` keeps the values themselves, and holds only the arguments passed positionally, so `NotFound(key="db")` would have an empty message in Python.)
- It compares, hashes, displays inside containers and matches class patterns as any dataclass does, so one that isn't frozen isn't hashable (`TP0200` as a key). Used as a key through its base, as `dict[Exception, int]` allows, it hashes by identity, as its base does; Python raises `TypeError`. §4.8's rule that a subclass of a hashable class stays hashable (`TP0225`) doesn't apply to it (`NotFound(key="db")`), and `str(e)` and `print(e)` give its message, as Python's `BaseException.__str__` does.

**Variable-length tuples.** `tuple[T, ...]` holds any number of `T`s, as in Python:
- It's the type of `args`, and of `tuple(xs)` for an `Iterable[T]` `xs`. A tuple literal checked against `tuple[T, ...]` builds one, checking each element against `T`. A fixed tuple type isn't assignable to it, nor it to one.
- `len(t)`, `t[i]` with any `int` `i` (negative from the end; out of range raises `IndexError`, as for lists, §7), slices (`t[1:]`, giving a `tuple[T, ...]`), `for`, `in` and `not in`, and unpacking, with its length checked at run time as a list's is (§3.8). Assigning an element is `TP0202`, as for tuples.
- `==` and `!=` compare element by element, ordering compares lexicographically when `T` is ordered, and it's a valid dict key when `T` is, as for tuples (§3.8).
- It displays as tuples do (§6), with Python's forms for no element and one: `()`, `("a",)`, `(1, 2)`.
- It converts to and from Python's `tuple` when `T` is convertible (§10.3), as tuples do. A `match` on one is `TP0200`, `cannot match on tuple[T, ...]`; that's deferred.

### 3.20 Generators and async II (M42)
Generators are Python's coroutines too: `yield` is an expression that receives what `send` passes in, a generator can `return` a value, and `throw` and `close` raise inside it. Async generators, `async for` and `async with` work as in Python, and the built-in module `asyncio` runs awaitables together on one thread, as Python's event loop does.

```python
def averager() -> Generator[float, float, int]:
    var total = 0.0
    var count = 0
    var average = 0.0
    while count < 3:
        let value = yield average
        total += value
        count += 1
        average = total / count
    return count

let avg = averager()
next(avg)
print(avg.send(10.0), avg.send(20.0))   # 10.0 15.0

async def ticks(name: str, n: int) -> AsyncIterator[str]:
    for i in range(n):
        await asyncio.sleep(0)
        yield f"{name}{i}"

async def consume(name: str) -> int:
    var count = 0
    async for t in ticks(name, 2):
        print(t)
        count += 1
    return count

print(await asyncio.gather(consume("a"), consume("b")))   # a0 b0 a1 b1, then [2, 2]
```

**`Generator[Y, S, R]`**
- A generator that yields `Y`s, receives `S`s from `send`, and returns an `R`, as in Python's `typing`. It takes exactly three type arguments (`TP0209`), any of which may be `None`. A generator function may declare it as its return type, as well as `Iterator[Y]` and `Iterable[Y]` (§3.13), which are a generator that receives and returns `None`.
- `Generator[Y, S, R]` is assignable to `Iterator[Y]` and `Iterable[Y]`, so `for`, `next` and the iterator built-ins take one. It's covariant in `Y` and `R`, as `Iterator` is in `Y`, and invariant in `S`.
- It displays as `<generator object f>`, as before.

**`yield` as an expression**
- `yield e` is also an expression, whose value is what the generator is resumed with: `let x = yield e`, `x = yield e`, `x += yield e`, `return (yield e)`, and anywhere else in parentheses, `print((yield e))`. Unparenthesized, it may only be a whole statement or the right side of `let`, `var` or an assignment (`TP0100`), as in Python.
- **Its type** is `S`. `yield` without a value yields `None`, so `Y` must accept `None` (`TP0200` otherwise, `'yield' needs a value: '<f>' yields <Y>`).
- **How it's resumed:** `g.send(v)` resumes it with `v`, checked against `S`. `next(g)`, a `for` loop and the iterator built-ins resume it with `None`. When `S` doesn't accept `None`, resuming a generator that's waiting at a `yield` whose value is used, other than with `send`, raises `TypeError`, `a generator waiting for a <S> can't be resumed with next(); use send()`. Python would give the `yield` the value `None`. A `yield` whose value isn't used (a statement) may be resumed either way, and so may a generator that hasn't started.
- `yield from e` is an expression too: its value is `e`'s return value when `e` is a `Generator[Y2, S2, R]` (so its type is `R`), and the generator delegates `send`, `throw` and `close` to `e` while it runs, as in Python (PEP 380). `S` must be assignable to `S2` (`TP0200`). Over any other iterable its value has no type: using it is `TP0200`, `'yield from' over <T> gives no value`, and sending a value other than `None` to it raises `TypeError`, `can't send a value to a 'yield from' over an iterator`.

**Return values**
- `return e` in a generator declared `Generator[Y, S, R]` returns `e`, checked against `R`, and ends the generator. In one declared `Iterator` or `Iterable`, it's `TP0200`, `a generator declared '<T>' can't return a value`, with help `declare it 'Generator[Y, S, R]'`. `TP0208` doesn't apply, since reaching the end returns `None`, which `R` must then accept (`TP0200`, `'<f>' can end without returning a <R>`).
- The value becomes the `value` of the `StopIteration` that ends the generator, and the value of a `yield from` over it.
- **`StopIteration.value: object`** is `None` unless the exception ended a generator that returned a value. Since an exception class isn't generic, its type is **`object`**, which holds a value of any type. An `object` displays as its value does, may be tested with `is None` and `is not None` (which don't narrow it), and converts with `as`: `e.value as int` gives the `int`, raising `TypeError`, `cannot convert <runtime type> to <T>`, if it isn't one, and `as T?` gives `None` instead (§4.3). Any value is assignable to `object`. It has no other operations (`TP0200`). Since M43, .NET members give `object`s too, and `isinstance` and class patterns test them (§13.17). A `StopIteration` constructed with a message has `value` `None`; Python's would be the message.

**`send`, `throw` and `close`** are methods of `Generator[Y, S, R]`, as in Python:
- **`g.send(v: S) -> Y`** resumes the generator with `v` and gives the next value it yields, raising `StopIteration` if it returns instead. Sending a value other than `None` to a generator that hasn't started raises `TypeError`, `can't send non-None value to a just-started generator`, as in Python; start it with `next(g)`.
- **`g.throw(e: BaseException) -> Y`** raises `e` where the generator is paused, as if its `yield` had raised it, and gives the next value it yields. A generator that hasn't started is closed, and `e` raised from `throw`. Python's older forms, `throw(type, value, traceback)`, are `TP0209`.
- **`g.close() -> None`** raises **`GeneratorExit`** where the generator is paused, so its `finally` blocks run. If the generator then yields, `close` raises `RuntimeError`, `generator ignored GeneratorExit`. If it returns or raises `GeneratorExit` or `StopIteration`, `close` returns. Closing a generator that hasn't started or has finished does nothing.
- Resuming a generator from inside itself (calling its own `send` while it runs) raises `ValueError`, `generator already executing`.
- **`GeneratorExit`** is a built-in class deriving from `BaseException`, as in Python, so `except Exception` doesn't catch it.
- A finished generator gives no more values: `send`, `next` and `throw` raise `StopIteration` (for `throw`, the exception itself, as in Python).

**`yield` inside `try` and `with`.** Since M42, `yield` may appear anywhere in a generator's body, in a `try` with `except` clauses, in `except`, `else` and `finally` blocks, and inside `with`. Before, each was `TP0215`. An exception thrown in with `throw`, including `GeneratorExit`, reaches the enclosing `except` clauses and `finally` blocks as any exception does. A generator that's never finished or closed still never runs its `finally` (§3.13).

**Async generators**
- An `async def` whose body has a `yield` is an async generator. It's declared to return **`AsyncIterator[Y]`**, **`AsyncIterable[Y]`** or **`AsyncGenerator[Y, S]`** (two type arguments, as in Python's `typing`); anything else is `TP0200`, `'<f>' is an async generator, so it must return 'AsyncIterator[T]'`. `AsyncGenerator[Y, S]` is assignable to `AsyncIterator[Y]`, and that to `AsyncIterable[Y]`.
- Like a generator, and unlike an async function (§3.14), calling one runs none of its body: it gives a new async generator, whose body runs as values are asked for. Its body may `await`, and `yield` is an expression of type `S` as above.
- `return` with a value is `TP0200`, `an async generator can't return a value`, and `yield from` is `TP0215`, `'yield from' can't be in an async generator`, as in Python.
- Methods, as in Python: `__anext__()` and `asend(v: S)`, which give `Awaitable[Y]` and raise **`StopAsyncIteration`** (a built-in class deriving from `Exception`) when it's finished; `athrow(e)` (`Awaitable[Y]`) and `aclose()` (`Awaitable[None]`), which behave as `throw` and `close` do. The built-in **`anext(it)`** is `it.__anext__()`, and `anext(it, default)` gives `default` when it's finished, as `next` does (§6).
- Their errors are Python's: `TypeError`, `can't send non-None value to a just-started async generator`; `RuntimeError`, `async generator ignored GeneratorExit`; and `RuntimeError`, `anext(): asynchronous generator is already running`, when one is resumed while its body awaits.
- It displays as `<async_generator object f>`.

**`async for` and `async with`.** Both may appear only where `await` may (§3.14, `TP0215`, `'async for' outside an async function`).
- **`async for x in e:`** takes `e`, an `AsyncIterable[T]`: an async generator, or an instance of a class with `__aiter__(self) -> AsyncIterator[T]` and an `async def __anext__(self) -> T` that raises `StopAsyncIteration` at the end (§4.8). Each value is `await`ed from `__anext__`. Otherwise it follows `for` (§5.2): unpacking, `break` and `continue`. Anything else is `TP0200`, `cannot iterate over <T> with 'async for'`; a plain iterable gets help `use 'for'`.
- **`async with e as v:`** calls and awaits `e.__aenter__()`, and `e.__aexit__(…)` on the way out, as `with` (§3.17) calls `__enter__` and `__exit__`. A class with `async def __aenter__(self) -> T` and `async def __aexit__(self, exc_type: type[BaseException]?, exc: BaseException?, tb: TracebackType?) -> bool` (or `-> None`) is an async context manager. Both must be async, with the same signatures as `__enter__` and `__exit__` otherwise (`TP0225`), and a class must define both or neither. A plain context manager in `async with`, or an async one in `with`, is `TP0200`, with help `use 'with'` or `use 'async with'`.
- Async comprehensions (`async for` in a comprehension) are deferred.

**The event loop.** Since M42, a program's async code runs on one thread, as Python's `asyncio` event loop does, so it interleaves the same way at the same `await`s:
- Code after an `await` runs on the program's main thread when what it awaited finishes, in the order those things finished. Code that never awaits runs straight through, as before.
- The main file's top level, when it has an `await` (§3.14), runs the loop until it finishes. So does `asyncio.run` (below). .NET code that an `await` waits for may run on other threads, but the Taipan code after the `await` doesn't.
- Calling an async function still starts it (§3.14), unlike Python, where a coroutine starts when it's awaited or made a task. So `gather(f(), g())` starts `f` before `g` is called; in Python both start when `gather` is awaited. Awaiting an async generator's `__anext__` runs its body directly, as Python does, without a turn of the loop in between.
- In a library (§14), Taipan code after an `await` runs where the .NET caller's context runs it, as before.

**Cancellation.** A running async function can be cancelled, as a Python task can, by `asyncio.wait_for` and `asyncio.timeout` (below), or by cancelling a `gather` that's waiting for it:
- Cancelling raises **`asyncio.CancelledError`** (deriving from `BaseException`, as in Python) at the `await` the function is paused at, or at its next `await` if it's running. If it's awaiting another running async function or a `gather`, that is cancelled instead, and the `CancelledError` reaches it through that `await`, so the innermost `finally` blocks run first, as in Python.
- The function may catch `CancelledError` and go on; it isn't cancelled again unless something cancels it again.

**`asyncio`** is a built-in module (§4.7), `import asyncio`, with these names, and no others:
- **`asyncio.gather(*aws: Awaitable[T]) -> Awaitable[list[T]]`** waits for every awaitable and gives their results in order. The element type is what the arguments' types join to, as list literal elements do (§4.6), or `TP0200`. Its arguments may be awaitables, or `*xs` for a `list[Awaitable[T]]`. Without arguments it needs an expected type, as `[]` does (`TP0206`). If one raises, the `gather` raises that exception at once, while the others keep running, as Python's does (without `return_exceptions`). Cancelling the `gather` cancels them all.
- **`asyncio.sleep(delay: float) -> Awaitable[None]`** finishes after `delay` seconds (an `int` converts, §3.3). `sleep(0)` lets everything else that's ready run first, as in Python.
- **`asyncio.wait_for(aw: Awaitable[T], timeout: float?) -> Awaitable[T]`** gives `aw`'s result, unless `timeout` seconds pass first, when it cancels `aw` and waits for it to finish: if it ended with `CancelledError`, `wait_for` raises `TimeoutError`; if it caught that and finished anyway, `wait_for` gives its result, as Python 3.12's does. `None` waits without a limit.
- **`asyncio.timeout(delay: float?)`** is an async context manager (an `asyncio.Timeout`): `async with asyncio.timeout(1.5):` cancels the code inside after `delay` seconds, and the `async with` then raises `TimeoutError` from the `CancelledError`, as Python 3.11's does. `None` never expires.
- **`asyncio.run(aw: Awaitable[T]) -> T`** runs the event loop until `aw` finishes and gives its result, so that a program written as Python's `asyncio.run(main())` works. It's an ordinary function, and calling it while the loop is running (in an async function, or at a top level with `await`) raises `RuntimeError`, `asyncio.run() cannot be called from a running event loop`.
- **`asyncio.CancelledError`** (above), and **`asyncio.TimeoutError`**, which is the built-in **`TimeoutError`**, a new built-in class deriving from `OSError`, as in Python 3.11 and later.
- Deferred: `create_task` and tasks' methods (`cancel`, `done`, `result`), `return_exceptions`, `wait`, `as_completed`, queues, locks and the other `asyncio` names.

## 4. Declarations

### 4.1 Bindings
- `let` is immutable. Reassigning it is `TP0202`.
- `var` is mutable.
- `const` is top-level only, and its initializer must be a literal or an arithmetic or string expression of literals. Constants are visible everywhere.
- **Scoping** is per block, as in C#. A name is visible from its declaration to the end of its block.
- **Shadowing** a name from an enclosing block is `TP0207`. Redeclaring a name in the same block is `TP0214`.
- **Top level:**
  - `def`, `class`, `enum`, and `const` are hoisted and visible everywhere.
  - Top-level `let` and `var` are script locals. They're visible only to later top-level statements. Referencing one from inside a function is `TP0211`.

### 4.2 Functions
- `def` declares a function. Recursion and mutual recursion are allowed, since functions are hoisted. A `def` inside a block is a nested function, which isn't hoisted (§3.7).
- Every path in a function with a declared return type must `return` a value (`TP0208`).
- Overloading isn't supported. A duplicate name is `TP0214`.
- **Default values (M14):** `def connect(host: str, port: int = 8080) -> str:`. A parameter with a default may be left out of a call.
  - Parameters with defaults come after those without (`TP0100`, §2).
  - The default is checked against the parameter's type as its expected type (§4.6), so `xs: list[int] = []` is valid.
  - It may use any name visible where the function is defined: constants, functions, built-ins, and, for a nested `def` (§3.7), the bindings it can capture. It can't use the function's parameters (`TP0201`), as in Python. A top-level function's default can't use top-level variables (`TP0211`).
  - **A default is evaluated on each call that leaves it out**, after the call's own arguments. This differs from Python, which evaluates a default once, when the function is defined, so a mutable default such as `[]` would be shared between calls. In Taipan, every call that omits `xs` gets a new list.
  - In a generic function, a default is checked once, with every type parameter opaque, even a constrained one (§3.6). It doesn't take part in inference.
  - A panic while evaluating a default reports the default's line (§7).
  - Methods' parameters may have defaults too (§4.6). Lambdas' parameters can't.
- **Arguments (M14)** are matched as in Python:
  - Positional arguments fill parameters from the left. Then each keyword argument fills the parameter of that name. Any parameter still unfilled takes its default.
  - Too many positional arguments is `TP0209` (`'<f>' takes <n> arguments, found <m>`, or `<a> to <b> arguments` when some have defaults). So are too few, in a call with only positional arguments.
  - Otherwise, a parameter without a default that's left unfilled is `TP0209` (`missing argument '<x>'`), unless the call also has an unknown keyword. One filled twice, by position and by keyword or by two keywords, is `TP0209` (`argument '<x>' is given more than once`).
  - A keyword that isn't a parameter name is `TP0218` (`'<f>' has no parameter '<k>'`).
  - Arguments are evaluated in source order, then the missing defaults in parameter order.
  - The same rules apply to functions, nested `def`s and methods. A function used as a value (§3.7) loses its parameter names and defaults: its type lists every parameter, and calls through it pass them all, positionally.
- **Parameter kinds (M30)**, as in Python:

  ```python
  def query(table: str, /, limit: int = 10, *columns: str, order: str, **filters: int) -> str:
  ```

  - **Positional-only:** parameters before a `/` can only be passed by position. In a method, `/` comes after `self` or `cls` at the earliest.
  - **Keyword-only:** parameters after a `*`, or after `*args`, can only be passed by keyword. They may have defaults or not, in any order, as in Python.
  - **`*args: T`** collects the positional arguments left over after the positional parameters are filled, in order, into a new `list[T]`, empty if there are none. It's annotated with the element type, as in Python's type hints.
  - **`**kwargs: T`** collects the keyword arguments that name no parameter it could fill, in source order, into a new `dict[str, T]`, empty if there are none. As in Python, a keyword naming a positional-only parameter goes into it too.
  - **Order:** positional-only parameters and `/`, then the others, then `*` or `*args` and the keyword-only parameters, then `**kwargs`, last. Anything else is `TP0100`: `/` first or twice, `/` after `*`, two stars, a bare `*` with no keyword-only parameter after it (`named arguments must follow bare '*'`), anything after `**kwargs`, or a default on `*args` or `**kwargs`. Among the parameters before `*`, one without a default still can't follow one with a default (M14).
  - `args` and `kwargs` are only Python's conventional names: any name will do. Inside the function each is an immutable parameter, holding a list or dict the call made, which the body may change.
- **Binding arguments (M30)** follows Python's rules, in this order:
  1. Positional arguments fill the positional-only parameters and then the ordinary ones, left to right. The rest go to `*args`, or without one are `TP0209`, as in M14: `'<f>' takes <n> arguments, found <m>`, or `<n> positional arguments` when it has keyword-only parameters.
  2. Each keyword argument fills the ordinary or keyword-only parameter of its name. One naming a positional-only parameter is `TP0218`, `'<p>' is positional-only, so it can't be passed by keyword`, unless the function has `**kwargs`, which takes it. Any other name goes to `**kwargs`, or without one is `TP0218` as in M14.
  3. Parameters still unfilled take their defaults. One without a default is `TP0209`, `missing argument '<x>'`, or for a keyword-only one `missing keyword-only argument '<x>'`. One filled twice is `TP0209` as in M14.
  - Every one of these is reported at compile time. Arguments are evaluated in source order, including those collected into `*args` and `**kwargs`, and then the missing defaults in parameter order.
- **Unpacking at a call (M30):** `f(*xs)` and `f(**opts)`, in any order with other arguments, as in Python.
  - **`*e`** with a tuple supplies its elements as positional arguments, one by one, so `point(*pair)` is valid wherever two positional arguments are. With a list, whose length isn't known when compiling, every element must go to `*args`: each positional parameter must already be filled by the positional arguments before it, and the elements must fit `*args`'s type. Otherwise it's `TP0209`, `'*<e>' can only fill '*<args>'`, or without `*args` `'<f>' has no '*args' for '*<e>'`. Positional arguments after a list `*e` go to `*args` too. Anything else after `*` is `TP0200`, `cannot unpack <T> into arguments`.
  - **`**e`** needs a `dict[str, V]`, a callee with `**kwargs`, and `V` fitting its type (`TP0200`, or `TP0209`, `'<f>' has no '**kwargs' for '**<e>'`). Every entry goes to `**kwargs`, since the keys aren't known when compiling, so every parameter a keyword could fill must already be filled or have a default, which it then takes (`TP0209`, `'**<e>' can't fill '<x>'`). At run time a key naming one of those parameters raises `TypeError`, `<f>() got multiple values for argument '<k>'`, and one given twice across arguments `<f>() got multiple values for keyword argument '<k>'`, as Python does. (Python would let a key fill a named parameter; that's deferred.)
- **Function values (M30):** a function with `*args` or `**kwargs` can't be used as a value (`TP0200`, `'<f>' has '*args' or '**kwargs', so it cannot be used as a value`, with help `wrap it in a lambda`), since a function type passes a fixed list of positional arguments (§3.7). Positional-only and keyword-only parameters are positional in its function type, as every parameter is.
- **Overrides and protocols (M30)** compare parameter kinds too: an override must have the base method's `/`, `*`, `*args` and `**kwargs` (§4.3, `TP0228`), and a method satisfies a protocol's only with the same kinds (§3.11).

### 4.3 Classes (M15)
Classes are Python's, with the checks a static type system needs. Before M15, records were declared with `struct`. A `@dataclass class` replaces it, with the same behavior.

```python
class Animal:
    name: str
    legs: int = 4

    def __init__(self, name: str):
        self.name = name

    def speak(self) -> str:
        return "..."

class Dog(Animal):
    breed: str

    def __init__(self, name: str, breed: str):
        self.breed = breed
        super().__init__(name)

    @override
    def speak(self) -> str:
        return f"{self.name} the {self.breed} says woof"

@dataclass
class Point:
    x: float
    y: float = 0.0
```

- **Two kinds of class:**
  - A **plain class** has an `__init__` that sets its fields, compares by identity, and displays as `<Dog object>`. Its instances may be dict keys, compared by identity.
  - A **dataclass**, marked `@dataclass`, gets a generated constructor that takes its fields in order. It compares field by field and displays as `Point(x=1.0, y=0.0)`, as in Python. Like Python's (without `frozen=True`), its instances can't be dict keys (`TP0200`).
  - Since M28, either kind may define `__str__`, `__repr__`, `__eq__` and `__hash__` to change this (§4.8).
- **Fields** are declared in the class body, with a type and an optional default: `legs: int = 4`. Assigning a field that isn't declared, through `self` or otherwise, is `TP0210`. Python would create it.
  - A field's default is evaluated each time an instance is made: in a plain class, just before the class's own `__init__` body runs, and in a dataclass, as the constructor's parameter default. This differs from Python, where a class-level `items: list[int] = []` is one list shared by every instance.
  - Defaults follow the rules for parameter defaults (§4.2): checked against the field's type, and unable to use `self` or the `__init__` parameters (`TP0201`).
  - Fields are mutable through any binding: `s.healthy = 2` is valid even when `s` is a `let`. `let` fixes the binding, not the object.
- **Methods** take an explicit `self` as the first parameter, typed implicitly as the enclosing class. They're called as `s.method(args)`, with arguments matched as for functions (§4.2), and are dispatched on the object's runtime class, so a `Dog` in a `list[Animal]` runs `Dog.speak`. Inside a method, other methods are reached as `self.m()`, and a bare `m()` refers to a global function.
- **Special methods:** a plain class may define `__init__`, and any class may define `__lt__` (M17, §3.11). Since M28, any class may define the special methods of §4.8 (`__str__`, `__eq__`, `__hash__`, `__add__`, …). Any other method named `__x__` is `TP0225`: Taipan reports it rather than silently ignore it.
- **Members** are looked up on the static type. An unknown field or method is `TP0210`. A `Dog`-only method on an `Animal` needs narrowing first (§3.9).

**Construction**
- A class's name, called, constructs an instance: `Dog("rex", "lab")`, `Point(1.0, y=2.0)`. Arguments are matched as for functions (§4.2).
- **Plain class:** the arguments are matched to `__init__`'s parameters, after `self`.
  - A plain class without `__init__` uses its base class's `__init__`, as in Python. With no base class, it takes no arguments.
  - `__init__` has no return type (`TP0200`), other than `-> None` (M26), and may use a bare `return`.
  - `__init__` can't be called directly (`x.__init__()` is `TP0200`), except as `super().__init__(…)` (below).
- **Dataclass:** the parameters are the fields, in declaration order, with inherited fields first, and with the fields' defaults as their defaults.
  - A field without a default after one with a default is `TP0225`, as Python's `dataclass` reports it. That includes a field after an inherited field with a default.
  - Messages say `field` rather than `argument`: `missing field '<f>' in '<C>'`, `field '<f>' is given more than once` (`TP0209`), and an unknown keyword is `TP0210`.
  - A dataclass can't define `__init__` (`TP0225`). Since M39 it may define `__post_init__` (§4.9).

**Strict initialization (`TP0226`).** An object can never be seen with a field unset.
- At the end of `__init__`, on every path, each of the class's own fields must have been assigned, or have a default. Otherwise it's `TP0226` at `__init__`'s name: `'__init__' does not set 'breed'`.
- A plain class without `__init__` needs a default for each of its own fields (`TP0226` at the field).
- Until the object is complete, `self` may only be used to assign a field, or to read one of its own fields already assigned on every path to that point:
  - Reading a field that may be unset is `TP0226`: `'breed' is read before it is set`.
  - Any other use of `self` is `TP0226`: `'self' is used before 'breed' is set`. That includes calling a method, passing or storing `self`, displaying it, and capturing it in a lambda or nested function.
  - Inherited fields belong to the base class's `__init__`. Reading or assigning one before `super().__init__(…)` is `TP0226`.
- **The object is complete** once every own field is assigned on every path and, in a class with a base, `super().__init__(…)` has been called. From then on, `self` may be used freely, including calling methods.
- **`super().__init__(…)` in a subclass:**
  - It's required, exactly once, as a statement directly in `__init__`'s body, not inside an `if`, loop, `match` or nested function (`TP0226`).
  - **The subclass sets its own fields first**, then calls `super().__init__(…)`, as in Swift. Calling it before an own field is set is `TP0226`, `'super().__init__' is called before 'breed' is set`. The base's `__init__` may call methods once its own fields are set. Since methods dispatch on the runtime class, `speak` would then run `Dog.speak`, which can read `breed` only because the subclass set it first. Python allows either order, and the other order fails, or reads stale values, only at run time.
  - Its arguments can't use `self` (`TP0226`).
  - Outside `__init__`, `super().__init__` is `TP0226`.
- Assignments analysed are the plain `self.f = e` and `self.f op= e` statements. The analysis follows `if`/`elif`/`else`, `match` arms and loops. A field assigned only inside a loop body, or in only some branches, isn't assigned after it. A `return` in `__init__` before the object is complete is `TP0226`. `try` and `raise` follow §3.10 (M16).

**Inheritance**
- `class Dog(Animal):` derives `Dog` from `Animal`. A class has at most one base class: `class Dog(Animal, Pet)` is `TP0225`, as is a base that's an enum, a built-in type or a type parameter. Protocols it declares (M17, §3.11) may be listed alongside, and don't count as bases. The base may come from another module: `class Dog(zoo.Animal):`.
- A subclass inherits every field and method of its base. A `Dog` is assignable to `Animal` (§3.3), and to every class above it.
- **Restrictions in M15**, each `TP0225`:
  - A dataclass's base must be a dataclass, and a plain class's base a plain class.
  - Generic classes can't derive or be derived from (until M40, §3.18).
  - A class can't derive from itself, directly or through other classes.
- A subclass can't declare a field or method with the name of an inherited field (`TP0214`), or a field with the name of an inherited method (`TP0214`).
- **`super().m(…)`** calls the base class's `m`, found from the base upwards, without dispatching on the runtime class. It's valid in any method of a class with a base. `super` used any other way, or in a class without a base, is `TP0200`.

**Overriding**
- A method with the name of an inherited method overrides it, and must be marked `@override` (`TP0227`): `'speak' overrides 'Animal.speak'; mark it '@override'`. `@override` on a method that overrides nothing is also `TP0227`. This catches a misspelled override, and a base method added later that would silently take over a subclass's method. Python 3.12's `typing.override` is optional; in Taipan it's required.
- `__init__` doesn't override: each class has its own, with any parameters. `@override` on `__init__` is `TP0227`.
- **An override must be compatible (`TP0228`)**, so that every call valid on the base is valid on the subclass:
  - the same number of parameters, with the same names, in the same order, and the same types
  - a default wherever the base method has one. The override may add defaults. A call evaluates the defaults of the method that runs, as in Python.
  - since M30, the same kinds: the same `/` and `*`, and `*args` and `**kwargs` of the same types, if the base has them (`TP0228`, `'<m>' must take the same parameters as '<B>.<m>'`).
  - a return type that's the same as the base's, or, when both are classes or optional classes, one assignable to the base's (`Dog` for `Animal`). If the base returns nothing, the override must too.

**Narrowing and conversion**
- `isinstance(x, C)` tests the runtime class (§6), and narrows `x` (§3.9).
- `x as C` converts `x` to the class `C`, which must derive from `x`'s class or be a base of it (`TP0200` otherwise). If `x` doesn't hold a `C`, it panics with `cannot convert <runtime class> to <C>` (§7). `x as C?` gives `None` instead of panicking.
- `match` takes class patterns (§5.1).

**Equality, display and keys**
- **Plain class:** `==` and `!=` compare identity, as in Python without `__eq__`. Each construction makes a new object, equal only to itself. The display form is `<Dog object>`, by runtime class.
- **Dataclass:** `==` compares the runtime classes, then every field, inherited ones included, structurally. Values of different classes are never equal, as in Python's `dataclass`. The display form is the runtime class's name and every field: `Dog(name="rex", breed="lab")`.
- `==` between two class types is valid when one is assignable to the other. Unrelated classes are `TP0200`.
- `dict[C, V]` is valid for a plain class `C`, with keys compared by identity (§3.1). Since M28, for any hashable class (§4.8).
- Class values aren't convertible to Python (§10.3).

**Generics:** classes may take type parameters, plain or dataclass, as structs could (§3.6): `class Box[T]:`, with `@dataclass` on the line before it for a dataclass.

### 4.4 Enums (sum types)
- Variants have no payload or named payload fields.
- **Construction:**
  - `Ok`
  - `Degraded(1, 4)` or `Degraded(healthy=1, total=4)`
  - The qualified form `Health.Ok` is always valid.
  - The unqualified form is valid when the variant name is unique among enums in scope. Otherwise it's `TP0216`.
- Payload fields are read-only and reachable only through `match` bindings.
- Enums have structural equality.
- **Names:**
  - An enum's name lives in the type namespace and the global value scope, like a class's.
  - Each variant name also occupies the global value scope. A variant can't share its name with a function, constant, class, enum or built-in (`TP0214`).
  - Two enums may share a variant name, but then only the qualified form is valid. The unqualified form is `TP0216`, even when an expected type would disambiguate.
  - A local can't take a variant's name (`TP0207`).
- **Construction details:**
  - A variant with a payload follows the dataclass constructor rules of §4.3 and §4.6 (`TP0209`, `TP0210`). Without arguments, it's `TP0209`.
  - A variant without a payload is a value. Calling it, as in `Ok()`, is `TP0200`.
  - An enum name used as a value is `TP0200`. So is member access on an enum value: payloads are reached only through `match`.

### 4.5 Names and checking rules
- **Namespaces:** types (`int`, `float`, `bool`, `str`, user classes and enums) and values (functions, constants, variables, parameters, built-in functions) are separate. An unknown type name is `TP0201`.
- **Global scope:** functions, constants and built-in functions share one global scope. Defining a name twice there, including a `def` or `const` with a built-in function's name, is `TP0214`.
- **Shadowing (`TP0207`)** covers every name visible at the declaration, including globals and built-in classes and types. Since M35, a local or parameter may reuse a built-in function's name, as in Python (`var sum = 0`, `let max = 3`), except one that's also a type's (`int`, `float`, `str`, `list`, `set`); calling it afterwards is checked as for any value, so `sum(xs)` with `sum: int` is `TP0200`. A function body can't see top-level `let`/`var` bindings (§4.1), so declaring the same name inside a function is allowed.
- **Parameters** are immutable, like `let`. They're declared in the function's outermost scope, so `let x` in a body with a parameter `x` is `TP0207`. Two parameters with the same name are `TP0214`.
- **Assignment** to anything other than a `var` (a `let`, a parameter, a loop variable, a constant or a function) is `TP0202`. Compound assignment `x op= e` is checked as `x = x op e`, so `var n = 1` then `n /= 2` is `TP0200`.
- **Use before declaration:** a local or top-level binding referenced before its `let`/`var` is `TP0201`.
- **Functions as values** follow §3.7 (M11). Calling something that isn't a function is `TP0200`.
- **Keyword arguments** in a call to a function value (§3.7) or to a built-in without keyword parameters (§6) are `TP0218`. So is a keyword that a function, method or built-in doesn't take.
- **Return values:**
  - `return` with a value in a function without a return type, or `return` without one in a function with a return type, is `TP0200`.
  - Using a call to a function without a return type as a value is `TP0200`.
- **Return-path analysis (`TP0208`).** A statement *terminates* if it is a `return` or a `raise` (M16), or an `if` with an `else` whose every branch ends in a terminating statement. (In M7, a `match` whose arms all terminate also counts, and in M16 a `try`, as §3.10 says.) A block terminates if any statement in it does. A function with a return type whose body doesn't terminate is `TP0208`. Loops never terminate for this purpose, including `while True:`.
- **Unreachable statements (`TP0300`, M16).** A statement that follows, in the same block, a statement that terminates, or a `break` or `continue`, can never run. The first such statement in each block is warning `TP0300`, `unreachable code`. It's still checked. Since loops never terminate, code after `while True:` isn't reported.
- **Constants:** a `const` initializer may contain only literals, unary `-`, and the operators `+ - * / // %`. Anything else, including a reference to another constant, is `TP0217`. Evaluation happens at run time, so `const X = 1 // 0` panics at startup.
- **Integer literals:** a literal outside the 64-bit signed range is `TP0200`. The exception is `-9223372036854775808`, where `-` is applied directly to the literal.
- **Expression statements:** any expression may be used as a statement, and its value is discarded.
- **Error types:** once an expression has an error, its type is treated as compatible with everything. This means one mistake is reported once, not again at every use.

### 4.6 Classes, lists and dicts: checking rules
- **Class names** live in the type namespace and also in the global value scope, as the class's constructor. So a class can't share a name with a function, constant or built-in (`TP0214`), and a local can't take its name (`TP0207`). A class named `int`, `float`, `bool`, `str`, `list`, `dict`, `py`, `Callable` or `tuple` is also `TP0214`, as is one named `Protocol`, `Self` or `Comparable` (M17), or `ABC` or `ClassVar` (M28).
- **Type alias names (M18)** live in the type namespace only, but share the global scope's names: an alias can't share a name with any other global (`TP0214`), and a local can't take its name (`TP0207`).
- **Members:** field and method names within one class, inherited ones included, must be distinct (`TP0214`). Field types may name any class, including the class itself, for example through `list[Node]` or `Node?`.
- **Dataclass construction:**
  - Arguments are matched to fields as to parameters (§4.2): positional ones in field order, then keyword ones by name. A field with a default may be left out.
  - Too many positional arguments, or a missing or repeated field, is `TP0209`. An unknown name is `TP0210`.
  - Arguments are evaluated in source order, then the missing defaults in field order.
- **Methods** are hoisted with their class. Inside a method, `self` is an immutable parameter (`TP0202` to reassign), and other methods are reached as `self.m()`. A bare `m()` refers to a global function. `self` outside a method is `TP0201`.
- **Member access:**
  - An unknown field or method is `TP0210`.
  - A method used without calling it is `TP0200`. So is calling a field, unless the field has a function type (§3.7).
  - Assigning to a method is `TP0202`.
- **Expected types.** An expression is checked against an expected type when its context supplies one: an annotation, a parameter (including constructor fields and built-in method parameters), a return type, an assignment target (variable, field or element), or an enclosing list or dict literal that has an expected type. `[]` and `{}` need one (`TP0206`). With an expected type, each element is checked against the expected element type, so `let xs: list[float] = [1, 2]` is valid.
- **Literals without an expected type:** all elements (or all keys, or all values) must have the same type, except that `int` and `float` mix to `float`, and classes mix to their nearest common base (§3.2). A mismatch is `TP0200` at the first element that differs.
- **Assignment targets:**
  - `xs[i] = v` and `d[k] = v` are allowed (a `d[k]` assignment inserts or updates).
  - `s[i] = v` on a `str` is `TP0202`, and so is `t[i] = v` on a tuple (M12).
  - A slice is a target for a list since M26 (§5.5). Before M26 it was `TP0100`.
  - Compound forms (`xs[f()] += 1`, `make().count += 1`) evaluate the target's sub-expressions once.
- **Indexing** applies to `list[T]` (`int` index, gives `T`), `dict[K, V]` (`K` key, gives `V`), `str` (`int` index, gives `str`), and tuples (§3.8). Anything else is `TP0200`.
- **Built-in methods** follow §6, with argument counts checked by `TP0209`. `d.get` arrives with optionals in M6.

### 4.7 Modules (M9)
- **A program is a main file plus the modules it imports, directly or indirectly.**
  - `import geometry` loads `geometry.tpn` from the main file's directory. Every module of a program lives in that one directory, so there are no packages or dotted module names in v0.1.
  - A missing file is `TP0221`, reported at the import.
  - The module name `python` is reserved for Python interop (§10), and `dotnet` for .NET interop (M21, §13).
  - **Built-in modules (M37):** `sys`, `os`, `types` and since M42 `asyncio` (§3.20) are part of Taipan (§6.1), imported as other modules are (`import sys`, `from os import environ`, `import sys as system`), and never loaded from a file, so a `sys.tpn` beside the main file isn't imported. `from python import os` still binds Python's `os` module (§10.1); importing both under one name is `TP0214`. A dotted `from` path is a .NET namespace under `dotnet` (§13.1); any other dotted path is `TP0221`, since modules aren't dotted.
- **Module files contain declarations only:** `import`, `def`, `class`, `enum` and `const`. A top-level statement (including `let` and `var`) in a module other than the main file is `TP0222`. Only the main file has a top level that runs.
- **Exports:** a module exports every name it defines, meaning its functions, classes, enums, their variants, constants, protocols (M17) and type aliases (M18). There's no privacy, no re-export (a name a module itself imported can't be imported from it), and no `from m import *` (`TP0100`).
- **Imports:**
  - `import m` binds `m` in the importing file. Its names are then reached as `m.f(…)`, `m.LIMIT`, `m.Point(…)`, `m.Health.Ok` and `m.Ok` in expressions, `m.Point` and `m.Box[int]` in types, and `m.Health.Ok` in patterns.
  - `from m import f, Point` binds those names directly. Importing an enum also binds its variants unqualified, under the usual ambiguity rule (`TP0216`).
  - `as` renames either form: `import geometry as g`, `from m import area as surface`.
  - A name `m` doesn't define is `TP0201`.
- **Scope:**
  - Imports may appear anywhere at a file's top level, and like definitions they take effect throughout the file.
  - Imported names share the file's global scope. A clash with a definition, another import or a built-in is `TP0214`, and the usual shadowing rules apply (`TP0207`).
  - `import` inside a function is `TP0100`.
  - A module name used as a value (`print(geometry)`) is `TP0200`.
- **Cycles are allowed.** Since definitions are hoisted, `a` may import `b` while `b` imports `a`, and their functions may call each other.
- **Stages:** every file is lexed and parsed before any is type-checked, and §9's stage gating applies across all of them. Diagnostics name the file they're in, by its path relative to the main file's path as given (for example `tests/cases/m9/shapes/geometry.tpn`).
- **Order:** constants are initialized module by module, each module after the modules it imports (a cycle is broken at the file reached first), and the main file's last.

### 4.8 Special methods and class members (M28)
```python
class Money:
    cents: int
    currency: str
    count: ClassVar[int] = 0

    def __init__(self, cents: int, currency: str):
        self.cents = cents
        self.currency = currency
        Money.count += 1

    @classmethod
    def parse(cls, s: str) -> Money:
        let parts = s.split(" ")
        return cls(int(parts[0]) ?? 0, parts[1])

    @property
    def dollars(self) -> float:
        return self.cents / 100

    def __add__(self, other: Money) -> Money:
        return Money(self.cents + other.cents, self.currency)

    def __radd__(self, other: int) -> Money:
        return Money(self.cents + other, self.currency)

    def __eq__(self, other: Money) -> bool:
        return self.cents == other.cents and self.currency == other.currency

    def __hash__(self) -> int:
        return hash((self.cents, self.currency))

    def __str__(self) -> str:
        return f"{self.dollars:.2f} {self.currency}"

class Shape(ABC):
    @abstractmethod
    def area(self) -> float: ...
```

**Special methods.** A class may define these, each with exactly the parameters shown (after `self`, without defaults) and the result shown, or it's `TP0225`, `'<m>' must be 'def <m>(self, …) -> <R>'`. Any other name of the form `__x__` (`__bool__`, `__len__`, `__ne__`, `__iadd__`, `__format__`, …) is still `TP0225`.

| Method | Signature | Used by |
|---|---|---|
| `__str__` | `(self) -> str` | `print`, `str()`, f-string holes |
| `__repr__` | `(self) -> str` | the form inside lists, dicts, tuples and dataclasses, `!r`, `repr()`, and `str()` when there's no `__str__` |
| `__eq__` | `(self, other: C) -> bool`, where `C` is the class or one of its bases | `==`, `!=`, `in`, `contains`, list and dict equality, dict keys |
| `__hash__` | `(self) -> int` | dict keys, `hash()` |
| `__lt__`, `__le__`, `__gt__`, `__ge__` | `(self, other: T) -> bool`, for any `T` | `<`, `<=`, `>`, `>=`; `sorted` and `sort` use `__lt__` only (§3.11) |
| `__add__`, `__sub__`, `__mul__`, `__truediv__`, `__floordiv__`, `__mod__`, and `__pow__` (M35) | `(self, other: T) -> R`, for any `T` and `R` | `+ - * / // % **` with the class value on the left |
| `__radd__`, `__rsub__`, `__rmul__`, `__rtruediv__`, `__rfloordiv__`, `__rmod__`, `__rpow__` (M35) | `(self, other: T) -> R` | the same operators with the class value on the right |
| `__neg__` | `(self) -> R` | unary `-` |
| `__iter__` | `(self) -> Iterator[T]` | `for`, `iter()` and every built-in that takes an `Iterable[T]` (M31, §3.13) |
| `__next__` | `(self) -> T` | `next()`, and taking a value from the class as an `Iterator[T]` (M31, §3.13) |
| `__enter__` (M37) | `(self) -> R`, for any `R` | `with` (§3.17) |
| `__len__` (M39) | `(self) -> int` | `len(x)`; a negative result raises `ValueError`, `__len__() should return >= 0` |
| `__getitem__` (M39) | `(self, key: K) -> V`, for any `K` and `V` | `x[k]` |
| `__setitem__` (M39) | `(self, key: K, value: V)`, returning nothing | `x[k] = v`, and with `__getitem__`, `x[k] op= v` |
| `__contains__` (M39) | `(self, item: T) -> bool` | `in` and `not in` |
| `__call__` (M39) | `(self, …) -> R`: any parameters a method may have (§4.2), defaults and keywords included | `x(…)`, and converting `x` to a function type (§4.9) |
| `__iadd__`, `__isub__`, `__imul__`, `__itruediv__`, `__ifloordiv__`, `__imod__`, `__ipow__` (M39) | `(self, other: T) -> R` | `x += y` and the others (§4.9) |
| `__format__` (M39) | `(self, spec: str) -> str` | f-string holes, with the hole's spec (§4.9) |
| `__exit__` (M37) | `(self, exc_type: type[BaseException]?, exc: BaseException?, tb: TracebackType?) -> bool`, or `-> None` | `with` (§3.17); a class with one must have the other, or it's `TP0225`, `'<C>' defines '__exit__' but not '__enter__'` (or the reverse) |
| `__aiter__` (M42) | `(self) -> AsyncIterator[T]` | `async for` (§3.20) |
| `__anext__` (M42) | `async (self) -> T` | `anext()`, and each value of `async for`, ending with `StopAsyncIteration` (§3.20) |
| `__aenter__`, `__aexit__` (M42) | as `__enter__` and `__exit__`, each an `async def` | `async with` (§3.20); a class with one must have the other (`TP0225`) |

- Special methods are inherited and overridden like other methods (§4.3), with `@override` when a base class defines the method in its body. Defining one that only Python's `object` has (`__str__`, `__eq__`, …), or that a dataclass generates (below), needs no `@override`.
- Calling one directly (`m.__add__(n)`, `m.__str__()`) is `TP0200`, `'<m>' cannot be called directly`, with help naming its form (`use '+'`, `use 'str(m)'`, `use 'repr(m)'`, `use 'hash(m)'`), except as `super().__m__(…)` in an override.
- A special method can't be a property, static method or class method (`TP0224`).
- `x op= y` is `x = x op y` (§4.5), as in Python without `__iadd__`. Since M39, a class's in-place method runs instead when it has one (§4.9).

**Display.** `str(x)`, `print` and f-string holes call `__str__`. A class without one in its hierarchy uses `__repr__`, as Python's `object.__str__` does. A class without `__repr__` gets the M15 forms: `<Dog object>` for a plain class, and for a dataclass the generated `Point(x=1.0, y=0.0)`. As in Python, every dataclass generates its `__repr__` unless its own body defines one, so a dataclass deriving from one with a custom `__repr__` displays in the generated form. `repr(x) -> str` (§6) gives the form inside a container for any value, which `!r` writes. Format specs on a class value are `TP0200` unless it has `__format__` (M39, §4.9).

**Equality.**
- `a == b` on classes needs related types, as in M15 (§4.3). At run time it follows Python's protocol:
  1. If `b`'s runtime class is a proper subclass of `a`'s, `b.__eq__(a)` is tried first.
  2. Then `a.__eq__(b)`, then `b.__eq__(a)`.
  3. If none applies, `a` and `b` are equal only if they're the same object.
- A method *applies* when the other operand is an instance of its parameter's class at run time. Otherwise it's skipped, as when a Python `__eq__` returns `NotImplemented`. Taipan never calls a method with a value its parameter doesn't accept.
- **A dataclass** generates `__eq__` unless its own body defines one, as in Python: it applies when both operands have the same runtime class, and compares every field. So a dataclass deriving from one with a custom `__eq__` compares field by field.
- `a != b` is `not (a == b)`, as Python's default `__ne__` gives.
- Inside lists, dicts and tuples, and for `in` and `contains`, an element is equal to itself before `__eq__` is consulted, as in Python's containers.

**Hashing and dict keys.**
- A class is **hashable** unless its body defines `__eq__` without `__hash__`, in which case `__hash__` is removed, as in Python. A class that defines neither inherits both, so a plain class is hashable by identity. **A dataclass** is hashable only if its own body defines `__hash__`, since the generated `__eq__` removes it otherwise.
- `dict[C, V]` (and a tuple key containing `C`) needs `C` hashable: otherwise `TP0200`, `dict keys must be hashable, found <C>`, with help `'<C>' defines '__eq__' without '__hash__'` or `a dataclass needs its own '__hash__' to be a key`.
- **A subclass of a hashable class must be hashable** (`TP0225`, `'<C>' would not be hashable, but its base '<B>' is`, with help `define '__hash__'`), so that a `dict[Animal, V]` never meets an unhashable `Dog` at run time. Python raises `TypeError` there.
- Keys are found by `__hash__`, then by identity, then by `==` as above. Objects that are equal must hash equally; Taipan doesn't check it, as Python doesn't.
- `hash(x) -> int` (§6) gives `x.__hash__()` reduced as Python reduces it, so `hash(x)` is never `-1`.

**Ordering.**
- `a < b` is valid when `a`'s class has `__lt__` taking `b`, or `b`'s class has `__gt__` taking `a`. `<=` pairs with `__ge__`, `>` with `__lt__`, and `>=` with `__le__`. Otherwise it's `TP0200`, `operator '<' cannot be applied to <A> and <B>`, with help `define '__lt__' on '<A>'` when `a` is a class.
- At run time it follows Python's protocol: if `b`'s runtime class is a proper subclass of `a`'s, `b`'s reflected method (`__gt__` for `<`) is tried first. Then `a`'s method, then `b`'s reflected one, each only when it applies (above), using the methods of the operands' runtime classes. One of them always applies, since the check above found one for the static types.
- Without `@total_ordering` (M39, §4.9), `<=` needs `__le__` (or a reflected `__ge__`) even when the class has `__lt__` and `__eq__`.

**Arithmetic.**
- `a + b` uses `a`'s class's `__add__` when it takes `b`. Otherwise, it uses `b`'s class's `__radd__` when it takes `a`. Otherwise the built-in rules of §5.3 apply, and failing those it's `TP0200`. So `m + n`, `m + 5` with `__add__(self, other: int)`, and `0 + m` with `__radd__(self, other: int)`. The result has the method's return type, and the other operand converts to its parameter as for any argument (`m * 2` with `__mul__(self, k: float)`).
- **Subclass priority.** At run time, if `b`'s runtime class is a proper subclass of `a`'s and defines a different `__radd__` that takes `a`, `b.__radd__(a)` runs instead, as in Python. So that its result fits the expression's type, a class's `__rop__` must return a type assignable to the result of any base's `__op__` that the class fits: `TP0228`, `'<D>.__radd__' must return <R>, since it can run in place of '<B>.__add__'`.
- When `a + b` uses `b.__radd__(a)`, and `a`'s class, or a class derived from it, has an `__add__` that a `b` could fit at run time, it's `TP0200`, `'+' on <A> and <B> is ambiguous: '<C>.__add__' would run when it fits`. Python would call that `__add__` first.
- `-a` calls `a.__neg__()`.

**Properties.**
- `@property` makes a method with no parameters other than `self`, and a return type, into a read: `m.dollars`, not `m.dollars()`. Other parameters, or no return type, are `TP0225`.
- `@<name>.setter`, on a method named the same that directly follows the property in the class body, takes one parameter of the property's type and has no return type (`TP0225` otherwise). Then `m.dollars = 2.5` and `m.dollars += 1.0` call it, the latter after reading the property once. Without a setter, assignment is `TP0202`, `cannot assign to 'dollars': it is a read-only property`.
- Properties are members like fields and methods (§4.6), and dispatch on the runtime class. A property overrides only a property, with `@override` on its getter: the same type, or for a property without a setter, a class type assignable to the base's (`TP0228`). If the base's property has a setter, the override must have one too (`TP0228`), since Python's overriding property would otherwise be read-only.
- A property is code, not storage: reading or setting one counts as a call. It ends the narrowing of field paths (§3.9), isn't narrowed itself (`TP0203`, with help `'<p>' is a property, so it may change; copy it to a 'let' first`), and in `__init__` it's a use of `self` (`TP0226`) until the object is complete.
- `C.dollars`, a property reached through its class rather than an instance, is `TP0200`, `'dollars' is a property; read it from an instance`.

**Static and class methods.**
- `@staticmethod` makes a method without `self`: `def make(n: int) -> C:`. `@classmethod` makes one whose first parameter is `cls`, written without a type: `def parse(cls, s: str) -> C:`.
- Both are called through the class (`C.parse(s)`, `zoo.C.parse(s)`, `Sub.parse(s)`) or an instance (`m.parse(s)`, `self.parse(s)`), and inside a class method through `cls` (`cls.other()`). An instance method or property called through a class (`C.speak()`) is `TP0200`, `'speak' needs an instance of 'C'`.
- **`cls`** is the class the method was called through: `Sub.parse(s)` and `sub.parse(s)` run with `cls` as `Sub`, even when `parse` is inherited, as in Python.
  - `cls(…)` constructs an instance of that class. Its type is the method's class, and its arguments are checked against that class's constructor. So that it's valid for every class `cls` can be, each class derived from a class with a class method that calls `cls(…)` must take the same constructor arguments: the same parameters, as for an override (§4.3), with a default wherever the base has one. Otherwise it's `TP0228`, `'<D>' must be constructed as '<C>' is, since '<C>.<m>' calls 'cls(…)'`. A dataclass that adds a field changes its constructor, and so is reported.
  - `cls.m(…)` calls a static or class method, and `cls.a` reads a class attribute, both by `cls`'s class.
  - Any other use of `cls` (passing, storing, displaying or comparing it) is `TP0200`, `'cls' can only be called, or used to reach static and class methods and class attributes`.
- Static and class methods override like instance methods (§4.3), with `@override`, and only one of the same kind (`TP0228`, `'<m>' is a <static method|class method|method|property> in '<B>'`). Through an instance or `cls` they dispatch on the runtime class, and through a class name they're found from that class upwards.
- They can't use `self` (`TP0201`), and a class method's `cls` follows the `self` rules: it's an immutable parameter, and a local can't take its name.
- Before M39, using a static or class method without calling it was `TP0200`; since M39 it's a function value (§4.9). An instance method used without calling it is still `TP0200`, with help `wrap it in a lambda`.
- Before M39, a generic class couldn't have static or class methods or class attributes (`TP0225`); since M39 it can (§4.9).

**Class attributes.**
- `count: ClassVar[int] = 0` in a class body declares a class attribute, shared by the class, its subclasses and their instances. A field declaration without `ClassVar` is still per instance (§4.3). `ClassVar[T]` is valid only as the whole annotation of a class-body declaration (`TP0200` elsewhere), and the attribute needs a value (`TP0225`, `class attribute 'count' needs a value`).
- The value may contain only what a `const` initializer may (§4.5), plus `None` and list, dict and tuple literals of such values, since it's evaluated once, before any code runs (`TP0217`, `class attribute value must be a constant expression`).
- It's read as `C.count`, `Sub.count`, `self.count`, `m.count` or `cls.count`. It's assigned only through the class that declares it, `C.count = 5` or `C.count += 1`: through a subclass, an instance or `cls`, it's `TP0202`, `cannot assign to 'count' through '<x>'`, with help `assign 'C.count'`. In Python those would create a new attribute that hides the class's.
- A class attribute is narrowed as a field path is (§3.9), rooted at its class: `if C.cache is not None:` narrows `C.cache` until a call or an assignment to it.
- Class attributes share the member namespace (§4.6). A subclass can't redeclare one (`TP0214`). They aren't fields: they're not constructor parameters of a dataclass, aren't displayed, and aren't compared.

**Abstract classes.**
- `class Shape(ABC):` makes `Shape` able to have abstract methods. `ABC` is a built-in name, as `Protocol` is (Python imports it from `abc`), and it doesn't count as a base class, so `class Shape(Base, ABC)` is valid, and so is a dataclass deriving from it.
- `@abstractmethod` marks a method, or a property, as abstract. Its body is `...` (`TP0225` otherwise). It's valid only in a class that derives from `ABC`, directly or through its bases, and not together with `@override`, `@staticmethod` or `@classmethod` (`TP0224`).
- **A class is abstract** if it has an abstract method that neither it nor a class between overrides. Overriding one needs `@override` (§4.3), and must be compatible (`TP0228`).
- **Compile-time checks (`TP0234`):**
  - Constructing an abstract class, `Shape()` or `Square()`, is `TP0234`, `cannot instantiate abstract class 'Square'`, with help `it does not implement 'Shape.area'` (listing each such method: `'Shape.area' and 'Shape.perimeter'`).
  - `cls(…)` in a class method of an abstract class is `TP0234`, since `cls` may be that class.
  - A class can't add an abstract method when a base class has a class method that calls `cls(…)` (`TP0234`, `'<C>' cannot add abstract method '<m>', since '<B>.<cm>' calls 'cls(…)'`), so that the call stays valid when `cls` is the new class.
- `super().area()` that reaches an abstract method is `TP0200`, `'Shape.area' is abstract`.
- An abstract class may have fields, `__init__` and concrete methods, and may be a type: values of it are instances of its concrete subclasses.


### 4.9 Classes III (M39)
```python
@dataclass(frozen=True, order=True)
class Version:
    major: int
    minor: int = 0

    def __post_init__(self) -> None:
        if self.major < 0:
            raise ValueError("negative version")

class Grid:
    cells: dict[tuple[int, int], str]

    def __init__(self):
        self.cells = {}

    def __len__(self) -> int:
        return len(self.cells)

    def __getitem__(self, at: tuple[int, int]) -> str:
        return self.cells.get(at, ".")

    def __setitem__(self, at: tuple[int, int], value: str) -> None:
        self.cells[at] = value

    def __call__(self, row: int) -> str:
        return "".join(self[(row, c)] for c in range(3))

    def copy(self) -> Self:
        ...
```

**Dataclass options.** `@dataclass(frozen=True)`, `@dataclass(order=True)` or both, with the literal `True` or `False`; any other argument or value is `TP0224`.
- **`frozen=True`:** a field of the class can't be assigned once the object exists. Assigning one anywhere, `self.f = …` in a method and `__post_init__` included, or with `op=`, is `TP0202`, `cannot assign to '<f>': '<C>' is frozen`. Python raises `FrozenInstanceError` when the line runs. A field holding a list may still have its list changed.
  - A frozen dataclass is hashable, as in Python: it gets a generated `__hash__` over its fields, unless its body defines `__eq__` or `__hash__` (§4.8). If a field's type isn't hashable (a list, a dict, an unhashable class), the class isn't either, and using it as a key is `TP0200`, with help `'<f>' is <T>, which isn't hashable`. Python raises `TypeError` when such a value is hashed.
  - A frozen dataclass can derive only from a frozen one, and one that isn't frozen only from one that isn't (`TP0225`, `a frozen dataclass can't derive from one that isn't`, or the reverse), as Python's `dataclass` raises `TypeError`.
- **`order=True`** generates `__lt__`, `__le__`, `__gt__` and `__ge__`, which compare the fields as a tuple, in constructor order (inherited fields first), as Python's do.
  - Every field must be ordered as `sorted`'s values are (§6): `TP0225`, `'order=True' needs fields that can be ordered: '<f>' is <T>`.
  - A body that defines any of the four with `order=True` is `TP0225`, as Python raises `TypeError`.
  - `a < b` needs related classes (§4.8). At run time, operands of different runtime classes raise `TypeError`, `'<' not supported between instances of 'A' and 'B'`, as in Python. `sorted` and `sort` use the generated `__lt__`.
- **`__post_init__(self) -> None`**, in a dataclass only (`TP0225` in a plain class, or with another signature), runs at the end of the generated constructor, once every field is set, so `self` may be used freely. It's inherited and overridden like other methods (with `@override`), and as in Python, only the most derived one runs; an override calls the base's with `super().__post_init__()`.

**`@final`.** On a class, deriving from it is `TP0225`, `cannot derive from final class '<C>'`. On a method (or property, static or class method), overriding it is `TP0228`, `'<B>.<m>' is final`. As with `@override` and `ABC`, `final` is a built-in name; Python imports it from `typing`.

**`Self`** may appear in a class's method signatures: parameter and return types, inside other types too (`list[Self]`, `Self?`), in instance methods, class methods and properties. Anywhere else in a class, such as a field or a static method, it's `TP0201` as before.
- In a result, at a call, it's the static type of what the method is called on: `dog.copy()` is a `Dog` when `copy` is `Animal`'s, and `Dog.create()` through a class method is a `Dog`.
- In a parameter, it's the class itself, so `def merge(self, other: Self)` takes any instance of the class, through a subclass too. Python's type checkers take only the receiver's type there.
- In the body, a `Self` value is used as the class. Where `Self` itself (or `Self?`) is the result, a `return` must give a `Self` value: `self`, `cls(…)` in a class method, or the result of a call returning `Self` on those. A value of the class itself isn't one: returning `Animal(…)` from `-> Self` is `TP0200`, `expected Self, found Animal`, since a subclass would get the wrong type, as Python's type checkers report.
- An override of a method returning `Self` must return `Self` too (`TP0228`), and one returning the class may return `Self`. In a generic class, `Self` is the class with its type parameters.

**Container special methods** (§4.8's table):
- `len(x)` calls `__len__`; `x[k]` calls `__getitem__`, with `k` checked against its parameter; `x[k] = v` calls `__setitem__`; `x[k] op= v` calls `__getitem__`, then `__setitem__`, evaluating `x` and `k` once. A class without the method is `TP0200`, as before. Slices (`x[a:b]`) and `del x[k]` on a class are deferred (`TP0200`).
- `a in x` calls `x.__contains__(a)`, with `a` checked against its parameter. Without `__contains__`, `in` goes through the class's `__iter__` or `__next__` (§3.13), as Python's fallback does.
- These are found on the static type and dispatch on the runtime class, as other methods do.

**`__call__`.** `x(…)` on a value of a class with `__call__` calls it, with arguments matched as for a method (§4.2). Where a function type is expected (§3.7), such a value converts to it, as Python's type checkers allow, when `__call__`'s positional parameters accept the function type's parameters and its result fits the function type's result (§3.3): `sorted(names, key=Lengths())`. The function value calls the object's `__call__`, dispatching on its runtime class, and displays as `<function Lengths.__call__>`.

**In-place operators.** `x op= y` uses `x`'s class's in-place method (`__iadd__` for `+=`, …) when it has one that takes `y`, and otherwise falls back to `x = x op y`, as in Python. The result is assigned to `x`, so it must be assignable to `x`'s type (`TP0200`). For a target that's a `let` (§4.1), which can't be reassigned, the method must return `Self` or `x`'s class, and at run time it must return `x` itself, as an in-place method usually does; another object raises `TypeError`, `'+=' can't rebind a 'let': '__iadd__' returned another object`. Otherwise `op=` on a `let` is still `TP0202`.

**`__format__`.** In an f-string hole, a value of a class with `__format__` is formatted by calling it with the hole's spec, or `""` without one, as Python's `format(x, spec)` does: `f"{v:short}"`. Its spec isn't checked at compile time. `print` and `str()` still use `__str__`, and `!r` and `!s` convert first, as in Python.

**`@total_ordering`.** On a class that defines at least one of `__lt__`, `__le__`, `__gt__` and `__ge__` (`TP0225` otherwise), it generates the others the class doesn't define (or inherit), as Python's `functools.total_ordering` does, from the first of those four that it defines, using `==`: from `__lt__`, `a <= b` is `a < b or a == b`, `a > b` is `not a < b and a != b`, and `a >= b` is `not a < b`. The generated methods take the defining method's parameter type. `total_ordering` is a built-in name; Python imports it from `functools`.

**Generic classes** may have static and class methods and class attributes.
- A static or class method is called through the class with type arguments, `Box[int].make(1)`, or without them, `Box.make(1)`, inferring them from the arguments and the expected type as a constructor call does (§3.6), or through an instance, `b.make(1)`, with the instance's. In a class method, `cls(…)` constructs the class with those type arguments.
- A class attribute is one for every instantiation, as in Python, so its type can't use the class's type parameters (`TP0200`, `a class attribute can't use 'T'`). It's read through the class, with or without type arguments, or an instance, and assigned only through the bare class name: `Box.count += 1`.

**Static and class methods as values.** `C.parse`, `zoo.C.parse`, `x.parse` and `cls.parse`, without a call, are function values of the method's type without `cls` (§3.7). A class method's value keeps the class it was reached through, as `cls`: `Sub.parse` constructs `Sub`s. A generic class's static or class method as a value needs type arguments, `Box[int].make` (`TP0200` otherwise, as for generic functions). Instance methods as values (bound methods) are still deferred.

---

## 5. Statements and expressions

### 5.1 `match`
- The scrutinee can be an enum, `int`, `str`, `bool`, a class (M15), since M29 a tuple or a list, and since M38 a `float`, a dict, or an optional of any of these.
- **Patterns** (M29 makes them nest, as in Python):
  - `_` matches anything and binds nothing.
  - **A name** binds the value, as a `let` scoped to the arm: `x`, `Degraded(h, t)`. It's a binding unless it names a variant of the enum its position has (below).
  - **Literal patterns** (`0`, `-1`, `"api"`, `True`, `False`) apply to `int`, `str` and `bool`. Since M38, float literals (`1.5`, `-0.5`, `1e3`) apply to `float`, and so do int literals, as in Python: each matches a value `==` to it, so `0.0` matches `-0.0` and nothing matches a NaN.
  - **`None` (M38)** matches `None`, at an optional position only. Anywhere else it's `TP0200`, `<T> can't be None`.
  - **Variant patterns:** `Ok`, `Degraded(h, t)`, `Health.Down`. Each argument is a sub-pattern for a payload field, by position. With a payload, the argument count must equal the field count. A variant with a payload may also be matched as `Degraded` without parentheses, matching any payload.
  - **Class patterns (M15):** `Dog()`, `Dog(name=n, breed="lab")`, and for a dataclass `Point(0, y)` by position, in constructor order, as Python's `__match_args__` does. Each argument is a sub-pattern for a field.
  - **Tuple patterns (M29):** `(0, y)`, `(Circle(r), _)`, with one sub-pattern per element.
  - **List patterns (M29):** `[]`, `[a, b]`, `[first, *rest]`, `[*init, last]`, `[a, *_, z]`. Without a star, a list matches when it has exactly as many elements as sub-patterns; with one, when it has at least as many as the others. `*name` binds the rest as a new list, and `*_` ignores it.
- **Patterns M38 adds**, as in Python:
  - **Or-patterns:** `A | B | C` matches when any alternative does, tried left to right: `Ok | Degraded(_, _)`, `0 | 1`, `"y" | "yes"`. Each alternative must bind the same names, or it's `TP0200`, `alternatives bind different names: '<x>' is not bound in every one`. A name's type must be the same in each, except that classes join to their nearest common base (§3.2), as `except (A, B) as e` does: `Circle(r) | Square(r)` needs both fields to have one type, and otherwise it's `TP0200`, `'<x>' is <A> in one alternative and <B> in another`. An alternative that can't match anything the ones before it, or earlier arms, don't is warning `TP0300`, `unreachable alternative`.
  - **Groups:** `(p)` is `p`, so `(A | B) as x` and `Point((0 | 1), y)` work. Before M38, `(p)` was a one-element tuple pattern, `TP0209`; `(p,)` still is.
  - **`as` patterns:** `p as name` matches when `p` does and binds `name` to the value, with the type `p` narrows it to: the class of a class pattern, the common base of an or-pattern's classes, the inner type for a pattern that never matches `None` at an optional position, and otherwise the position's type. `_ as x` is the same as `x`. `as` binds loosest: `A | B as x` is `(A | B) as x`.
  - **Mapping patterns:** `{"name": n, "port": 8080}` matches a `dict[K, V]` with every listed key, each value matching its sub-pattern (at type `V`), ignoring any other keys, as in Python. Keys are literals of `K`'s type, which must be `int`, `str` or `bool` (`TP0200` otherwise, and a mapping pattern elsewhere is `TP0200`, `<T> cannot match a mapping pattern`). A key listed twice is `TP0214`. A final `**rest` binds a new `dict[K, V]` of the other keys, in the dict's order; `**_` and a `**` anywhere but last are `TP0100`, as in Python. `{}` matches every dict.
  - **`*` in tuple patterns:** `(first, *rest)`, `(*init, last)`, `(a, *_, z)`. The tuple must have at least as many elements as the other sub-patterns (`TP0209`). `*rest` binds a `list` of the elements it stands for, typed as a starred name in tuple unpacking is (§3.8), and `*_` ignores them. At most one `*` per tuple pattern (`TP0100`).
- **Guards (M38):** an arm may end its pattern with `if <condition>`: `n if n > 0:`, `Point(x, y) if x == y:`. The guard is a `bool` (`TP0205`), evaluated after the pattern matches, with its bindings in scope; when it's `False`, the next arm is tried. It narrows the arm's body as an `if` does (§3.9). This is Python's `case p if …:` without `case` (§5.1 has no `case` keyword), and match expressions' arms take guards too (§5.1.1).
- **Optional positions (M38):** any pattern may appear at a position of type `T?`. `None` matches only `None`; every other pattern but `_` and a binding matches only a value that isn't `None`, checked against `T`. In an arm whose pattern can't match `None`, a scrutinee that's a name that can't change (§3.9) is narrowed to `T`. A binding at an optional position has type `T?`, except at the top level of the scrutinee after earlier unguarded arms that cover `None`, where it has type `T`, as Python's type checkers narrow the subject: after `None: …`, the arm `n: n + 1` sees an `int`.
- **Bare names (M29).** In a position whose type is an enum, a bare name that's one of its variants is a variant pattern: `Some(Ok)` matches the variant `Ok`. A bare name that names a variant or a class in scope is never a binding either, since a local can't shadow it (§4.4, §4.6): it's checked as that pattern, so a variant of another enum is `TP0200`, `expected <T>, found <E>`, and a class is `'Cat' is a class; write 'Cat()'`. Any other bare name binds. So that a misspelled variant isn't silently a binding, a bare name in an enum-typed position that starts with an uppercase letter and isn't a variant of that enum is `TP0210`, `<E> has no variant '<x>'`, as before M29. A qualified name (`Health.Ok`, `m.Health.Ok`) is always a variant pattern.
- **Class patterns** match when the value's runtime class is the named class or derives from it, as with `isinstance`:
  - Unlike a variant, a class pattern always has parentheses, as in Python: a bare `Dog` is `TP0200`.
  - Keyword arguments name any field of the class, inherited ones included, in any order. Positional ones may come first, for a dataclass only (`TP0209` on a plain class, and more than there are fields is `TP0209`).
  - A field named twice is `TP0209`, and an unknown one is `TP0210`.
  - The class must derive from the type of its position, or be it (`TP0200` otherwise). In its arm, a scrutinee that's a name that can't change (§3.9) is narrowed to that class.
- **Types.** Each position has a type: the scrutinee's, then a payload field's, a class field's, a tuple element's, or a list's element type. A sub-pattern must fit it: a literal of another type, a variant pattern where there's no enum, a tuple pattern of another length (`TP0209`, `<T> has <n> elements, found <m> patterns`), or a list pattern on anything but a list, is `TP0200`. A binding gets its position's type, and `*rest` gets a `list` of the element type. Before M38, an optional position took only `_` and bindings, and other patterns there were `TP0203`.
- **Exhaustiveness** (generalized in M29): a `match` must cover every value its scrutinee's type can have, through all nesting. Otherwise it's `TP0204`. It's checked as Python's type checkers do (pyright's "exhaustive match"), by the usefulness of each arm against the arms before it:
  - An enum is covered by its variants, a `bool` by `True` and `False`, a tuple or a class of the scrutinee's own type by one pattern whose sub-patterns cover each element or field, and a list by patterns covering every length.
  - `int` and `str` positions, and class patterns of a subclass, are covered only by a `_` or a binding there: subclasses are open-ended, so listing them never covers the base.
  - Since M38: a `float` position is covered only by `_` or a binding; an optional by `None` together with patterns covering `T`; a dict by a mapping pattern without keys (`{}` or `{**rest}`), since one with keys may find them missing; an or-pattern covers what any alternative does, an `as` pattern what its pattern does, and a tuple pattern with `*` what it would with `_` in the starred elements' places.
  - **A guarded arm covers nothing**, since its guard may be `False`, so a `match` whose only arm for a case is guarded is `TP0204`, and a guarded arm never makes a later arm unreachable. A guarded arm is itself unreachable (`TP0300`) when its pattern is.
  - The message lists, in order, a missing case for each kind of value no arm covers, written as patterns with `_` for any value: `missing: Down, Degraded(0, _), Move((_, True))`, at most 12 of them. A variant no arm names is written bare, as before M29 (`missing: Down, Ok`); one that an arm names is written with its payload, `Level(_)`. A class pattern shows only the fields that matter, `Point(y=False)`. Lists are written by length, `[]` or `[_, _, *_]` for any longer.
- **Reachability:** an arm that can't match anything the arms before it don't, such as one after `_`, a duplicate, or `(0, _)` after `(_, _)`, is warning `TP0300` (unreachable). Unreachable arms are still checked, but not emitted.
  - The missing cases `TP0204` lists (above) may now be `None`.
- `match` is a statement, and since M19 also an expression (§5.1.1).
- **Checking details:**
  - Any other scrutinee type is `TP0200`, and an optional scrutinee is `TP0203`.
  - The scrutinee is evaluated once. Sub-patterns are tested left to right, and a list's length before its elements. A guard is evaluated only after its whole pattern has matched.
  - Variant patterns are resolved against their position's enum, so an unqualified variant pattern is never ambiguous. A variant the enum doesn't have is `TP0210`, and a qualified pattern naming another enum is `TP0200`.
  - A variant without a payload may be written with or without empty parentheses. Arguments to it otherwise are `TP0209`.
  - **`_`** may repeat, and so may `*_`. A list pattern with two stars is `TP0100`, `a list pattern can have only one '*'`.
  - A pattern whose position's type already has an error isn't reported again, and the `match` isn't checked for exhaustiveness or reachability.
  - Bindings follow the shadowing rules (`TP0207`), and a name bound twice within one pattern is `TP0214` (a name bound in each alternative of an or-pattern is bound once).
  - `TP0204` points at the `match` keyword.
- **Return paths:** a `match` terminates (§4.5) if every arm's body terminates. Exhaustiveness is guaranteed once it type-checks.
- **Control flow:** `break` and `continue` in an arm apply to the enclosing loop, as anywhere else.

### 5.1.1 `match` expressions (M19)
A `match` can produce a value, with each arm an expression:

```python
let label = match status:
    Ok: "up"
    Degraded(h, _): f"degraded ({h})"
    Down: "down"

def area(s: Shape) -> float:
    return match s:
        Circle(radius=r): 3.14159 * r * r
        Rect(w, h): w * h
        _: raise ValueError("unknown shape")
```

- **Where:** before M29, only as the whole right-hand side of `let`, `var` or an assignment (`=` or a compound `+=`), or the whole value of `return`. Since M29, anywhere an expression may appear: an operand (`1 + match x: …`), an argument, an element, a lambda's body. Python has no `match` expression; this follows the statement's syntax, with a value after each pattern's colon instead of a statement.
  - Since its arms are indented lines below it, a `match` expression ends the line it starts on: its `:` is the line's last token.
  - Outside brackets, the arms end the statement: `let n = 1 + match x:` and then its arms.
  - Inside brackets, the lexer indents the arms as it would outside (§1.2), and the bracketed expression continues on the first line indented no more than the line with `match`:
    ```python
    print(match shape:
        Circle(r): 3.14 * r * r
        _: 0.0
    , "units")
    let by_area = sorted(shapes, key=lambda s: match s:
        Circle(r): r * r
        Square(side): side * side
    )
    ```
  - A `match` at the start of a statement is always a `match` statement.
- **Block arms (M29):** an arm may be an indented block instead of a value on its line. Its statements run in order, and its last statement gives the arm's value: an expression statement, or a statement that terminates (§4.5), such as `raise`, which gives none. Anything else last is `TP0200`, `this arm gives no value`, with help `end it with an expression`. A `return`, or a `break` or `continue` of a loop outside the `match`, can't leave a `match` expression, which is evaluated in the middle of its statement: each is `TP0215`, `'return' can't leave a 'match' expression`, with help `use a 'match' statement`.
  ```python
  let cost = match order:
      Express(weight):
          let base = rate(weight)
          base * 2
      Standard(weight): rate(weight)
  ```
  Bindings and other names a block declares are scoped to it.
- **Patterns** are the statement's (§5.1): the same scrutinee types, patterns, guards (M38), bindings, narrowing and checks. Bindings are scoped to their arm's expression.
- **Exhaustiveness** is required, as for the statement: a missing case is `TP0204` at `match`. An arm after `_` or a duplicate arm is warning `TP0300`, and is still checked, but doesn't count toward the result type and isn't emitted.
- **Raising arms:** an arm may be `raise e` (or a bare `raise` inside an `except` clause, §3.10). It produces no value, so it doesn't count toward the result type. At least one reachable arm must give a value, or it's `TP0200`, `every arm of this 'match' raises`, with help `use a 'match' statement`.
- **Result type:**
  - With an expected type (an annotated `let` or `var`, an assignment target, a `return` in a function with a return type), each arm is checked against it, as each element of a list literal is (§4.6). So `let x: float = match n: …` accepts `int` arms.
  - Without one, the arms' types join as a list literal's elements do (§4.6): the same type, `int` and `float` to `float`, classes to their nearest common base, and `None` making the result optional (`1` and `None` give `int?`). A mismatch is `TP0200`, `expected <T>, found <U>`, at the first arm that differs. An arm of type `None` alone can't be inferred (`TP0206`).
  - In a function without a return type, `return match …` is `TP0200` like any `return` with a value.
- **Evaluation:** the scrutinee is evaluated once, then only the matching arm's expression or block. A `match` expression is evaluated where it appears among its neighbours, left to right as any operand.
- **Return paths (§4.5):** `return match …` terminates, as any `return` does. A `let`, `var` or assignment whose `match` has a raising arm doesn't terminate, since other arms give values.

### 5.2 Loops
- `for x in e` works when `e` is:
  - `list[T]`, giving `x: T`
  - `dict[K, V]`, giving `x: K` (keys, insertion order)
  - `str`, giving `x: str` (one character each)
  - `range(...)`, giving `x: int`
  - since M31, `Iterator[T]` or `Iterable[T]`, giving `x: T`, or an instance of a class with `__iter__` or `__next__` (§3.13)
- The loop variable is a `let` scoped to the body.
- `for a, b in e` unpacks each element, which must be a tuple with that many elements, or since M29 a list (§3.8), with nesting and a starred name as for `let`. Each name is a loop variable.
- Mutating a list or dict while iterating over it panics.

### 5.3 Operators

| Operator | Operands | Result | Semantics |
|---|---|---|---|
| `+ - *` | int, int | int | checked; overflow panics |
| `+ - *` | numeric, mixed | float | int widens |
| `/` | numeric | float | always true division; ÷0 panics (floats too) |
| `//` | int, int | int | floor division (Python semantics); ÷0 panics |
| `%` | int, int | int | result takes the divisor's sign (Python semantics) |
| `+` | str, str | str | concatenation |
| `+` | list[T], list[T] | list[T] | new list |
| `**` | int, int | int | M35: checked; a negative exponent raises `ValueError`, `negative exponent; use a float base, as in 2.0 ** -1`, and is `TP0200` when it's a literal; `0 ** 0` is `1` |
| `**` | numeric, with a float | float | M35: as Python's: `0.0 ** -1` raises `ZeroDivisionError`, and a negative base with a fractional exponent raises `ValueError` (Python would give a complex number) |
| `**` | a class with `__pow__` (or `__rpow__`) | the method's | M35, §4.8 |
| `\| & - ^`, `<= < >= >` | set[T], set[T] | set[T], bool | M35, §3.15 |
| `== !=` | same type, int/float, or related classes | bool | structural for dataclasses, enums, lists, dicts, tuples; identity for plain classes (§4.3) and functions (§3.7); a class's `__eq__` since M28 (§4.8) |
| `is None`, `is not None` | T? | bool | M15; narrows (§3.9) |
| `< <= > >=` | numeric, or str (ordinal), or tuples of those (M12) | bool | tuples compare lexicographically |
| `< >` | a class with `__lt__`, or a type parameter bounded by a protocol with `__lt__` (M17) | bool | `a < b` is `a.__lt__(b)`, `a > b` is `b.__lt__(a)` (§3.11) |
| `< <= > >=`, `+ - * / // %`, unary `-` | a class with the matching special method (M28) | the method's | §4.8 |
| `in`, `not in` | T in list[T]; K in dict[K,V]; str in str; T in set[T] (M35) | bool | |
| `??` | T?, T | T | |
| `and or not` | bool | bool | short-circuit |

Comparing values of unrelated types is `TP0200`.

`-2 ** 2` is `-4` and `2 ** 3 ** 2` is `512`, as in Python: `**` binds tighter than a unary minus on its left, and groups to the right. `//=`, `%=` and `**=` (M35) assign as `+=` does.

In `a == b` and `a != b`, `a`'s type is `b`'s expected type (§4.6), so `m == Nothing` with `m: Maybe[int]`, and `xs == []`, are valid.

### 5.4 Indexing
- `list[i]` with an int index: out of range panics. Negative indices count from the end, as in Python.
- `dict[k]`: a missing key panics. Use `d.get(k) -> V?` instead.
- `str[i] -> str`, one character, with the same rules as list. Characters are Unicode code points, as for `len`.
- **Slicing (M12):** `xs[a:b]` and `xs[a:b:c]`, with any of the three parts omitted.
  - It applies to `list[T]`, giving a new `list[T]`, and to `str`, giving a `str`.
  - Each part is an `int`, or an `int?` where `None` means omitted.
  - The semantics are Python's:
    - Negative bounds count from the end, and out-of-range bounds clamp.
    - A negative step walks backwards, with the omitted bounds defaulting to the ends, so `xs[::-1]` reverses.
    - A step of 0 panics.
  - The result is always a new list, never shared with the original.
  - The target is evaluated first, then the parts, left to right.
  - Slicing a `py` value slices it in Python and gives `py`.
  - Anything else, including a tuple or a dict, is `TP0200`.

### 5.5 Slice assignment and `del` (M26)
- **Slice assignment** replaces part of a list, as in Python: `xs[1:3] = [7, 8, 9]`.
  - The target is a `list[T]`, or `py`, sliced as §5.4 says, and the value is a `list[T]`, checked with `list[T]` as its expected type, so `xs[1:1] = [0.5]` works on a `list[float]` and `xs[:0] = []` on any list.
  - With no step, or a step of 1, the slice may be replaced by a list of any length, so the list grows or shrinks.
  - With another step (`xs[::2] = [0, 0]`), the value must have exactly as many elements as the slice. Otherwise it's `ValueError`, with Python's message: `attempt to assign sequence of size 3 to extended slice of size 2`. A step of 0 is `ValueError` (`slice step cannot be zero`), as for slicing.
  - The value is copied first, so `xs[1:] = xs` works.
  - Compound assignment to a slice (`xs[1:3] += [1]`) is `TP0202`, `cannot use '+=' on a slice`. A slice of a `str` or a tuple is `TP0202`, as an element of one is (§4.6).
- **`del`** removes list elements and dict entries: `del xs[0]`, `del xs[1:3]`, `del xs[::2]`, `del d["k"]`, and `del xs[0], d["k"]`, which deletes left to right.
  - `del xs[i]` follows indexing's rules (§5.4): a negative index counts from the end, and one out of range is `IndexError` (`index 5 out of range for length 3`).
  - `del xs[a:b:c]` removes the slice's elements. Out-of-range bounds clamp, so it never raises, except for a step of 0.
  - `del d[k]` removes the entry, keeping the others in order. A missing key is `KeyError` (`missing key "k"`), as for reading one.
  - `del x[k]` and `del x[a:b]` on a `py` value delete in Python.
  - Deleting anything else is `TP0202`: a name (`cannot delete 'x'`, with help `assign it a new value instead`), a field, or an element or slice of a `str` or tuple. An expression that isn't a target at all (`del f()`) is `TP0100`, `cannot delete this expression`, as for assignment.
  - Deleting from a list or dict while iterating over it is `RuntimeError`, as any change is (§5.2).

### 5.6 `assert` (M44)
- **`assert c`** raises `AssertionError` when `c` is `False`, and does nothing otherwise. **`assert c, m`** raises `AssertionError(str(m))`, evaluating `m` only when `c` is `False`, as in Python. Without a message, the exception's message is empty, so it displays as `AssertionError` alone (§3.19).
- `c` is a condition: a `bool` (`TP0205`), and it narrows what follows as `if c:` would with the rest of the block inside it (§3.9), so `assert x is not None` makes `x` usable as a `T`. `m` may be any value that `str()` takes.
- An `assert` is always checked: there's no `-O` that turns it off.
- `assert(c, m)`, a tuple, is `TP0205`, since a tuple isn't a `bool`; Python only warns that it's always true.
- It's how a test (§20) fails, with the traceback pointing at its line.

---

## 6. Built-ins (v0.1)

**Functions**
- `print(*args)` writes each argument's display form, space-separated, then a newline. Since M37 it takes Python's keyword-only `sep: str = " "`, `end: str = "\n"`, `file: TextIO = sys.stdout` and `flush: bool = False` (§6.1).
- `input()` and `input(prompt: str) -> str` (M37) write `prompt` to stdout without a newline, flushing it, then read a line from stdin, without its line ending (`\n` or `\r\n`). At the end of the input, it raises `EOFError`, `EOF when reading a line`.
- `open(file: str, mode: str = "r", *, encoding: str = "utf-8", newline: str? = None) -> TextIO` (M37) opens a text file (§6.1).
- `len(x) -> int` takes a list, dict, str or, since M35, set (an iterator is `TP0200`, with help `convert it with 'list(…)' first`). For a `str` it counts Unicode code points, so `len("🐍")` is `1`.
- `range(stop)`, `range(start, stop)` and, since M35, `range(start, stop, step)` iterate only (they're not a list). A negative `step` counts down, and a `step` of 0 raises `ValueError`, `range() arg 3 must not be zero`.
- `str(x) -> str` gives the display form.
- `repr(x) -> str` (M28) gives the form a value has inside a list, as `!r` writes it: `repr("a")` is `"a"` with its quotes. A class's `__repr__` defines it (§4.8).
- `hash(x) -> int` (M28) takes any value that can be a dict key (§3.1), and is `TP0200` otherwise, `cannot hash <T>`. Equal values hash equally. For an `int` or `bool` it's Python's (`hash(-1)` is `-2`), for a class with `__hash__` it's that method's result reduced as Python reduces it (§4.8), and otherwise its value is unspecified and may differ between runs, as `str` hashes do in Python.
- `int(x: float) -> int` truncates toward zero. An `int` argument is returned unchanged. `int(x: str) -> int?` parses, returning `None` on failure. It accepts an optional sign and decimal digits with `_` only between digits (as in §1.3), with surrounding whitespace allowed. A value outside the int range gives `None`.
- `float(x: int) -> float`. A `float` argument is returned unchanged. `float(x: str) -> float?` accepts the same forms as `int(str)`, plus an optional fraction and exponent (`1.5`, `2e-3`, `.5`, `5.`), and `inf`, `infinity` and `nan` in any case. Otherwise it returns `None`.
- `range` can only be used as the iterable of a `for` loop. Any other use is `TP0200`.
- `sorted(xs: list[T], key=…, reverse=False) -> list[T]` (M11) returns a new list in ascending order.
  - `key: Callable[[T], K]` gives each element's sort key. It's called once per element, in list order, before any reordering.
  - The elements, or the keys when `key` is given, must be `int`, `float` or `str`, tuples of those (M12), or another `Comparable` type: a class with `__lt__`, or a type parameter bounded by a protocol with `__lt__` (M17, §3.11). They're ordered as by `<`. Otherwise it's `TP0200`. A NaN sorts before every other float.
  - `reverse: bool` sorts in descending order.
  - The sort is stable: equal elements keep their original order, with `reverse=True` too, as in Python.
  - `key` and `reverse` are optional and keyword-only.
- `map(f: Callable[[T], U], xs: Iterable[T]) -> Iterator[U]` and `filter(f: Callable[[T], bool], xs: Iterable[T]) -> Iterator[T]` (M11) call `f` once per element, in order. Since M31 they're lazy, as in Python 3: each calls `f` only when its next value is asked for (§3.13). Before M31 they returned new lists, so code that indexes, slices, measures or compares their results is now `TP0200`, with help `convert it with 'list(…)' first`.
- `enumerate(xs)` and `enumerate(xs, start)` (M12) give `(index, element)` tuples: `Iterator[tuple[int, T]]` for an `Iterable[T]`, which a `str` is as `Iterable[str]`. `start` is an `int` that defaults to 0. It may be given by position, or as the keyword argument `start=`, as in Python. Before M31 it gave a new list.
- `list(xs: Iterable[T]) -> list[T]` (M31) takes every value into a new list, in order: `list(map(f, xs))`. `list()` with no argument needs an expected type, as `[]` does (§4.6).
- `iter(xs: Iterable[T]) -> Iterator[T]` (M31) starts an iterator over `xs`; on an iterator it gives the iterator itself.
- `next(it: Iterator[T]) -> T` (M31) takes the next value, raising `StopIteration` when there's none. `next(it, default)` gives `default` instead, which is checked against `T`; a `None` default makes the result `T?`.
- `anext(it: AsyncIterator[T]) -> Awaitable[T]` (M42, §3.20) is `next`'s async form, raising `StopAsyncIteration`, with the same `default`.
- `isinstance(x, C) -> bool` (M15) is `True` when `x`'s runtime class is `C` or derives from it, as in Python, and `False` for `None`. Since M26, `isinstance(x, (A, B, …))` is `True` when `x` is any of the listed classes. It narrows `x` to the nearest class all of them derive from (§3.9), as `except (A, B) as e` binds (§3.10), and each listed class follows the rules for one (`TP0200` for a class `x` can never be).
  - `x` must be a class or an optional class, and `C` the name of a class (qualified for another module: `zoo.Dog`) that isn't generic. Anything else is `TP0200`. Since M40, `x` may also be a protocol value, a type parameter bounded by a class, or a `py` value (§3.18).
  - `C` must derive from `x`'s class, or be it or one of its bases. An unrelated class is `TP0200`, since the test could never be `True`.
  - It narrows `x` (§3.9).
- `super()` (M15) is valid only as `super().m(…)` in a method (§4.3).
- **Since M35**, as in Python:
  - `abs(x)` gives an `int` for an `int` (`OverflowError` for the smallest one) and a `float` for a `float`.
  - `min(a, b, …)` and `max(a, b, …)` take two or more arguments, and `min(xs)` and `max(xs)` any iterable. The values, or their keys with `key=`, must be ordered as `sorted`'s are (`TP0200` otherwise, as for `sorted`). With `int` and `float` arguments mixed, the result is a `float`, so `max(0, 2.5)` is `2.5` and `max(3, 2.5)` is `3.0`, where Python keeps the `3`. An empty iterable raises `ValueError`, `min() iterable argument is empty`, unless `default=` gives a value, which must have the element type (`None` gives `T?`). Of equal values, the first is the result. `key` and `default` are keyword-only.
  - `sum(xs)` and `sum(xs, start)` add an iterable of `int` or `float` values to `start` (default `0`): an `int` for `int` values and an `int` start, a `float` otherwise. Other element types are `TP0200`, `sum() takes numbers, found <T>`, with help `use ''.join(…)` for `str`.
  - `any(xs)` and `all(xs)` take an iterable of `bool`, stopping at the first `True` or `False`. Since Taipan has no truthiness, other element types are `TP0200`, with help `test each value, as in any(x > 0 for x in xs)`.
  - `round(x)` gives the nearest `int`, with ties to the even one (`round(2.5)` is `2`), and `round(x, n)` a `float` rounded to `n` digits after the point, as Python rounds the exact value (`round(2.675, 2)` is `2.67`). On an `int`, `round(x)` is `x` and `round(x, n)` rounds to `-n` tens (`round(1234, -2)` is `1200`). A NaN or infinity raises `ValueError` or `OverflowError`.
  - `divmod(a, b)` is `(a // b, a % b)`, a `tuple[int, int]` or `tuple[float, float]`.
  - `set()` and `set(xs)` (§3.15).
- `zip(xs: Iterable[A], ys: Iterable[B], …) -> Iterator[tuple[A, B, …]]` (M12) groups the values of two or more iterables, stopping at the shortest, as in Python: `zip(names, ages, cities)` is an `Iterator[tuple[str, int, str]]`. For each tuple it takes a value from each iterable in turn, left to right, and stops at the first that has none, so `zip(naturals(0), ["a", "b"])` ends. Before M26 it took exactly two lists, and before M31 it gave a new list. One iterable, which would need a one-element tuple, is `TP0209`.
- `sorted(xs: Iterable[T], …)` takes any iterable since M31, and still gives a new list.
- `sorted`, `sort`, `map`, `filter`, `enumerate` and `zip` iterate their lists (§5.2), so a function argument that changes a list panics with `list mutated during iteration`. Since M31 the lazy ones do so when they're iterated.

**Methods**
- `list[T]`: `append(x: T)`, `pop() -> T` (removes and returns the last element; `IndexError` if empty), `insert(i: int, x: T)` (Python semantics: a negative `i` counts from the end, and out-of-range indices clamp), `contains(x: T) -> bool`, `sort(key=…, reverse=False)` (M11: sorts in place, with the same rules as `sorted`)
- `dict[K, V]`: `get(k: K) -> V?` (M6), and since M35 `get(k: K, default: V) -> V`, `keys() -> list[K]`, `values() -> list[V]`, `items() -> list[tuple[K, V]]` (M12) (new lists, in insertion order), `remove(k: K) -> bool` (`True` if the key was present)
- `str`: `upper() -> str`, `lower() -> str` (invariant-culture case mapping), `strip() -> str` (removes leading and trailing whitespace), `split(sep: str) -> list[str]` (an empty `sep` panics), `startswith(p: str) -> bool`, `endswith(p: str) -> bool`, `replace(old: str, new: str) -> str` (every occurrence; an empty `old` panics)
- **Since M35**, with Python's names and behavior:
  - `list[T]`: `extend(xs: Iterable[T])`, `remove(x: T)` (the first equal value; `ValueError`, `list.remove(x): x not in list`, if none), `index(x: T) -> int` (`ValueError` if absent), `count(x: T) -> int`, `reverse()`, `clear()`, `copy() -> list[T]`, `pop(i: int) -> T` (a negative `i` counts from the end; `IndexError` out of range).
  - `dict[K, V]`: `pop(k: K) -> V` (`KeyError` if absent), `pop(k: K, default: V) -> V`, `setdefault(k: K, default: V) -> V`, `update(other: dict[K, V])`, `clear()`, `copy() -> dict[K, V]`.
  - `str`: `join(xs: Iterable[str])`, `find(sub) -> int` and `rfind(sub) -> int` (`-1` if absent), `index(sub) -> int` (`ValueError` if absent), `count(sub) -> int`, `split()` and `split(None, maxsplit)` on runs of whitespace, `split(sep, maxsplit)`, `rsplit(sep, maxsplit)`, `splitlines()`, `strip(chars)`, `lstrip()`, `lstrip(chars)`, `rstrip()`, `rstrip(chars)`, `partition(sep)` and `rpartition(sep) -> tuple[str, str, str]`, `removeprefix(p)`, `removesuffix(p)`, `title()`, `capitalize()`, `swapcase()`, `zfill(width)`, `ljust(width, fill=" ")`, `rjust(width, fill=" ")`, `center(width, fill=" ")`, and the tests `isdigit()`, `isalpha()`, `isalnum()`, `isspace()`, `isupper()`, `islower()`. Indices and widths count code points, as `len` does, and the tests and case mappings use Unicode's categories, invariant of culture, as Python's do. `maxsplit` may be given by keyword.
- Equality inside `contains`, `in`, `==` and `!=` is structural, as for `==`. Dict and set equality ignore order.

**Display form** (used by `print`, `str()`, and f-strings)

| Type | Format |
|---|---|
| `int` | decimal |
| `float` | as Python's `repr`: the shortest digits that round-trip. Fixed notation when the decimal exponent is in [-4, 16), with `.0` appended when integral (`1.0`, `0.0001`, `1000000000000000.0`, `-0.0`). Otherwise scientific notation, with a lowercase `e`, a sign, and at least two exponent digits (`1e+16`, `1e-05`, `1.5e-07`). Also `inf`, `-inf`, `nan` |
| `bool` | `True` / `False` |
| `None` | `None` |
| `str` | as-is at top level. Inside a list, dict or dataclass, written as a literal: in `"` quotes, with `\\`, `\"`, `\n` and `\t` escaped |
| list | `[1, 2, 3]` |
| tuple | `(1, "a")` (M12) |
| dict | `{"a": 1}` |
| set | `{1, 2}`, or `set()` when empty (M35) |
| dataclass | `Service(name="api", replicas=3, healthy=3)`, by runtime class, with inherited fields first |
| plain class | `<Dog object>`, by runtime class (M15) |
| class with `__str__` or `__repr__` | as §4.8 says (M28): `__str__` at top level, `__repr__` inside a container |
| enum | `Ok`, `Degraded(healthy=1, total=4)` |
| function | `<function name>`, `<function <lambda>>` (M11) |
| exception | its message, as Python's `str(e)` (M16). Inside a container, its class and message: `[KeyError("missing key \"a\"")]` |
| `TextIO` (M37) | Python's: `<_io.TextIOWrapper name='data.txt' mode='r' encoding='utf-8'>` |
| `type[C]` (M37) | `<class 'ValueError'>`, `<class '__main__.Timer'>` (§3.17) |
| `TracebackType` (M37) | `<traceback object>` |

**F-string holes (M26)** take the parts §1 lists, applied in this order:
- **`=`** writes the expression's text exactly as written, spaces included, then its value: `f"{x = }"` is `x = 5`. The value is written as `!r` would write it, unless the hole has a conversion or a format spec of its own, as in Python.
- **`!s`** writes the display form, as a hole does without one. **`!r`** writes the form the value has inside a list (above), so `f"{name!r}"` is `"api"`. Unlike Python, the quotes are `"`, as Taipan always writes a string literal. After a conversion, the value is a `str`, so a format spec applies to it as to a `str`.
- **A format spec** is Python's format mini-language, `[[fill]align][sign]["z"]["#"]["0"][width][grouping]["." precision][type]`, for `int`, `float` and `str` values, giving the same text as Python's `format(value, spec)`:
  - *align* is `<`, `>`, `^` or `=`, after an optional *fill* character; *sign* is `+`, `-` or a space; `z` turns `-0.0` into `0.0`; `#` adds `0b`, `0o` or `0x`, or keeps a float's decimal point; `0` pads with zeros after the sign; *grouping* is `,` or `_`.
  - For an `int`, *type* is `d` (the default), `b`, `o`, `x`, `X` or `c` (the character with that code point), or one of the float types, which format it as a `float`.
  - For a `float`, *type* is `e`, `E`, `f`, `F`, `g`, `G` or `%`, or none, which is like `g` but always shows at least one digit after the decimal point, as Python's `repr` does (`f"{2.0:>6}"` is `   2.0`). The default precision is 6 for `e`, `f`, `g` and `%`.
  - For a `str`, *type* is `s` or none. A precision truncates (`f"{name:.3}"`), and `=` alignment, a sign, `z`, `#` and grouping don't apply.
- **Checked at compile time:** a spec is checked against the hole's type, so a mistake is an error at the spec rather than Python's `ValueError` when the line runs:
  - a spec that isn't in the mini-language, or one with an `n` type (locale-dependent), is `TP0006`, `invalid format spec '<spec>'`;
  - a spec whose parts don't apply to the type (`f"{name:.2f}"`, `f"{n:.3d}"`, `f"{name:+}"`) is `TP0200`, `format spec '<spec>' isn't valid for <T>`, with help naming the part, as Python's message does (`'f' formats a number, not str`, `precision isn't allowed for an int formatted with 'd'`, `a sign isn't allowed for str`);
  - a spec on any other type (a `bool`, an optional, a list, a class) is `TP0200`, `format specs apply to int, float and str, found <T>`, with help `convert it with str() first, or narrow the optional` as fits. A `bool` is an `int` in Python, so `f"{True:>5}"` there is `    1`, which is rarely what's meant.

### 6.1 Programs and I/O (M37)
```python
import sys
from os import environ

if len(sys.argv) != 3:
    sys.exit(f"usage: {sys.argv[0]} <source> <target>")
with open(sys.argv[1]) as src, open(sys.argv[2], "w") as dst:
    for line in src:
        dst.write(line.upper())
print("done", file=sys.stderr)
```

**`sys`** (a built-in module, §4.7) has these names, and no others:
- `argv: list[str]`, the program's path followed by its arguments, as in Python. The path is the main file's as given to `taipan run`, whose arguments follow `--` (§12.1), and for a built program its `.dll` or executable. In a library or extension module (§14, §17) it's the host process's arguments. It's an ordinary list, so the program may change it.
- `exit()`, `exit(code: int)` and `exit(message: str)` raise `SystemExit` (§3.10), with `code` `0`, `code`, or `1` and the message. It never returns, so it terminates (§4.5).
- `stdin: TextIO`, `stdout: TextIO` and `stderr: TextIO`, the standard streams, named `<stdin>`, `<stdout>` and `<stderr>`. `print` and `input` use `stdout` and `stdin`. They're UTF-8, and `stdin` reads with universal newlines (below) on every platform, as Python does on Windows (on Unix, Python leaves a `\r` in place). Closing one makes later uses raise `ValueError`, as in Python.

**`os`** (a built-in module) has these names, and no others:
- `environ: dict[str, str]`, the environment variables when the program started, in the order the system gives them. It's an ordinary dict: changes to it are seen by `getenv`, but not by .NET code or Python modules, which read the process's environment.
- `getenv(name: str) -> str?` and `getenv(name: str, default: str) -> str` read `environ`, as in Python.
- `os.path`, directories and the other functions of Python's `os` are deferred.

**`types`** (a built-in module) has one name, `TracebackType` (§3.17).

**Opening files.** `open(file, mode="r", *, encoding="utf-8", newline=None)` opens a text file, as Python's does:
- `mode` is `r` (read), `w` (write, emptying the file or creating it), `a` (append, creating the file) or `x` (write a new file, `FileExistsError` if it exists), with an optional `t`, which changes nothing. It must be a string literal, checked at compile time: any other mode is `TP0200`, `invalid mode '<m>'`, with help `binary files ('b') are deferred` or `read-write modes ('+') are deferred` for those, and a mode that isn't a literal is `TP0200`, `'open' needs a literal mode`.
- `encoding` is one of `utf-8`, `utf-8-sig` (which skips a byte order mark when reading, and writes one first), `ascii` and `latin-1`, with Python's other spellings of the same codecs (`utf8`, `UTF-8`, `latin1`, `iso-8859-1`, `us-ascii`, …). It must be a string literal, and anything else is `TP0200`, `unknown encoding '<e>'`. Unlike Python, whose default is the locale's encoding before 3.15, the default is always `utf-8`. A `utf-8` file's byte order mark is read as the character U+FEFF, as in Python.
- `newline` is `None` or `""`, as a literal. With `None`, reading ends lines at `\n`, `\r\n` and `\r` and gives each as `\n`, and writing turns each `\n` into the platform's line ending (`\r\n` on Windows). With `""`, reading ends lines at the same three but leaves them as they are, and writing writes `\n` unchanged. Python's other values (`"\n"`, `"\r"`, `"\r\n"`) are deferred (`TP0200`).
- A failure raises the `OSError` the system's error matches (§3.10): `FileNotFoundError` (`[Errno 2] No such file or directory: '<file>'`), `FileExistsError` (errno 17, `File exists`), `PermissionError` (errno 13, `Permission denied`) and `IsADirectoryError` (errno 21, `Is a directory`), as on Linux, whatever the platform. Any other failure is an `OSError` whose `errno` and `strerror` are `None` and whose message is .NET's.

**`TextIO`**, an open file, has Python's methods:
- `read() -> str` reads to the end, and `read(n: int) -> str` at most `n` characters (code points). Both give `""` at the end.
- `readline() -> str` reads one line, with its line ending, or `""` at the end. `readlines() -> list[str]` reads the rest as lines.
- A `TextIO` is an `Iterator[str]` (§3.13) over its remaining lines, each with its line ending, so `for line in f`, `list(f)` and `next(f)` work, and a line read by `readline` isn't seen again.
- `write(s: str) -> int` writes `s` and gives its length in code points. `writelines(lines: Iterable[str])` writes each, adding nothing between them.
- `flush()` writes what's buffered, and `close()` flushes and closes the file. Closing a closed file does nothing.
- Its attributes are `name: str`, the path as given to `open`, `mode: str`, as given, `encoding: str`, as given, and `closed: bool`.
- Reading a file opened for writing, or writing one opened for reading, raises `OSError`, `not readable` or `not writable` (Python's `io.UnsupportedOperation` is both an `OSError` and a `ValueError`). Using a closed file raises `ValueError`, `I/O operation on closed file.`.
- Bytes that aren't valid in the file's encoding raise `UnicodeDecodeError`, `'<encoding>' codec can't decode byte 0x<hh>`, and a character the encoding can't hold raises `UnicodeEncodeError`, `'<encoding>' codec can't encode character '<c>'`, with `<c>` written as Python writes it (`\xe9`, `\u20ac`, `\U0001f40d`). Python's messages also give the position and a reason.
- It's a context manager (§3.17). Files left open are flushed and closed when the program ends, normally, through `SystemExit` or through a panic, as CPython does when it exits. The `errors=`, `buffering=` and other arguments of Python's `open`, binary files, `seek` and `tell` are deferred.

---

## 7. Runtime errors (panics)
- A panic prints to stderr and exits with code **101**. Output the program printed before the panic still appears. Since M41 a panic prints the exception's traceback, as §3.19 says, ending with `<class>: <message>`, such as `ZeroDivisionError: division by zero`. Before M41 it printed `panic: <message>\n  at <file>.tpn:<line>`, with `<file>` the source path as given on the command line (§9) and `<line>` the line of the statement that panicked, in the innermost Taipan function.
- A failure that isn't an exception (loading Python, below and §10.5) still prints `panic: <message>`, then `  at <file>:<line>` for the statement running, and exits with 101.
- The emitted C# carries `#line` directives, so .NET stack traces and debuggers map back to `.tpn` lines. Tracebacks come from the program's own line tracking (§8, §15.3), not from stack traces.
- From M16, every runtime error raises an exception (§3.10), which a `try` can catch. A panic is an exception that nothing catches. The class each one raises follows its message below.
- **Panic sources and messages:**
  - int overflow: `integer overflow`. This includes `-x` and `x // -1` for the most negative int. (`OverflowError`)
  - division by zero (`/`, `//`, `%`): `division by zero` (`ZeroDivisionError`)
  - `int(x)` of a float that is NaN, infinite, or outside the int range: `cannot convert <x> to int`, with `<x>` in display form (`ValueError` for NaN, `OverflowError` otherwise, as in Python)
  - index out of range: `index <i> out of range for length <n>` (`IndexError`)
  - missing dict key: `missing key <k>`, with `<k>` as written inside a container (strings quoted) (`KeyError`)
  - `pop()` on an empty list: `pop from empty list` (`IndexError`)
  - mutation during iteration: `list mutated during iteration` / `dict mutated during iteration`. Any change to the list or dict being iterated counts, including assigning to an existing element. (`RuntimeError`)
  - `split("")` / `replace("", …)`: `empty separator` (`ValueError`)
  - a slice step of 0: `slice step cannot be zero` (M12) (`ValueError`)
  - `**e` at a call with a key naming a parameter, or one the call already gives (M30, §4.2): `<f>() got multiple values for argument '<k>'` / `… for keyword argument '<k>'` (`TypeError`)
  - unpacking a list of the wrong length (M29, §3.8): `not enough values to unpack (expected <n>, got <m>)`, `not enough values to unpack (expected at least <n>, got <m>)` with a starred name, or `too many values to unpack (expected <n>)` (`ValueError`)
  - Python interop (§10.5): `cannot convert Python <type> to <T>` (`TypeError`), `Python <ExceptionType>: <message>` (`PythonError`), `cannot load Python: <reason>` (at startup, so never caught)
  - failed non-optional `as` (M10), and `as` to a class the value isn't an instance of: `cannot convert <runtime class> to <C>` (M15) (`TypeError`)
  - an uncaught `raise` (M16): as §3.10 says
  - `input()` at the end of its input (M37): `EOF when reading a line` (`EOFError`)
  - files (M37, §6.1): the system's refusals (`OSError` and the classes deriving from it), `not readable` / `not writable` (`OSError`), `I/O operation on closed file.` (`ValueError`), and encoding failures (`UnicodeDecodeError`, `UnicodeEncodeError`)
- An uncaught `SystemExit` (M37) isn't a panic: it ends the program with its code (§3.10).

---

## 8. Code generation (C#)

The output is one C# compilation unit per program, compiled in-process by Roslyn with these options:
- `CheckForOverflowUnderflow = true`
- nullable enabled
- warnings not treated as errors in generated code

| Taipan | Emitted C# |
|---|---|
| program | `public static class TaipanProgram { ...constants... static void TopLevel() { ...constant initializers, then top-level stmts... } ...functions... }`, plus `public static class TaipanEntry { static int Main(string[] args) => Taipan.Runtime.Panic.Run(TaipanProgram.TopLevel, args); }`. `Panic.Run` sets up `sys.argv` (M37), turns panics into the §7 output and the exit code, and an uncaught `SystemExit` into its code. A generated name that clashes with a user name gets a numeric suffix. |
| identifiers | always verbatim-escaped (`@name`) to avoid C# keyword collisions |
| `def f(a: int) -> str` | `public static string @f(long @a)` |
| default values | Each default of a top-level function or method is a static helper method next to it (`connect_port()`, or `scale_by_default()` in a class), called by each call that leaves the parameter out, so every call evaluates its own (M14). A method's helpers are virtual instance methods, overridden along with the method, so a call evaluates the defaults of the method that runs (M15). A nested `def`'s default is emitted inline at each such call. Arguments are passed positionally while each is its own parameter, then by name (`arg1`, `arg2`, … for a nested `def`'s delegate), which C# evaluates in source order. The C# methods have no optional parameters. |
| `@dataclass class S` | `public class S : Taipan.Runtime.IStruct` (or `: Base`), `sealed` when no class derives from it, with a mutable `{ get; set; }` property per own field, a positional constructor over every field that passes the inherited ones to `: base(…)`, methods as instance methods (`self` is `this`), and `ToString` overridden to the display form. Not a C# record: Taipan equality is deep (lists inside dataclasses compare by contents), so `==` on dataclasses, lists and dicts calls `Taipan.Runtime.Equality.Equal` rather than C# `Equals`, and it compares runtime classes first. |
| `class C` (plain, M15) | `public class C : Taipan.Runtime.IObject` (or `: Base`), `sealed` when no class derives from it, with a `{ get; set; }` property per own field and `ToString` giving `<C object>`. C# `Equals` and `GetHashCode` aren't overridden, so `==` and dict keys use reference identity. Construction is `C.@__new__(args)`, a static method that creates the object with a protected parameterless constructor, then calls `__init__`. Each class's `__init__` is an instance method `@__init__` that first evaluates its own fields' defaults (`new` when it hides a base's), so `super().__init__(…)` is `base.@__init__(…)` and can follow the subclass's own field assignments, which a C# constructor's `: base(…)` couldn't. A class without `__init__` gets one that sets its defaults and passes its parameters on to the base's. |
| methods (M15) | `public virtual` where a method is introduced and `public override` for `@override`, with C#'s covariant return types for a narrower class result. `super().m(…)` is `base.@m(…)`. |
| protocols (M17) | Nothing: a protocol has no C# form, and a protocol a class declares is left out of its base list. A bounded generic function is monomorphized (below), so in `largest_Version`, `x > b` is `b.@__lt__(x)`, and in `largest_int` it's `x > b`. |
| protocols as types (M40) | A protocol used as a type is a C# interface, `public interface @Drawable`, with a property per field and a method per method, monomorphized as classes are when it's generic (`Source_int`). Each class, or class instantiation, that satisfies it implements it, with explicit implementations that call the class's own members and convert their results. A protocol value is the object itself, so converting a class to it is C#'s implicit conversion, and converting a protocol value to another protocol is a cast, which can't fail since every class satisfying the first satisfies the second. |
| generic inheritance (M40) | Each instantiation derives from its base's instantiation: `IntBox : Box_int`, `Labeled_str : Box_str`. |
| function values (M40) | A generic function, built-in or constructor used as a value is a `Function<D>` over a lambda that calls it, cached in a static field per function and type, so each is one object. |
| `is` (M40) | `ReferenceEquals(a, b)`, or `Taipan.Runtime.Interop.Is(a, b)` for `py` values, which is Python's `is`. |
| narrowing II (M40) | A narrowed element is read as the element, with the narrowed read's cast (`((C)xs[0])`, `xs[0]!`). A `py` narrowed by `isinstance` is converted at each read, as `as T` converts. |
| `__lt__` (M17) | an ordinary method, `@__lt__`. `a < b` on classes is `a.@__lt__(b)`. `a > b` evaluates `a` and `b` into temporaries, then calls `b.@__lt__(a)`. Sorting a list of classes passes the runtime sort a comparison that calls `__lt__`. |
| special methods (M28) | Ordinary methods: `@__add__`, `@__str__`, …. Every class has virtual `@__str__` and `@__repr__`, with the default forms where it defines neither (`@__str__` calls `@__repr__`), and `ToString` calls `@__str__`. A dataclass overrides `@__repr__` (and its `==` hook) unless it defines its own. |
| `==`, ordering (M28) | Each `__eq__`, `__lt__`, `__le__`, `__gt__` and `__ge__` also overrides a runtime hook, `TryEq(object other, out bool result)` and so on, which tests the parameter's class with `is` and calls the method, returning `false` when it doesn't apply. `Taipan.Runtime.Equality.Equal` and `Ops.Compare` run §4.8's protocol over the hooks, using `Type.IsSubclassOf` for subclass priority. A hashable class overrides C# `Equals` and `GetHashCode` with `Equality.Equal` and `hash()`, so `OrderedDictionary` finds keys as §4.8 says. |
| arithmetic (M28) | `a + b` is `a.@__add__(b)` or `b.@__radd__(a)`. When subclass priority could apply (some class derived from `a`'s static class and `b`'s defines `__radd__`), the operands go into temporaries and `Ops.Prefer(a, b)` tests whether `b.@__radd__(a)` runs instead. |
| properties (M28) | C# properties, `public virtual R @p { get { … } set { … } }`, `override` for `@override`, `abstract` for `@abstractmethod`. |
| static and class methods (M28) | Each class with static or class methods, or a base with them, has a nested class object, `C.@__Class`, deriving from the base's, with a singleton `C.@__class` and the methods as virtual instance methods (`this` is `cls`). `C.m(…)` is `C.@__class.@m(…)`, and `x.m(…)` is `x.@__classobj.@m(…)`, a virtual property each class overrides. `cls(…)` calls a virtual `@__call__` that each class object overrides with its class's construction. |
| class attributes (M28) | `public static T @a { get; set; }` on the declaring class, initialized by its C# static initializer. |
| abstract classes (M28) | `public abstract class`, with `public abstract` methods. An abstract class has no `@__new__`, since nothing constructs it. |
| `Iterable[T]` / `Iterator[T]` (M31) | `IEnumerable<T>`, and the covariant interface `Taipan.Runtime.IIterator<T>`, an `IEnumerable<T>` over one stream: a second loop over it continues where the first stopped. A list, dict, str or class converts to `Iterable[T]` through a runtime wrapper that starts a fresh pass each time, and keeps the mutation check (§5.2). |
| comprehensions (M36) | A chain of lazy LINQ operators through `Taipan.Runtime.Comprehensions`: each `for` a `SelectMany` (the last a `Select` of the element), each `if` a `Where`, each over lambdas whose parameters are the loop variables. A list, set or dict comprehension collects it at once; a generator expression wraps it in an `Iterator<T>` (§3.13). |
| generators (M31, M42) | Before M42, a C# iterator method returning `IEnumerable<T>`, with `yield return` and `yield break`. C# iterators can't receive values, and can't `yield` inside a `try` with a `catch`, so since M42 the body is a C# `async` method, `async Task<R> body(Taipan.Runtime.GeneratorChannel<Y, S> channel)`, that the generator object drives as a coroutine: `yield e` is `await channel.Yield(e)`, whose awaiter stores `e` and gives control back to whatever resumed the body, and whose result is what the body is resumed with (the sent value, or an exception from `throw` or `close`, which its `GetResult` throws). C# allows `await` in `try`, `catch` and `finally`, so `yield` may be anywhere. The body's `return r` completes its task with `r`. Nothing else is ever awaited, so the body runs only inside `send`, `next`, `throw` and `close`, on their thread. The generator function returns `new Taipan.Runtime.Generator<Y, S, R>(body, "<f>")`, an `IIterator<Y>`, with `S`'s name as a third argument when `S` doesn't accept `None`, for `next`'s `TypeError`. `yield from e` is a loop in the runtime that delegates to `e`. |
| async functions (M33, M42) | A C# `async` method returning `Task<T>` (`Task` for `Awaitable[None]`). `Awaitable[T]` is `Task<T>`. A main file with a top-level `await` gets an `async Task Main`. Since M42 the method is `Taipan.Runtime.Async.Start(body)` over a local `async` function, which gives each call a cancellable task (§3.20), and `await e` is `await Taipan.Runtime.Async.Await(e)`, whose awaiter resumes on the event loop, and with `CancelledError` when the task is cancelled. |
| event loop (M42) | `Taipan.Runtime.EventLoop`, a single-threaded queue of continuations that `Async.Await` posts to, and that `Panic.Run` (for a top-level `await`) and `asyncio.run` run until their awaitable finishes. Timers post to it when they fire. |
| async generators (M42) | As generators, with an `async Task body(Taipan.Runtime.AsyncGeneratorChannel<Y, S> channel)` that also awaits real work; `Taipan.Runtime.AsyncGenerator<Y, S>` completes each `__anext__` when the body yields, running the awaiting code directly. |
| `async for`, `async with` (M42) | A `while` loop that awaits `__anext__()` until `StopAsyncIteration`, and `with`'s form (above) with `await` on `__aenter__` and `__aexit__`. |
| `object` (M42) | `object?`. `x as T` is `Taipan.Runtime.Ops.Cast<T>(x)`. |
| `map`, `filter`, `zip`, `enumerate` (M31) | Runtime iterators over `IEnumerable<T>`, which call their function or take from their sources only when asked for a value. |
| `isinstance(x, C)` / narrowed `x` | `x is C`. A narrowed read is `((C)x)`, or `x!` / `x.Value` for `x is not None` on a reference / value type. |
| `x as C` / `x as C?` | `Taipan.Runtime.Ops.Cast<C>(x)`, which panics as §7 says / `x as C` |
| exceptions (M16) | `Exception` is `Taipan.Runtime.Exception`, which derives from `System.Exception` and is emitted against like a plain class's base (`__new__`, `@__init__`, a `message` property); the other built-in exception classes derive from it. A user exception class is a plain class deriving from one of them. Runtime errors throw the matching class, and a Python exception becomes a `PythonError` where the `py` operation calls Python. Checked arithmetic keeps throwing C#'s `OverflowException`, which a `catch` turns into an `OverflowError`. `raise e` is `throw e` (since M41, `throw Taipan.Runtime.Exceptions.Raise(e)`, which sets its context). A bare `raise` is `throw Taipan.Runtime.Exceptions.Rethrow(caught)`, which before M41 rethrew with `ExceptionDispatchInfo` to keep the original stack trace and line, and since M41 marks the exception so that its frame isn't added again (§3.19). |
| `assert` (M44) | `if (!(c)) throw new Taipan.Runtime.AssertionError(m)`, with the message `Display.Format(m)`, or `""` without one. |
| tests (M44) | `taipan test` compiles a test file as a program whose entry runs its top level, then calls `Taipan.Runtime.Testing.Run` with each test's name and a delegate calling it (an async one's awaited on the event loop), which writes §20.3's report and gives the exit code. A test project's program is the same (§18.6). |
| `try` (M16) | A C# `try` with one `catch (System.Exception)` that converts the exception to its Taipan value (or rethrows one that isn't catchable), then tests the `except` clauses in order with `is` (or Python's `isinstance` for a Python class). If none matches, `throw;` rethrows the original. `else` runs after the `try`/`catch`, behind a flag set at the end of the `try` block. `finally` is C#'s `finally`. `Panic.Run` prints an uncaught exception as §3.10 says. |
| `with` (M37) | The manager and `__enter__`'s result go into temporaries, then a C# `try` whose `catch (System.Exception)` sets a flag, converts the exception to its Taipan value (rethrowing one that isn't catchable), and calls `__exit__` with its class, the exception and a `Taipan.Runtime.Traceback`, rethrowing with `throw;` unless it returns `true`. Its `finally` calls `__exit__(null, null, null)` unless the flag is set. A `TextIO` is a `Taipan.Runtime.TextFile`, whose methods `@__enter__` and `@__exit__` the statement calls as a class's. |
| `sys`, `os` (M37) | `Taipan.Runtime.Sys` and `Taipan.Runtime.Os`, static classes whose members the names compile to: `sys.argv` is `Sys.argv`. |
| classes III (M39) | Frozen fields are ordinary properties, which only the generated constructor assigns, since the type checker rejects any other assignment. `order=True` and `@total_ordering` generate ordinary `@__lt__` methods and hooks, as written ones are. `Self` is the class's own type in C#, with a cast at each call site whose receiver is a subclass. Container and call special methods are ordinary methods the operators and calls go to. A static or class method of a generic class is emitted in each instantiation; its class attributes go in one non-generic holder class. A static or class method as a value, or a `__call__` object as a function, is a `Function<D>` over a lambda that calls it. |
| class patterns | C# type patterns with property sub-patterns: `case Dog { @name: var n }`. The scrutinee is narrowed as above. |
| function conversion | Converting a function value to a different function type (§3.3) wraps the delegate in an adapter. The new `Function<D>` keeps the original's identity, which its `==` compares, and its display name. |
| `enum E` | `public abstract record E` with a private constructor; each variant is `public sealed record V(...) : E` nested inside |
| `T?` | `T?` |
| `list[T]` / `dict[K, V]` | `List<T>` / `OrderedDictionary<K, V>`. `for` over them goes through a runtime iterator that turns concurrent modification into the §7 panic. |
| `tuple[A, B]` | a C# value tuple `(A, B)` (M12), with elements read as `Item1`, `Item2`, …. Unpacking stores the value in a temporary first. `==` uses `Taipan.Runtime.Equality.Equal`, and ordering uses a runtime comparison that orders strings by ordinal. In generic instance names, a tuple type is written `tuple_` followed by its element types: `first_tuple_int_str`. |
| slices | `Taipan.Runtime.Ops.Slice(target, start, stop, step)`, with `null` for an omitted part (M12) |
| type aliases (M18) | Nothing: each use is its expanded type. |
| line tracking (M23, M41) | §15.3: each method that's a Python frame declares `int line` and `string file`, assigns `line` before each statement, and wraps its body in `try { … } catch (System.Exception e) when (Taipan.Runtime.Tracebacks.Leave(e, file, line, "<name>")) { throw; }`, whose filter adds the frame to the exception's traceback and returns false. Before M41 this was only for `--native`, and recorded only the innermost frame. A generator passes a function reading its body's `line` to its `Generator<T>`, which adds the frame the same way. |
| tracebacks (M41) | Each exception's `__traceback__` is a `Taipan.Runtime.Traceback`, an immutable list of entries that the filters and `except` clauses add to in front. Contexts are set from the exceptions being handled, a stack kept in an `AsyncLocal` by each `except` block, each `finally` block that runs because of an exception (its in-flight exception captured by a filter on the `try`), and each `__exit__` called with one, when an exception first reaches a filter or a `catch`. `Panic.Run` prints the chain. |
| `raise e from c` (M41) | `throw Taipan.Runtime.Exceptions.Raise(e, c)`, which sets the cause before raising. |
| `tuple[T, ...]` (M41) | `Taipan.Runtime.VarTuple<T>`, an immutable `IReadOnlyList<T>` with Taipan's equality, ordering, hashing and display. |
| dataclass exceptions (M41) | A dataclass whose base is an exception class, with the dataclass's constructor and members (§8 above), whose constructor also sets `message` and `args` from its arguments. |
| libraries (M22) | `--library` (§14): one public static class per module in the library's namespace, with its classes and enums nested inside, instead of `TaipanProgram`, and no `TaipanEntry`. Exported definitions are public and everything else internal. Each module has an internal initializer, called at the start of each exported member, that runs once (§14.3). |
| .NET types and members (M21) | Used directly, by their full names (`global::System.Text.StringBuilder`). Arguments and results convert as §13.3 says: `checked` casts for narrower integers, copies for arrays and enumerables, and a runtime null check on a result that's not annotated as nullable. |
| .NET interop III (M43) | A generic .NET type is its constructed C# type, `global::System.Collections.Generic.HashSet<long>`. An `out` parameter is a C# `out` variable declared in the call, and a `ref` one a box the argument is put in, so a call is an expression giving the tuple (`(Int32.TryParse(s, out int v), (long)v)`). An expanded `params` argument is an array, an extension method a static call (`Enumerable.Where<long>(xs, …)`), and explicit type arguments C#'s. A class with .NET bases lists them as C# bases; it implements each interface method explicitly, and overrides each .NET method with a C# `override`, each converting its arguments and result and calling the class's own method, which is emitted under a C# name of its own when the .NET method's would clash with it. A .NET exception is raised with C#'s `throw`, a type test is C#'s `is`, and an event subscription C#'s `+=` and `-=` with the delegate the runtime keeps for the function value. Each async frame's `CancellationTokenSource` is made when its token is first passed, and cancelled when the frame is. |
| platforms (M45) | `--runtime` is `dotnet publish -r <rid>`, with the target's `<triple>-gcc` and `<triple>-objcopy` as NativeAOT's linker and object copier when Linux cross-compiles; the size options are `InvariantGlobalization` and `OptimizationPreference=Size`. A native library's exports are `[UnmanagedCallersOnly(EntryPoint = "<name>_<f>")]` methods that convert C values, call the function, and catch what it raises into a thread-static last error. An extension module's classes and enums are heap types made with `PyType_FromSpec` (the stable ABI), whose instances hold a `GCHandle` to the Taipan object; their slots call the Taipan members through the same conversions as functions. A `py` value in an extension is a `Taipan.Runtime.PythonHandle` owning a reference, and a Python callable passed as a function is a `Function<D>` over a delegate that calls it. |
| generics | Monomorphized. Each distinct instantiation the program uses becomes its own C# method or type, named after its type arguments: `first_int`, `Box_str`, `Box_list_int`, `Maybe_int_opt` (for `Maybe[int?]`). A clash with another name gets a numeric suffix. Generic definitions that are never instantiated are type-checked but not emitted. |
| function types | `Taipan.Runtime.Function<D>` (M11), with `D` a `Func<…>` or `Action<…>`. It wraps the delegate and the display name, and calls go through the delegate. A top-level function used as a value is one cached object per function, and each evaluation of a lambda or nested `def` creates a new object, so `==` is reference equality. In generic instance names, a function type is written `fn_` followed by its parameter and result types: `apply_fn_int_str`, `Box_fn_none`. |
| lambdas, nested `def` | C# lambdas, which capture locals by reference. A `for x in range(…)` loop copies its variable into a fresh local each iteration, so captures see that iteration's value. |
| `const` | a `static` field, assigned at the start of `TopLevel` in source order |
| modules | All files go into one C# compilation unit, with no per-module classes. A name defined in two modules gets a numeric suffix in the C# output (`area`, `area2`). |
| locals | a typed C# local. A Taipan local whose name C# scoping would reject (the same name declared in a nested block and again later in the outer block) is renamed with a numeric suffix. |
| `for x in range(a, b)` | a C# `for` loop. `a` and `b` are evaluated once, before the loop. |
| `x is some v` | `x is {} v`. C# scopes pattern variables more widely than Taipan, so a binding whose name is used anywhere else in the method gets a numeric suffix. |
| `x is None` / `None` | `x is null` / `null` |
| `x ?? d` | `x ?? d` |
| `match` expressions (M19) | A C# `switch` expression with the statement's patterns, each arm `pattern => value`. A raising arm is a `throw` expression. No fallback arm is emitted: the Taipan checker guarantees an arm matches, and C# rejects a fallback it can prove unreachable (after `True` and `False`, say). Before M46 a final `_ => throw new UnreachableException()` arm was emitted without a `_` arm. |
| patterns (M38) | `None` is `null`, an or-pattern without bindings C#'s `or`, an `as` pattern `p and var x` (cast in a guard when it narrows), and float literals C# constants. A guard is the case's `when`. A mapping pattern is a type pattern whose `when` tests each key with `TryGetValue` and its value with `is`, and `**rest` and a tuple's `*rest` are built in the `when` too. C# can't declare variables under `or`, so an arm whose or-pattern binds names, or needs a `when` inside an alternative, is emitted as one case per combination of alternatives, each with the arm's body. |
| `match` | `switch` statement with type, positional and constant patterns; each arm's body in its own braces. When there's no `_` arm, an emitted `default:` throws `UnreachableException` (exhaustiveness is guaranteed by the Taipan checker). A Taipan `break` inside an arm becomes `goto` to a label after the loop, since a C# `break` would leave the `switch`. Variants implement `Taipan.Runtime.IStruct`, and `==` on enums uses `Taipan.Runtime.Equality.Equal`, as for dataclasses. |
| `/` `//` `%` | `Taipan.Runtime.Ops.Div / FloorDiv / Mod` |
| f-string | C# interpolated string; every hole wrapped in `Taipan.Runtime.Display.Format(...)` |
| `print` | `Taipan.Runtime.Builtins.Print(params object?[])` |

**`Taipan.Runtime`** is a small library referenced by every compiled program. It holds the panic infrastructure, operators, display formatting, and built-ins.

---

## 9. Diagnostics

**Format:**
```
<file>:<line>:<col>: error TP<code>: <message>
  help: <optional hint>
```
- Diagnostics are written to stderr.
- Lines and columns are 1-based. Columns count Unicode code points.
- Each compiler stage runs only if the stages before it reported no errors: lexing (§1), then parsing (§2), then type checking. This keeps one mistake from cascading into spurious errors in later stages.
- `<file>` is the source path exactly as given on the command line.

- **Warnings** (`TP0300`, `TP0301`, `TP0302` and `TP0303`) print as `<file>:<line>:<col>: warning TP<code>: <message>`. They don't stop compilation or change the exit code. They're printed with the type checker's errors, before the program runs.

**Exit codes:**
- `0` success
- `1` compile error
- `2` usage or I/O error (bad arguments, file not found)
- `101` runtime panic

| Code | Meaning |
|---|---|
| TP0001 | tab in indentation |
| TP0002 | dedent doesn't match any outer indentation level |
| TP0003 | unterminated string |
| TP0004 | invalid character |
| TP0005 | invalid escape sequence |
| TP0006 | invalid f-string |
| TP0007 | invalid number literal |
| TP0100 | syntax error: expected X, found Y (including `'yield' needs a value`, M31, until M42 allowed a bare `yield`) |
| TP0200 | type mismatch |
| TP0201 | undefined name |
| TP0202 | cannot assign to `let` binding |
| TP0203 | optional used without narrowing (expected T, got T?) |
| TP0204 | non-exhaustive match (missing: …) |
| TP0205 | condition must be `bool` |
| TP0206 | cannot infer type of empty collection |
| TP0207 | shadowing an outer binding |
| TP0208 | not all paths return a value |
| TP0209 | wrong number of arguments |
| TP0210 | unknown field or method |
| TP0211 | function references a top-level variable |
| TP0212 | chained comparison not supported |
| TP0213 | nested optional |
| TP0214 | duplicate definition |
| TP0215 | `break`/`continue` outside loop, `return` outside function, a bare `raise` outside `except`, `return`/`break`/`continue` in `finally`, or a misplaced `yield` or `await` |
| TP0216 | ambiguous enum variant; qualify it |
| TP0217 | `const` initializer is not a constant expression |
| TP0218 | keyword argument not accepted |
| TP0219 | generic instantiation too deep (polymorphic recursion) |
| TP0220 | invalid type parameter constraint or bound |
| TP0221 | module not found |
| TP0222 | top-level statement in an imported module, or in a library's main file (M22) or an extension module (M25) |
| TP0223 | nested function with type parameters |
| TP0224 | invalid decorator |
| TP0225 | invalid class definition |
| TP0226 | object used before it is fully initialized |
| TP0227 | missing or unneeded `@override` |
| TP0228 | incompatible override |
| TP0229 | protocol used as a type |
| TP0230 | recursive type alias |
| TP0231 | .NET member or type that Taipan can't use |
| TP0232 | ambiguous .NET overload |
| TP0233 | can't be used in a native program (M23) |
| TP0234 | abstract class instantiated (M28) |
| TP0235 | awaitable never awaited (M33) |
| TPN001 | an SDK project's main file is missing (M34, §18.5; reported by the build, not the compiler) |
| TPN002 | an SDK project's `taipan.json` lists references or packages (M34, §18.5) |
| TP0300 | warning: unreachable code (a statement, `match` arm or `except` clause) |
| TP0301 | warning: broad `except` clause discards the exception |
| TP0302 | warning: a library's or extension module's definition can't be exported (M22, M25) |
| TP0303 | warning: a `test_` function that can't be run as a test (M44, §20; reported only by `taipan test`) |

New codes go into this table before they're implemented.

### 9.1 Type-checking messages
Each diagnostic points at the expression, name or keyword at fault. TP0208 points at the function's name, and TP0209 at the call's callee.

| Code | Message |
|---|---|
| TP0200 | `expected <T>, found <S>` (annotations, arguments, return values, assignments) |
| TP0200 | `operator '<op>' cannot be applied to <S> and <T>` / `operator '<op>' cannot be applied to <S>` |
| TP0200 | `cannot compare <S> and <T>` |
| TP0200 | `'<f>' does not return a value` |
| TP0200 | `'<f>' has no return type, so 'return' cannot have a value` / `missing return value; '<f>' returns <T>` |
| TP0200 | `'<x>' is not a function` / `cannot call <T>` (a callee that isn't a name) |
| TP0200 | `'<f>' is generic, so it cannot be used as a value` / `built-in '<f>' cannot be used as a value`, with help `wrap it in a lambda` (M11) |
| TP0200 | `'<f>' has '*args' or '**kwargs', so it cannot be used as a value`, with help `wrap it in a lambda` / `cannot unpack <T> into arguments` (M30) |
| TP0200 | `'<f>' yields, so it must return 'Iterator[T]'` / `a generator can't return a value` / `cannot index <T>` / `cannot slice <T>` / `'len' takes a list, dict or str, found <T>`, each with help `convert it with 'list(…)' first` for an iterator (M31) |
| TP0200 | `expected <T>, found lambda` / `cannot sort <T>`, with help `pass a key, e.g. 'key=lambda x: ...'` / `cannot sort by <K>` (M11) |
| TP0200 | `tuple index must be an integer literal` / `index <i> out of range for <T>` (a tuple) / `cannot unpack <T>` / `cannot slice <T>` (M12) |
| TP0200 | `integer literal is out of range` |
| TP0200 | `'range' can only be used in a for loop` / `cannot iterate over <T>` |
| TP0200 | `'<T>' is inferred as both <A> and <B>` / `'<T>' must be <A> or <B>, found <C>` (type-argument inference and constraints; `A, B or C` for longer tuples) |
| TP0200 | `'<m>' is a module, not a value` |
| TP0200 | `'as' converts py values; found <T>` / `cannot convert to <T>` / `cannot pass <T> to Python` (§10) |
| TP0200 | `cannot match on <T>` / `'<E>' is a type, not a value` / `'<v>' is not a function` (a variant without a payload) |
| TP0200 | `'is some' needs an optional, found <T>` / `'is None' needs an optional, found <T>` / `'is not None' needs an optional, found <T>` / `'??' needs an optional on the left, found <T>` |
| TP0200 | `'<m>' is a method; call it` / `'<f>' is a field, not a method` / `cannot index <T>` / `dict keys must be int, str, bool, tuples of those, or plain class instances, found <T>` (for a dataclass, with help `a dataclass compares by value, so it cannot be a key`; since M28 a class that isn't hashable gets `dict keys must be hashable, found <C>` instead, §4.8) |
| TP0200 | `isinstance needs a class, found <T>` / `'<C>' is not a class` / `'<C>' is generic` / `<T> can never be a <C>` (`isinstance`, `as` and class patterns on unrelated classes) / `'<C>' is a class; write '<C>()'` (a class pattern without parentheses) (M15) |
| TP0200 | `no overload of '<T>.<m>' takes (<A1>, <A2>)` / `'<T>' is a static class` (constructing it, or using it as a type) / `'<T>' cannot be constructed` (an interface, abstract class or enum) / `<v> is out of range for <N>` (a literal argument, `<N>` the .NET type's name, such as `Int32`) / `'<T>.<m>' returned null` (`TypeError`, at run time) / `'<C>' is not an exception class` (a .NET class not deriving from `System.Exception`) / `cannot bind both Taipan and .NET exceptions`, with help `write separate clauses` (M21) |
| TP0200 | `'<n>' is a .NET namespace, not a value` / `a .NET exception can't have a cause`, with help `pass it to the constructor as the inner exception` (for the cause) / `'<E>' is an event; use '+=' or '-='` / `<S> can never be a <T>` and `'<T>' is generic` for .NET types (`isinstance`, `as` and class patterns) (M43) |
| TP0200 | `'<T>' does not satisfy '<P>': it has no field '<f>'` / `'<T>' does not satisfy '<P>': '<f>' must be '<f>: <F>'` / `'<T>' does not derive from '<C>'` / `<P> can never be a <C>` / `'<f>' cannot be used as a value`, with help `wrap it in a lambda` / `'is' compares identity, which <T> doesn't have`, with help `use '=='` / `isinstance on py takes int, float, str, bool, list, dict, tuple or a Python class, found <T>` (M40) |
| TP0200 | `'from' needs an exception or None, found <T>` (with help `write '<C>(…)'` for a class name) (M41) |
| TP0200 | `'yield' needs a value: '<f>' yields <Y>` / `a generator declared '<T>' can't return a value`, with help `declare it 'Generator[Y, S, R]'` / `'<f>' can end without returning a <R>` / `'yield from' over <T> gives no value` / `'<f>' is an async generator, so it must return 'AsyncIterator[T]'` / `an async generator can't return a value` / `cannot iterate over <T> with 'async for'` (help `use 'for'` for a plain iterable) / `<T> is not an async context manager` (help `use 'with'` for a plain context manager, `define '__aenter__' and '__aexit__'` otherwise), and with `with`, help `use 'async with'` / `object has no attribute '<a>'`, with help `convert it with 'as'` (M42) |
| TP0215 | `'yield from' can't be in an async generator` / `'async for' outside an async function` (and `'async with'`) (M42; `'yield' can't be inside a 'with'`, `'yield' in an async function` and `'yield' can't be inside a 'try' with 'except', or in 'finally'` are gone) |
| TP0200 | `expected Self, found <C>` / `a class attribute can't use '<T>'` / `'<m>' is generic, so it cannot be used as a value` for a generic class's static or class method without type arguments (M39) |
| TP0200 | `<T> can't be None` / `<T> cannot match a mapping pattern` / `mapping keys must be int, str or bool, found <K>` / `alternatives bind different names: '<x>' is not bound in every one` / `'<x>' is <A> in one alternative and <B> in another` (M38) |
| TP0200 | `every arm of this 'match' raises`, with help `use a 'match' statement` (M19) / `this arm gives no value`, with help `end it with an expression` / `<T> cannot match a tuple pattern` / `<T> cannot match a list pattern` / `'*<x>' would hold <A> and <B>` (a starred name in a tuple) (M29) |
| TP0200 | `set elements must be hashable, found <T>` / `sum() takes numbers, found <T>`, with help `use ''.join(…)` for `str` / `'any' takes bools, found <T>` (and `'all'`), with help `test each value, as in any(x > 0 for x in xs)` / `'min' needs values that can be ordered, found <T>` (and `'max'`), with help `pass a key, e.g. 'key=lambda x: ...'` / `an int can't be raised to a negative power`, with help `use a float base, as in 2.0 ** -1` (M35) |
| TP0200 | `<T> is not a context manager`, with help `define '__enter__' and '__exit__'` / `invalid mode '<m>'`, with help naming a deferred mode / `'open' needs a literal mode` (and `encoding`, `newline`) / `unknown encoding '<e>'` / `newline must be None or ''` / `'type[…]' needs a class, found <T>` (M37) |
| TP0200 | `format spec '<spec>' isn't valid for <T>`, with help naming the part that isn't / `format specs apply to int, float and str, found <T>` (M26) |
| TP0200 | `'<A>' is a type alias, not a value`, with help `use '<N>'` when it names a class or enum (M18) |
| TP0200 | `cannot raise <T>` (with help `write '<C>(…)'` for a class name) / `'<C>' is not an exception class` / `'PythonError' cannot be constructed` (and `'DotNetError'`, M21) (M16) |
| TP0200 | `'<T>' does not satisfy '<P>': it has no method '<m>'` / `'<T>' does not satisfy '<P>': '<m>' must be 'def <m>(self, …) -> <R>'` (the protocol's signature with `Self` replaced; `-> <R>` left out when it returns nothing), at a call's callee or a declared protocol, with ` (declared by '<B>')` after `'<P>'` for an inherited declaration / `'...' can only be the body of a protocol method` / `'<m>' cannot be called directly`, with help `use '<'` (M17) |
| TP0200 | `'super()' can only be used as 'super().<method>(…)'` / `'super()' can only be used in a method` / `'<C>' has no base class` / `'__init__' cannot be called directly` / `'__init__' cannot have a return type` (M15) |
| TP0200 | `'<m>' cannot be called directly`, with help `use '<form>'` (a special method, as M17's `__lt__`) / `dict keys must be hashable, found <C>`, with help as §4.8 says / `cannot hash <T>` / `'+' on <A> and <B> is ambiguous: '<C>.__add__' would run when it fits` / `'<p>' is a property; read it from an instance` / `'<m>' needs an instance of '<C>'` / `'cls' can only be called, or used to reach static and class methods and class attributes` / `'ClassVar' can only annotate a class attribute` / `'<B>.<m>' is abstract` (M28) |
| TP0201 | `undefined name '<x>'` (with help `use 'None'` for `none`) / `undefined type '<T>'` / `module '<m>' has no definition '<x>'` / `.NET namespace '<ns>' has no type '<x>'` (M21) |
| TP0202 | `cannot assign to '<x>': it is a <let binding\|parameter\|loop variable\|constant\|function\|method>`, with help `declare it with 'var'` for a `let` binding / `cannot assign to an element of str` / `cannot assign to an element of a tuple` / `cannot use '<op>' on a slice` / `cannot delete '<x>'`, with help `assign it a new value instead` / `cannot delete a field` / `cannot delete an element of <T>` (M26) / `cannot assign to '<p>': it is a read-only property` / `cannot assign to '<a>' through '<x>'`, with help `assign '<C>.<a>'` (M28) |
| TP0202 | `cannot assign to '<f>': '<C>' is frozen` (M39) |
| TP0203 | help `the change to a <T> on line <n> could have changed it; copy it to a 'let' first` (an element, M40) |
| TP0203 | `<T>? can only be matched by '_' or a binding`, with help `'None' patterns aren't supported yet; bind it and test it with 'is some'` (a pattern, M29) / `expected <T>, found <T>?` / `<T>? must be narrowed before use`, with help `narrow it with 'is some', or give a default with '??'`, or one saying why a name or path isn't narrowed there (§3.9, M27) |
| TP0204 | `non-exhaustive match (missing: <a>, <b>)`, each a variant, literal or, since M29, a pattern such as `Degraded(0, _)` or `[_, *_]` (§5.1) |
| TP0205 | `condition must be bool, found <T>` (with help `convert it with 'as bool'` for `py`) |
| TP0206 | `cannot infer the type of '<f>'`, with help `give it a type, e.g. 'let f: Callable[[…], …] = <f>'` (a generic function, built-in or constructor as a value, M40) |
| TP0206 | `cannot infer the type of an empty list` / `... empty dict` / `cannot infer the type of None` / `cannot infer '<T>' for '<f>'` / `cannot infer the type of lambda parameter '<x>'`, with help `add a type annotation` |
| TP0207 | `'<x>' shadows an outer binding` |
| TP0208 | `not all paths in '<f>' return a value` |
| TP0209 | `'<S>' takes <n> type arguments, found <m>` (`type argument` when n is 1) (since M43 also for generic .NET types, and `'<T>.<m>' takes <n> type arguments, found <k>` for a .NET method) |
| TP0209 | `'<v>' has <n> fields, found <m> bindings` (variant and dataclass patterns; `field` / `binding` when the count is 1) / `'<C>' is not a dataclass, so its fields can only be bound by name` / `field '<f>' is given more than once` (class patterns, M15) |
| TP0209 | `<T> has <n> elements, found <m> names` (unpacking) / `a tuple needs at least two elements` (M12) / `<T> has <n> elements, found <m> patterns` (a tuple pattern, M29) / `<T> has <n> elements, found at least <m> patterns` (a tuple pattern with `*`, M38) |
| TP0209 | `'<f>' takes <n> arguments, found <m>` (`argument` when n is 1; `1 or 2 arguments` for `range`; `<a> to <b> arguments` for a function with defaults, M14; `2 or more arguments` for `zip`, M26; `function takes …` when the callee isn't a name) / `missing argument '<x>'` (M14) / `missing field '<f>' in '<S>'` / `field '<f>' is given more than once` / `argument '<k>' is given more than once` |
| TP0209 | `'<f>' takes <n> positional arguments, found <m>` / `missing keyword-only argument '<x>'` / `'*<e>' can only fill '*<args>'` / `'<f>' has no '*args' for '*<e>'` / `'<f>' has no '**kwargs' for '**<e>'` / `'**<e>' can't fill '<x>'` (M30) |
| TP0209 | `expected a lambda with <n> parameters, found <m>` (`parameter` when n is 1) / `'Callable' takes a parameter list and a result type` |
| TP0210 | `<T> has no field '<x>'` / `<T> has no method '<x>'` / `<E> has no variant '<x>'` |
| TP0211 | `'<f>' cannot use top-level variable '<x>'`, with help `pass it as a parameter, or make it a const` |
| TP0213 | `optional types cannot be nested` |
| TP0214 | `duplicate definition of '<x>'` |
| TP0214 | `key <k> appears twice in a mapping pattern` (M38) |
| TP0100 | `'**' must come last in a mapping pattern` / `'**_' can't be used; leave it out` / `a tuple pattern can have only one '*'` (M38) |
| TP0215 | `'yield' can't be inside a 'with'` (M37) / `'await' can't be inside a comprehension` (M36) / `'await' outside an async function` / `'yield' in an async function` (M33) / `'yield' outside a function` / `'yield' can't be inside a 'try' with 'except', or in 'finally'` (M31) / `'<k>' can't leave a 'match' expression`, with help `use a 'match' statement` (M29) / `'break' outside a loop` / `'continue' outside a loop` / `'return' outside a function` / `'raise' without an exception outside an except block` / `'return' cannot be used in a finally block` (and `'break'`, `'continue'`) (M16) |
| TP0216 | `'<v>' is ambiguous: it is a variant of <E1> and <E2>`, with help `qualify it, e.g. '<E1>.<v>'` |
| TP0217 | `const initializer must be a constant expression` / `class attribute value must be a constant expression` (M28) |
| TP0218 | `'<p>' is positional-only, so it can't be passed by keyword` (M30) / `'<f>' takes no keyword arguments` (a built-in) / `a function value takes no keyword arguments` / `'<f>' has no parameter '<k>'` (M14; also for `sorted`, `sort` and `enumerate`) |
| TP0219 | `instantiating '<f>' needs '<f>[<T>]', which is too deep`, at the call that starts the chain |
| TP0221 | `module '<m>' not found`, with help `looked for <path>` / `'python' is not a module`, with help `use 'from python import <module>'` / `'dotnet' is not a module`, with help `use 'from dotnet.<namespace> import <type>'` / `.NET namespace '<ns>' not found` (M21) / `'import dotnet.<ns>' needs a name`, with help `use 'import dotnet.<ns> as <name>'` (M43) |
| TP0222 | `only the main file can have top-level statements` / `a library can't have top-level statements` (M22) / `a Python extension can't have top-level statements` (M25) |
| TP0223 | `nested functions cannot have type parameters` |
| TP0224 | `unknown decorator '@<d>'` / `'@<d>' cannot be used here` (with help `'@dataclass' goes on a class, '@override' on a method`) / `duplicate decorator '@<d>'` / `'@<d>' cannot be used here`, with help `'@<d>' goes on a method`, for a method's decorator elsewhere / `'@<a>' cannot be combined with '@<b>'` / `'@abstractmethod' needs a class deriving from 'ABC'` / `'@<x>.setter' needs a property '<x>' just above it` (M28) |
| TP0225 | `a class can have only one base class` (protocols aside, M17) / `'<T>' cannot be a base class` / `a dataclass can only derive from a dataclass` (with help `exceptions are plain classes; give it an '__init__'` for an exception base, M16; since M41 only for a plain user exception class, `SystemExit`, `PythonError` or `DotNetError`) / `a plain class can only derive from a plain class` / `generic classes cannot be derived from` / `generic classes cannot have a base class` / `'<C>' derives from itself` / `special method '<m>' is not supported` / `a dataclass cannot define '__init__'` / `field without a default follows field with a default` / `'<m>' must be 'def <m>(self, …) -> <R>'` (M28, replacing M17's `'__lt__' must take one parameter of type '<C>' (or a base class) and return bool`) / `'<m>' must take 'self' first` (or `'cls'`) / `a static method takes neither 'self' nor 'cls'` / `a property takes no parameters other than 'self'` / `a property must have a return type` / `a setter must follow its property` / `a setter must take one parameter of type <T> and return nothing` / `class attribute '<a>' needs a value` (`generic classes cannot have <static methods\|class methods\|class attributes>` until M39) / `an abstract method's body must be '...'` / `'<C>' would not be hashable, but its base '<B>' is`, with help `define '__hash__'` (M28) / `'<C>' defines '__exit__' but not '__enter__'` (or the reverse) (M37) / `'<P>' is already declared` (or `already declared by '<B>'`) / `a protocol can only declare methods` / `a protocol method's body must be '...'` / `protocol method parameters cannot have defaults` / `protocols cannot be generic` / `a protocol can only derive from 'Protocol'` (M17) |
| TP0225 | `a protocol field can't have a default` / `an exception class can't be generic` (M40) |
| TP0225 | `a frozen dataclass can't derive from one that isn't` (or the reverse) / `'order=True' needs fields that can be ordered: '<f>' is <T>` / `'order=True' generates '<m>'; remove it` / `'__post_init__' must be 'def __post_init__(self) -> None'` / `'__post_init__' is only for dataclasses` / `cannot derive from final class '<C>'` / `'@total_ordering' needs '__lt__', '__le__', '__gt__' or '__ge__'` (M39) |
| TP0225 | `cannot derive from '<B>': it's <sealed\|static\|a struct\|an exception class\|without a constructor Taipan can call>` (with help `derive from 'Exception'` for an exception class) / `'<C>' must define '<m>' to implement '<I>'` / `'<C>' must override '<B>.<m>'` / `'<m>' is defined by '<special>'` / `'<C>' derives from the .NET class '<B>', so it can't be derived from yet` / `generic classes cannot implement .NET interfaces yet` (M43) |
| TP0226 | `'__init__' does not set '<f>'` / `'<f>' has no default, and '<C>' has no '__init__'` / `'<f>' is read before it is set` / `'self' is used before '<f>' is set` (or `before 'super().__init__' is called`) / `'<f>' is inherited, so it is set by 'super().__init__'` / `'super().__init__' is called before '<f>' is set` / `'<C>.__init__' must call 'super().__init__(…)'` / `'super().__init__' must be called once, directly in '__init__'` / `'super().__init__' can only be called in '__init__'` / `'__init__' returns before '<f>' is set` (or `before 'super().__init__' is called`) (when several fields are unset, the first in declaration order is named) / `'super().__init__' must come first, since '<B>' is a .NET class` (M43) |
| TP0227 | `'<m>' overrides '<B>.<m>'; mark it '@override'` / `'<m>' does not override a method of <a base class\|'<B>'>` / `'__init__' cannot be marked '@override'` |
| TP0228 | `'<m>' must take the same parameters as '<B>.<m>'` / `parameter '<p>' of '<m>' must have type <T>, as in '<B>.<m>'` / `parameter '<p>' of '<m>' needs a default, as in '<B>.<m>'` / `'<m>' must return <T>, as '<B>.<m>' does` (or `return nothing`) / `'<m>' is a <static method\|class method\|method\|property> in '<B>'` / `'<p>' must have a setter, as '<B>.<p>' does` / `'<D>.<m>' must return <R>, since it can run in place of '<B>.<op>'` / `'<D>' must be constructed as '<C>' is, since '<C>.<m>' calls 'cls(…)'` (M28) |
| TP0228 | `'<B>.<m>' is final` / `'<m>' must return Self, as '<B>.<m>' does` (M39) |
| TP0228 | `'<m>' must be 'def <m>(self, <params>) -> <R>', as in '<I>.<m>'` (implementing a .NET interface's method, or overriding a .NET class's) (M43) |
| TP0235 | `this awaitable is never awaited`, with help `write 'await …'` (M33) |
| TP0234 | `cannot instantiate abstract class '<C>'`, with help `it does not implement '<B>.<m>'` (`'<B>.<m>' and '<B>.<n>'` for more) / `'cls(…)' may construct the abstract class '<C>'` / `'<C>' cannot add abstract method '<m>', since '<B>.<cm>' calls 'cls(…)'` (M28) |
| TP0220 | `a bound can't use type parameters` (M40) |
| TP0220 | `a constraint needs at least two types` / `'<T>' is listed twice in the constraint` / `a constraint cannot be optional or generic` / `constraints are only supported on functions` / `'<T>' is not a protocol` (with help `class bounds are not supported`, for a class) / `bounds are only supported on functions` (M17) |
| TP0229 | `'<P>' is a protocol, so it can only be a type parameter bound`, with help `use a bounded type parameter, e.g. 'def f[T: <P>](x: T)'` (M17) |
| TP0229 | `'<P>' uses 'Self' in '<m>', so it can only be a type parameter bound`, with M17's help / `'<P>' is a protocol`, with help `construct a class that satisfies it` (a construction) or `test for a class that satisfies it` (`isinstance`, `as`, class patterns, `except`), replacing M17's message for these (M40) |
| TP0231 | `'<T>.<m>' uses <N>, which Taipan can't use yet` (`<N>` the .NET type, e.g. `Dictionary<string, int>` or `Func<int, bool>`) / `'<N>' can't be imported: generic .NET types aren't supported yet` (M21, until M43) / `'<N>[…]' is <T> in Taipan; write '<T>'` / `'<I>' has <the property '<p>'\|the event '<e>'\|an indexer\|the generic method '<m>'\|two methods named '<m>'>, which a Taipan class can't implement yet` (M43) |
| TP0232 | `ambiguous call to '<T>.<m>': it could be '<m>(<P1>)' or '<m>(<P2>)'` (M21) |
| TP0233 | `a native program can't use Python`, with help `build it without '--native'` / `'<T>.<m>' can't be used in a native program: <message>` (M23) / `a Python extension can't use Python interop itself` (M25) / `an extension module can only pass Python objects along`, with help `do this in Python, and pass the result` (M45) |
| TP0230 | `type alias '<A>' refers to itself` / `type alias '<A>' refers to itself through '<B>'` (`'<B>' and '<C>'` for longer cycles), with help `use a class for a recursive type` (M18) |
| TP0300 | `unreachable match arm` / `unreachable except clause`, with help `'<B>' is already caught above` / `unreachable code` (M16) |
| TP0301 | `this clause catches every exception and discards it`, with help `catch a specific class, re-raise with 'raise', or bind it with 'as e' and use it` (M16) |
| TP0303 | `'<f>' takes parameters, so it isn't run as a test` (M44) |
| TP0302 | `'<f>' is not exported: <reason>`, where `<reason>` is `its type parameter '<T>' has a bound` / `'<T>?' has no .NET generic form` / `it uses '<g>', which isn't a .NET generic` / `it uses '<C>', which isn't exported` / `it has its module's name` (M22) / `'<T>' can't be passed to Python` / `it's generic` (M25) / `it's used as the protocol '<P>'` (M40) / `'<T>' can't be passed to C` / `it's async` / `it has '*args' or '**kwargs'` / `it has keyword-only parameters` (a native library) (M45), with help `rename it '_<f>' to keep it private` |

Types are written as in source: `int`, `str?`, `list[int]`. The type of `None` is written `None`, and a missing value is written `nothing`.

---

## 10. Python interop (M10)

### 10.1 Importing Python modules
- `from python import json, os as system` binds each name, which has type `py`, to the Python module of that name (via Python's `import`). `python` is a reserved module name (§4.7). `import python` is `TP0221`, with help `use 'from python import <module>'`.
- Submodules are reached as attributes (`os.path`). Dotted names can't be imported directly in v0.1.
- Python modules are imported when the program starts, in the order their imports appear (main file last, as for constants, §4.7). This happens before constants are initialized. A failed import (for example, a missing package) is a panic at that import's line.
- A program with no `from python import` never loads Python.

### 10.2 The `py` type
- `py` holds any Python object. Each `py` operation is checked by Python at run time, not by Taipan:
  - attribute access and assignment: `x.name`, `x.name = v`
  - calls, positional and keyword (`TP0218` doesn't apply): `json.dumps(v, indent=2)`
  - indexing and element assignment: `x[k]`, `x[k] = v`
  - the operators `+ - * / // %`, unary `-`, `== != < <= > >=`, and `in`/`not in`, when either operand is `py`
  - `for v in x`, giving `v: py`
  - `len(x)`, which gives `int`
  - `print`, `str()` and f-string holes. A `None`, `bool`, `int`, `float`, `str`, `list`, `tuple` or `dict` displays by Taipan's rules (§6), so `None` displays as `None` and strings inside containers are quoted. A `tuple` is written `(1, 2)`. Any other object displays as Python's `str(x)`.
- **The result of every `py` operation is `py`, including comparisons.** A `py` used as a condition, or with `and`, `or` or `not`, is `TP0205`, with help `convert it with 'as bool'`.
- `py` may annotate variables, parameters, return types, fields and collection elements (`list[py]`). It can't be a dict key (§3.1). A list, dict or optional containing `py` can't be compared with `==` (`TP0200`), since Python objects have no Taipan equality.

### 10.3 Taipan → Python
- These Taipan types are *convertible*: `int`, `float`, `bool`, `str`, `None`, `list[T]` and `dict[K, V]` of convertible types, tuples of convertible types (M12, converted to a Python `tuple`), `T?` of a convertible `T`, and `py` itself.
- A convertible value is converted when it's passed into a `py` operation, and when it's assigned to something of type `py` (so `let x: py = [1, 2]` is valid).
- Lists and dicts are copied, so changes on either side aren't seen by the other.
- **Mixed literals:** in a `py` operation's arguments and operands, a list literal has element type `py` and a dict literal has value type `py`, so their elements may mix types: `json.dumps({"xs": [1, 2], "ok": True, "None": None})`.
- A class or enum value, or any other non-convertible type, passed to Python is `TP0200` (`cannot pass <T> to Python`).

### 10.4 Python → Taipan: `as`
- A `py` value reaches a static type only through `as` (cast, §2). Any other use where a static type is expected is `TP0200`. `as` applied to a non-`py` value is also `TP0200`, except between classes (M15, §4.3).
- `x as T` converts or panics. `x as T?` converts or gives `None`.
- **Targets and what each accepts:**
  - `int`: a Python `int` in the 64-bit range. A `bool` is not accepted, even though Python's `bool` is an `int`.
  - `float`: a Python `float`, or an `int` (not `bool`), converted.
  - `bool`: a Python `bool`.
  - `str`: a Python `str`.
  - `list[T]`: a Python `list` or `tuple` whose every element converts to `T`.
  - `dict[K, V]`: a Python `dict` whose every key converts to `K` and every value to `V`.
  - `tuple[A, B, …]` (M12): a Python `tuple` or `list` with exactly that many elements, each converting to its type.
  - `T?` inside another target (for example `list[int?]`): Python `None` becomes Taipan's `None`, and anything else converts as `T`. As the outer target, `x as int?` gives `None` both for `None` and on a mismatch.
  - `py`: always succeeds, so `x as py` is allowed but pointless.
  - Any other target (a class, an enum) is `TP0200`. Converting between classes with `as` is §4.3.
- Conversion is all-or-nothing: one failing element fails the whole conversion.

### 10.5 Errors at run time
- A failed `as T` raises a `TypeError` (M16) with the message `cannot convert Python <type> to <T>`, where `<type>` is the Python type's name (`str`, `NoneType`).
- A Python exception during any `py` operation raises a `PythonError` (M16, §3.10), with the Python class's name, the message (Python's `str(e)`) and the exception object. `except json.JSONDecodeError` catches one by its Python class. Uncaught, it prints a traceback ending in the Python class and message, as Python would print them (§3.19); before M41, `Python <ExceptionType>: <message>`.
- If Python can't be loaded, the first Python import panics with `cannot load Python: <reason>`. A program without Python imports starts Python at its first conversion to `py` (`let p: py = [1, 2]`), which panics the same way.
- All of these exit with 101, and those that are exceptions print a traceback, as other panics do (§7, §3.19).

### 10.6 Locating and running Python
- The runtime embeds CPython through Python.NET (pythonnet). It looks for the CPython shared library in this order:
  1. the `PYTHONNET_PYDLL` environment variable (pythonnet's own setting)
  2. the library reported by the first of `python3` or `python` found on `PATH` (`sysconfig`'s `LIBDIR` and `LDLIBRARY`)
- Python is initialized once per process and never shut down. The GIL is taken around each Python operation, so `taipan run` can run programs on different threads of one process.

---

## 11. Deferred (post-v0.1)
ROADMAP.md plans most of these from M35 on. They were never part of v0.1:

- built-ins beyond M35: `frozenset`, `|=` and the other in-place set operators, `str.format` and `%` formatting, `sorted`'s and `min`'s keys on mixed types, and passing sets to Python and .NET
- I/O beyond §6.1 (M37): binary files and `bytes`, read-write (`+`) modes, `seek` and `tell`, `open`'s `errors=` and `buffering=`, `newline` values other than `None` and `""`, other encodings, `os.path` and the rest of `os` and `sys`, `contextlib`, parenthesized `with` items and targets other than a name, `yield` inside `with`, and `NotADirectoryError` and Python's other `OSError` classes
- protocols extending other protocols, a check that a class used with a protocol declares it, properties in protocols, and runtime-checkable protocols (`isinstance(x, P)`)
- bounds on classes', protocols' and enums' type parameters, bounds that use type parameters, and built-in types' ordinary methods satisfying protocols
- special methods beyond §4.8 (`__bool__`, `__ne__`, `__delitem__`, the bitwise operators), and special methods in protocols other than `__lt__`
- mixing dataclasses and plain classes in one hierarchy, generic exception classes, and `isinstance` and class patterns with generic classes
- an `__init__` written in a dataclass
- abstract static and class methods, property deleters, instance methods as values (bound methods), `field()` and the other dataclass options (`eq`, `kw_only`, `slots`, …), and slices and `del` on classes (`__getitem__` with a slice, `__delitem__`)
- methods as values (bound methods; wrap them in a lambda)
- async beyond §3.20 (M42): async lambdas, async comprehensions, `asyncio` beyond `gather`, `sleep`, `wait_for`, `timeout` and `run` (tasks, `return_exceptions`, queues, locks), and running a generator's `finally` when it's collected
- patterns beyond §5.1 (M38): value patterns naming constants (`case Color.RED` for a non-enum value), class patterns on .NET types and `py` values, mapping keys that aren't literals, and narrowing a binding at a nested optional position by earlier arms
- recursive aliases, and a class's aliases reached from outside it (`C.Name`)
- typed `**kwargs` (`Unpack[TypedDict]`), `*args` and `**kwargs` in function types, and `**e` filling named parameters
- narrowing elements with keys that can change (`xs[i]` for a `var` `i`), and literal types
- exceptions beyond §3.19 (M41): `with_traceback`, notes (`add_note`), the `traceback` module, `tb_frame` and `tb_lasti`, Python's own frames in a traceback, `^` markers under the failing part of a line, and `match` on `tuple[T, ...]`
- nested format specs in f-strings (`{x:{width}}`), and format specs for other types (`__format__`)
- language server features beyond §16: code actions beyond §16.11's, range formatting and formatting as you type, incremental sync, workspace-wide diagnostics, semantic token deltas and ranges, signature help for built-in functions, references to built-in, Python and .NET names, updating imports when the editor renames a file, editors' own support beyond VS Code, Vim and Neovim (§16.12), a tree-sitter grammar, and showing `taipan test`'s tests in VS Code's Test Explorer
- formatting beyond §19: wrapping or joining long lines, and options for the style
- tests beyond §20: fixtures, parametrized tests, selecting tests by name, capturing their output, and running them in parallel
- CPython extension modules beyond §17 (M45): Python subclasses of Taipan classes, special methods beyond `__str__`, `__repr__`, `__eq__` and `__hash__`, static and class methods, operations on Python objects inside an extension beyond passing and displaying them, generic functions and classes, and builds for one exact Python version
- .NET interop beyond §13 (M43): C# 14 extension members other than methods, `~` and bitwise operators on `int`, protected members, implementing or overriding .NET properties, events and indexers, implementing generic interface methods, deriving from .NET exception classes or from a Taipan class with a .NET base, generic classes implementing .NET interfaces, .NET variance, `ref` returns and `ref`/`out` parameters of delegates, and packages with native assets
- build integration beyond §18: multi-targeting, test projects reporting to `dotnet test`'s own test results (§18.6 runs them as programs), native and Python-extension SDK projects, and symbols packages
- native builds beyond §15 (M45): Python in native programs, another operating system, musl-based Linux, and a native library's constants, classes, enums and list or dict arguments
- Taipan libraries beyond §14: XML documentation, value `Equals` and `GetHashCode` for dataclasses, deriving from Taipan classes in other .NET languages, and plain delegates for function types

**Not planned**, since each costs much more than it gives a statically typed language on .NET:
- multiple inheritance (protocols cover the common uses, §3.11)
- defaults on lambda parameters, since loop variables are captured per iteration (§3.7)
- exception groups (`except*`)
- the locale-dependent `n` format type
- free-threaded CPython for extension modules
- design-time builds for Visual Studio's project system
- PascalCase renaming of a library's names (§14.1 keeps Taipan's names)

---

## 12. The `taipan` tool (M20)

### 12.1 Commands
```
taipan check <file> [<kind>]                         type-check only, printing diagnostics
taipan emit  <file> [<kind>]                         print the generated C# (§8)
taipan build <file> [<kind>] -o <dir>                write a runnable program to <dir>, or with <kind>:
                                                     --library (M22, §14), --native (M23, §15),
                                                     or --python-extension (M25, §17); since M45 also
                                                     --library --native (§15.6), and with --native or
                                                     --python-extension, --runtime <rid> (§15.4),
                                                     --invariant-globalization and --optimize-size (§15.5)
taipan run   <file> [-- args]                        compile and run, passing args to the program (sys.argv, M37)
taipan pack  <file> -o <dir>                         pack a library as a NuGet package (M34, §18.4)
taipan compile <file> [--library | --test] --out <dll>   compile one assembly, for TaipanLang.Sdk (M34, §18.2; --test: M44, §18.6)
               [--reference <dll>]… [--assembly <name>] [--namespace <ns>] [--version <v>]
taipan lsp [--stdio]                                 run the language server on stdin and stdout (M24, §16)
taipan fmt [--check] <path>…                         format files, or check that they're formatted (M44, §19)
taipan test [<path>…]                                run test functions (M44, §20)
taipan --version                                     print "taipan <version>", e.g. "taipan 0.4.1"
taipan --help                                        print the usage text
```
- `--version` and `--help` print to stdout and exit `0`. Each takes no other arguments.
- No arguments, an unknown command or option, or wrong arguments to a command print a one-line error (if there is one) and the usage text to stderr, and exit `2` (§9).
- `build -o <dir>` writes `<name>.dll` with `Taipan.Runtime.dll`, `Python.Runtime.dll` and a `runtimeconfig.json`, so `dotnet <dir>/<name>.dll` runs it on the .NET 10 runtime.

### 12.2 Packages
Taipan is distributed as NuGet packages, all with the same version:

| Package | Contents |
|---|---|
| `TaipanLang` | the `taipan` command, as a .NET tool: the compiler, the runtime and its dependencies (Roslyn, pythonnet) |
| `TaipanLang.Runtime` | the `Taipan.Runtime` library that compiled programs reference, depending on `pythonnet` |
| `TaipanLang.Sdk` (M34) | the MSBuild project SDK for `.tpnproj` projects (§18), carrying its own copy of the compiler |

- `TaipanLang` installs with `dotnet tool install -g TaipanLang`, which puts `taipan` on the path. It runs on the .NET 10 runtime, and compiles programs against that runtime's assemblies. It needs no SDK.
- The NuGet ID `taipan` belongs to another project, hence `TaipanLang`. Assembly and namespace names don't change: the runtime is still `Taipan.Runtime.dll`, namespace `Taipan.Runtime`.
- `TaipanLang.Runtime` isn't needed to run programs, since the tool carries the runtime. It's published for Taipan libraries referenced from other .NET code (M22).
- **Version:** one version for every package and `taipan --version`, set in one place in the build. M20's was `0.2.0`, the first packaged release after v0.1; M34's was `0.3.0`, which added `TaipanLang.Sdk`; M44's was `0.4.0`, which added `taipan fmt`, `taipan test` and test projects; M47's is `0.4.1`, which publishes the VS Code extension at the same version.
- `dotnet pack -c Release -o <dir>` at the repository root builds every package.
- An installed tool behaves exactly as a source build, Python interop included: CPython is found as §10.6 says, at run time.
- **Publishing** to nuget.org is done by `.github/workflows/publish.yml` when a tag `v<version>` is pushed: it checks that the tag names the build's version, packs every package and pushes them with NuGet's trusted publishing, so no API key is stored. A version, once published, can't be published again. Before M46 publishing was done by hand. Since M47 the tag must also match the VS Code extension's version, and the same workflow publishes the extension (§16.8) once the packages are pushed.

---

## 13. .NET interop (M21)
Taipan programs can use .NET libraries: the .NET runtime's own, local assemblies, and NuGet packages. Unlike Python interop (§10), .NET APIs have static types, which Taipan reads from the assemblies' metadata, so a .NET call is checked at compile time like a Taipan call.

```python
from dotnet.System.Text import StringBuilder
from dotnet.System.IO import File, FileNotFoundException

def report(path: str) -> str:
    let sb = StringBuilder()
    try:
        for line in File.ReadAllLines(path):
            sb.Append(line.upper()).Append("\n")
    except FileNotFoundException as e:
        sb.Append("missing: ").Append(e.FileName ?? path)
    return sb.ToString()
```

### 13.1 Importing .NET types
- `from dotnet.<namespace> import <Type>, …` binds .NET types by name, with `as` to rename: `from dotnet.System.IO import File, Path as FilePath`. The namespace is written as in C#, after the reserved root `dotnet` (§4.7).
- A type may be a class, a static class, a struct, an interface or an enum. It must be public and not nested. Nested types are reached through the type containing them: `Environment.SpecialFolder.Desktop`.
- Since M43 it may be generic: `from dotnet.System.Collections.Generic import HashSet, Queue` imports the generic types, which are then given type arguments where they're used (§13.13). A name a namespace defines with several arities (`Tuple`, `KeyValuePair` and `Task`, say) stands for all of them, and the number of type arguments picks one; without any, it's the non-generic one, if there is one. Before M43, importing a generic type was `TP0231`.
- Imported names share the file's global scope, like classes (§4.7): a clash is `TP0214`. They live in the type namespace, as classes do, so `let sb: StringBuilder = StringBuilder()` names the type twice: once as a type and once as its constructor.
- `import dotnet`, and `from dotnet import X`, are `TP0221`, with help `use 'from dotnet.<namespace> import <type>'`. A namespace no referenced assembly defines is `TP0221`. A type the namespace lacks is `TP0201`.
- **Namespace imports (M43):** `import dotnet.System.IO as io` binds `io` to the namespace, whose types are then reached as `io.File.Exists(p)` and `io.FileInfo` in expressions, and `io.FileInfo` and `io.List[int]` in types, as a module's are (§4.7). Only the namespace's own types are reached this way, not its nested namespaces, which take an `import` of their own. The `as` is required: without it, it's `TP0221`, `'import dotnet.<ns>' needs a name`, with help `use 'import dotnet.<ns> as <name>'`. A namespace name used as a value is `TP0200`, as a module's is. A namespace import doesn't make its classes' extension methods available (§13.15).

### 13.2 Assemblies and packages
- The .NET runtime's own assemblies (`System.*` in `Microsoft.NETCore.App`) are always available.
- Other assemblies and NuGet packages are listed in `taipan.json`, in the main file's directory:

  ```json
  {
    "references": ["lib/Acme.Geometry.dll"],
    "packages": { "Humanizer.Core": "2.14.1" }
  }
  ```

  - `references` are paths to assemblies, relative to `taipan.json`.
  - Since M34, `assembly`, `namespace`, `version`, `authors` and `description` name a library and its package (§18.3).
  - `packages` maps NuGet package IDs to exact versions. Ranges aren't allowed. A package's own dependencies come with it.
  - Both are optional, and so is the file. Any other key is an error.
- **Restoring packages** uses the .NET SDK's `dotnet restore`, from the configured NuGet sources. The result is cached per user, keyed by the package list, so the SDK and the network are only needed the first time a list is used. A program without packages needs neither.
  - NuGet's settings (sources, credentials, the global packages folder) come from the `NuGet.config` files that apply to the main file's directory, as they would for a project there.
  - The cache is the directory `$TAIPAN_CACHE` names, if it's set, and otherwise `taipan` in the user's cache directory (`$XDG_CACHE_HOME`, or `~/.cache`; `%LOCALAPPDATA%` on Windows).
  - An assembly that both a package and the .NET runtime provide (such as `System.Text.Json`) is the runtime's.
- A missing or malformed `taipan.json` entry, a missing assembly, and a failed restore are reported as `taipan: <message>` and exit with `2` (§9), since they aren't errors in the source:
  - `<path>: invalid JSON (line <n>)`, `<path>: unknown key '<k>'`, `<path>: 'references' must be a list of paths`, `<path>: 'packages' must map package IDs to versions`, `<path>: package '<id>' needs an exact version, found '<v>'`, where `<path>` is `taipan.json`'s path
  - `assembly not found: <path>` / `not a .NET assembly: <path>`
  - `cannot restore packages: <the first error dotnet restore reports>`, or `cannot restore packages: the .NET SDK was not found`
  - `package '<id>' has native or runtime-specific assets, which aren't supported yet`
- `taipan run` loads the referenced assemblies, and `taipan build -o <dir>` copies them next to the program. Packages with native or runtime-specific assets are deferred.

### 13.3 Types
.NET types map to Taipan's at the boundary. An argument converts from Taipan to .NET, and a result (a method's, property's or field's value) from .NET to Taipan:

| .NET type | Taipan type | As an argument | As a result |
|---|---|---|---|
| `long` | `int` | as is | as is |
| `int`, `short`, `sbyte`, `byte`, `ushort`, `uint`, `ulong` | `int` | checked: out of range raises `OverflowError`, and a literal out of range is `TP0200` (`99999999999 is out of range for Int32`) | widened; a `ulong` above the `int` range raises `OverflowError` |
| `double` | `float` | as is (an `int` widens, as anywhere) | as is |
| `float` | `float` | rounded to the nearest `float` | widened |
| `bool`, `string` | `bool`, `str` | as is | as is |
| `char` | `str` | a one-character `str`: a literal is checked at compile time (`TP0200`, `"ab" is out of range for Char`), any other raises `ValueError` if it isn't one character | a one-character `str` |
| `void` | no value, as a function without a return type (§4.2) | | |
| `T?`, `Nullable<T>` | `T?` | | |
| `T[]` | `list[T]` | copied into a new array | copied into a new list |
| `List<T>` | `list[T]` | passed as is, so changes are shared | as is |
| `IEnumerable<T>`, `ICollection<T>`, `IList<T>`, `IReadOnlyCollection<T>`, `IReadOnlyList<T>` | `list[T]` | the list itself, or a copy if its elements convert | enumerated at once into a new list |
| `IOrderedEnumerable<T>` (M33, as a result only) | `list[T]` | | enumerated at once into a new list |
| `Dictionary<K, V>` (M33) | `dict[K, V]` | copied into a new `Dictionary` | copied into a new dict, in the order it enumerates |
| `IDictionary<K, V>`, `IReadOnlyDictionary<K, V>` (M33) | `dict[K, V]` | the dict itself, so changes are shared, or a copy if its keys or values convert | copied into a new dict, in the order it enumerates |
| `Func<A…, R>`, `Action<A…>`, and other delegate types (M33) | `Callable[[A…], R]`, or `Callable[[A…], None]` for no result | a function value or lambda, as a delegate that calls it (§13.9) | a function value that calls the delegate |
| `Task<T>`, `ValueTask<T>` (M33) | `Awaitable[T]` | the awaitable itself (`ValueTask<T>`: wrapped) | as is (`ValueTask<T>`: as a `Task<T>`) |
| `Task`, `ValueTask` (M33) | `Awaitable[None]` | as for `Task<T>` | as for `Task<T>` |
| `object` | `object` (M43, §3.20) | an `int`, `float`, `bool`, `str` or .NET value (not a Taipan list, dict, tuple, class, function, `py` or `None`, whose .NET forms are internal), or an `object` as is | before M43 `TP0231`; since M43 an `object` holding the .NET value itself (§13.17) |
| `decimal` (M43) | `Decimal`, opaque | | |
| other public classes, structs, interfaces and enums, and since M43 generic ones (§13.13) | the same type, opaque | | |

- `T` in a row stands for any type in the table, so `string[]` is `list[str]` and `List<int?>` is `list[int?]`. `List<T>` is passed as is only when `T` maps without conversion (`long`, `double`, `bool`, `string` or an opaque type); any other `List<T>` is copied, both ways.
- A dict's keys and values convert as list elements do, and a dict whose key and value types map without conversion is the only kind passed as is. A `Dictionary`'s keys must be valid dict keys (§3.1).
- **`object` (M43)** is Taipan's `object` (§3.20) wherever it appears, so `List<object>` is `list[object]`, passed as is, and `Func<object, string>` is `Callable[[object], str]`. `object?` is `object` too, since an `object` may hold `None`. A .NET `null` in an `object` is `None`.
- **`Decimal` (M43)** is an ordinary opaque .NET struct, imported as `from dotnet.System import Decimal`. Its operators (§13.10) give arithmetic and ordering, and an `int` converts to it implicitly (below), so `price * 3` and `Decimal.Parse("19.99") + 1` work, while a `float` needs `Decimal(x)`, as in C#.
- **Implicit conversions (M43):** an argument converts to a parameter of a .NET type that defines an implicit conversion (`op_Implicit`) from the argument's .NET form (§8), as C# converts it: `int` to `Decimal`. Its fit is *conversion* (§13.5).
- **Anything else is `TP0231`**, reported where a member using it is called, read or assigned: generic types whose type arguments aren't .NET forms of Taipan types (§13.13), `Span<T>` and other ref structs, delegates with `ref`, `out` or `in` parameters or generic delegates' open types, `Int128`, `BigInteger`, `nint`, pointers, and `ref` returns. Members that use such types can be present, as long as they aren't used. Since M33, generic methods aren't in this list: §13.8 says how they're called. Since M43, neither are `decimal`, `object` results, generic types, and `ref`, `out` and `in` parameters (§13.14).
- **Nullability.** A reference type annotated as nullable (`string?`) is `T?`, and one annotated as not null is `T`. A result of a not-null type that is null anyway, since annotations aren't checked by .NET, raises `TypeError` (`'<T>.<m>' returned null`) at the boundary, rather than surfacing later as a null in Taipan. Elements of a not-null element type (`string[]`) are checked the same way when the list is copied. In an assembly without nullable annotations, reference-type results are `T?` and reference-type parameters take `T`.
- **Opaque types** are written by their imported name in annotations and type arguments: `list[FileInfo]`, `Uri?`. A value of a .NET type that wasn't imported (such as a method's result) can still be used. Its type is displayed by its C# name without the namespace (`FileInfo`, `Environment.SpecialFolder`).
- `==` and `!=` on two values of the same .NET type use .NET's `Equals`. Since M33, the type's operators and indexers apply too (§13.10), and since M43 iteration over an `IEnumerable<T>` (§13.13), `isinstance`, `as` and `match` (§13.17), and a flags enum's operators (§13.19). Otherwise, ordering, arithmetic, `in`, indexing and dict keys don't apply (`TP0200`). `print`, `str()` and f-string holes use .NET's `ToString()`.
- A .NET value can't be passed to Python (`TP0200`, §10.3).

### 13.4 Members
- .NET names are used as they're written in .NET, case-sensitively: `sb.Append(x)`, `File.ReadAllLines(path)`, `sb.Length`, `Math.PI`. There's no renaming to snake_case.
- **Static members** are reached through the type: `File.Exists(p)`, `DateTime.Now`, `Int32.MaxValue`. **Instance members** are reached through a value, and include those inherited from base classes and `object` (`ToString()`). Reaching a static member through a value, or an instance member through the type, is `TP0210`.
- **Methods** are called with positional and keyword arguments, as Taipan functions are (§4.2). Keywords are the .NET parameter names, and a parameter with a default value may be left out.
- **Properties and fields** are read as `x.P`, and assigned as `x.P = v` when they have a public setter or aren't read-only. Assigning one that can't be set is `TP0202`.
- **Constructors** are called through the type: `StringBuilder()`, `StringBuilder(capacity=64)`, `Uri("https://example.com")`. A struct's parameterless constructor gives its default value. Constructing a static class is `TP0200` (`'File' is a static class`), as is constructing an interface, abstract class or enum (`'<T>' cannot be constructed`).
- **Enums:** an enum's members are values of the enum type, reached through it: `StringComparison.Ordinal`.
- An unknown member is `TP0210`, as for Taipan types: `StringBuilder has no method 'Apend'`. Members are public only; non-public members are unknown.
- A member of an optional .NET value needs narrowing first (`TP0203`), as for any optional.
- Taipan's own types keep their own members only. `str` is a .NET `string`, but `s.Substring(1)` is `TP0210`; use `s[1:]`.
  - **Except a list (M43):** a `list[T]` is a .NET `List<T>` (§8), so it also has `List<T>`'s public instance methods and properties whose names Taipan's list doesn't use: `xs.Sort()`, `xs.BinarySearch(x)`, `xs.ConvertAll(f)`, `xs.Count`. They're called as any .NET member is, with `T` as the type argument. Each call of a `List<T>` method counts as a change to the list, ending its elements' narrowing (§3.18).
  - Extension methods may apply where a type has no member of a name (§13.15).
- **Events (M43)** are subscribed to with `x.E += f` and unsubscribed from with `x.E -= f` (§13.18).

### 13.5 Overloads
.NET methods and constructors are often overloaded. A call picks one overload, by this rule:
1. **Candidates** are the public overloads with that name, static or instance as the call requires, skipping any that use a type `TP0231` covers.
2. A candidate is **applicable** if the arguments match its parameters by position and keyword (§4.2), and each argument converts to its parameter (§13.3).
3. **Each argument's fit** is, best first:
   - *exact*: its Taipan type is the parameter's mapped type with no conversion (`int` to `long`, `float` to `double`, `str` to `string`, a value to its own .NET type, a list to `List<T>` or an interface of it, `None` or a `T?` to a nullable `T`)
   - *conversion*: `int` to a narrower integer, `str` to `char`, a .NET value to a base class or interface, a list copied to an array or with its elements converted, and since M33, a `T` to a nullable value type (`int` to `long?`, so `Max(IEnumerable<long>)` is better than `Max(IEnumerable<long?>)`, as in C#); since M43, a Taipan class to a .NET class or interface it derives from (§13.16), a .NET value to an `IEnumerable<T>` or other collection interface it implements, which it's passed as, and an implicit conversion (§13.3)
   - *float conversion*: `int` to `double` or `float`, and `float` to `float`
   - *object*: any value to `object`
4. A candidate is **better** than another if each of its arguments fits at least as well and one fits better, or if they fit the same and it leaves out fewer parameters with defaults. Since M43, `out` parameters (§13.14) don't count as left out, a cancellation token Taipan passes (§13.20) counts as given, and with the arguments fitting the same, a candidate taking such a token is better than one that doesn't, and one taking a `params` parameter in its normal form is better than one taking it expanded (§13.14), as in C#.
5. **The overload chosen** is the applicable candidate no other is better than. If there are several such, the call is `TP0232`, naming two of them.

Since M33, a generic method's candidate has its type arguments inferred first (§13.8), and is applicable only if they can be; a lambda argument fits a delegate parameter whose parameter count matches it, as *exact*. Between two candidates whose arguments fit the same, a non-generic one is better than a generic one, and of two generic ones, the one whose parameters mention fewer of its type parameters (`Func<TSource, double>` over `Func<TSource, TResult>`), as in C#. A lambda argument's fit is its result's fit to the delegate's result type.

So `sb.Append(5)` calls `Append(long)`, `sb.Append("x")` calls `Append(string)` (not `Append(object)`), `Math.Max(1, 2)` calls `Max(long, long)`, and `TimeSpan.FromDays(2)` calls `FromDays(int)`, not `FromDays(double)` or `FromDays(int, int = 0, …)`. These are the overloads C# picks, too.

- If no candidate has the right number and names of arguments and there's only one candidate, its errors are reported as for a Taipan function (`TP0209`, `TP0218`). If exactly one candidate has them, each argument is checked against its parameter (`TP0200` at the argument). Otherwise, with no applicable candidate, it's `TP0200`, `no overload of 'StringBuilder.Append' takes (list[int])`.
- If the only candidates with that name use types `TP0231` covers, the call is `TP0231`, naming a type that keeps the first one out.

### 13.6 Exceptions
- **Catching by .NET class:** `except C`, where `C` is an imported .NET class deriving from `System.Exception`, matches a .NET exception of that class or a class deriving from it, as for Taipan classes. `except C as e` binds `e` with type `C`, so its .NET members can be used (`e.Message`, `e.FileName`). Listing several .NET classes binds their nearest common .NET base. A clause can't list both Taipan and .NET classes with `as` (`TP0200`): write separate clauses.
- **Catching as `Exception`:** a .NET exception reaching `except Exception`, a bare `except:` or any other Taipan class's clause is a `DotNetError` (§3.10), with `type` the .NET class's full name and `message` the .NET exception's `Message`. So `except Exception` still catches everything.
- .NET's `OverflowException` is Taipan's `OverflowError`, as for checked arithmetic (§8), whichever code throws it.
- Unreachable clauses (`TP0300`) are checked for .NET classes too.
- **Uncaught**, a .NET exception prints a traceback ending in `<full class name>: <message>` (§3.19), and exits with 101. Before M41 it printed `.NET <full class name>: <message>`.
- **Raising (M43):** `raise e` takes a value of a .NET class deriving from `System.Exception` as well as a Taipan exception: `raise InvalidOperationException("closed")`. It's thrown as that .NET exception, so .NET code and `except InvalidOperationException` see it as it is, and `except Exception` as a `DotNetError`, as above. `raise e` with a .NET exception caught by `except C as e` throws it again. A .NET value of another class is `TP0200`, `'<C>' is not an exception class`. `raise … from …` with a .NET exception on either side is `TP0200`, `a .NET exception can't have a cause`, with help `pass it to the constructor as the inner exception` when it's the cause: .NET's `InnerException` plays that part, and is fixed when the exception is constructed.
- A Taipan class can't derive from a .NET exception class (`TP0225`, §13.16).

### 13.8 Generic methods (M33)
- A generic .NET method is called as any other, with its type arguments inferred from the call: `Enumerable.Where(xs, lambda x: x > 2)`, `Enumerable.Max(scores)`, `Array.IndexOf(names, "b")`.
- **Inference** works as C#'s does, in phases:
  1. Each argument that isn't a lambda gives its type's .NET form (§8: `int` is `long`, `list[T]` is `List<T>`, a Taipan class is itself) to the parameter it fills, which fixes the type parameters that appear there. `List<long>` against `IEnumerable<TSource>` fixes `TSource` as `long`.
  2. Then each lambda whose delegate parameter's input types are fixed is bound with those types, as `Callable[[int], …]`, and its result fixes the type parameters in the delegate's result: `Enumerable.Select(xs, lambda x: str(x))` fixes `TResult` as `string`. This repeats until nothing more is fixed.
  3. A type parameter fixed two different ways is `TP0200`, `'<T>' is inferred as both <A> and <B>`, and one left unfixed is `TP0206`, `cannot infer '<T>' for '<T>.<m>'`.
- **A type argument may be any Taipan type**, in its .NET form, so `Enumerable.Reverse(shapes)` with `shapes: list[Shape]` is `IEnumerable<Shape>`, a `list[Shape]` again as a result. Results whose type mentions a type parameter are mapped back as §13.3 says, with the type argument's Taipan type: `TSource` fixed as `long` is `int`.
- **Constraints** (`where T : struct`, `class`, `new()`, a base class or interface such as `IComparable<T>`) are checked against the type argument's .NET form: `TP0200`, `'<A>' doesn't satisfy the constraint '<C>' on '<T>' of '<T>.<m>'`. So `Enum.IsDefined(5)` is `TP0200`, since `TEnum` must be an `Enum`. A Taipan class has no .NET interfaces of its own, so it satisfies only `class` (and `new()` when its constructor takes no arguments) among constraints.
- A generic method whose type parameters can't all be inferred from its parameters, such as `Array.Empty<T>()`, couldn't be called before M43 (`TP0206`). Since M43 it takes explicit type arguments (below).
- **Explicit type arguments (M43)** follow the method's name, in brackets, as in C#: `Array.Empty[str]()`, `JsonSerializer.Deserialize[Point](text)`, `xs.Cast[int]()` (§13.15). They're written as types are where an expression is parsed, as in `Box[int].make(1)` (§4.9): a name, `list[…]`, `dict[…, …]` or a generic type with its arguments.
  - Every type argument is given; none are inferred. The candidates are the overloads with that many type parameters. If none has, it's `TP0209`, `'<T>.<m>' takes <n> type arguments, found <k>`, naming one overload's count. Arguments are then checked against the parameters with the type arguments put in, and constraints are checked as for inferred ones.
  - Type arguments on a .NET method that isn't generic are `TP0209` too, `'<T>.<m>' takes 0 type arguments, found <k>`. A Taipan function's type arguments are still always inferred (§3.6).

### 13.9 Delegates (M33)
- **A function value or lambda converts to a delegate type** whose `Invoke` maps (§13.3): `Func<long, bool>` is `Callable[[int], bool]`, `Action<string>` is `Callable[[str], None]`, `Predicate<T>` and `Comparison<T>` and any other named delegate type by their `Invoke`. The delegate calls the function, converting each argument from .NET to Taipan and the result back, as a call across the boundary does (§13.3), so `Func<int, int>` takes a `Callable[[int], int]` and checks its results' range.
- **A lambda** passed where a delegate is expected takes its parameter types from the delegate (after inference, §13.8), as a lambda passed to a `Callable` parameter does (§3.7). Its body is checked as Taipan code, with Taipan's semantics.
- **A delegate result** (a method's, property's or field's) is a function value that calls it. It displays as `<function <delegate>>`, and equality is identity.
- An exception escaping the Taipan function while .NET calls it propagates through the .NET code to the Taipan code that called it, as any exception does.

### 13.10 Indexers and operators (M33)
- **Indexers:** `x[i]` on a .NET value calls its indexer (`this[…]`, the `Item` property), and `x[i] = v` its setter, choosing among overloaded indexers by §13.5's rule. An indexer with several parameters takes them as `x[i, j]`, which elsewhere, as in Python, indexes with the tuple `(i, j)` (a dict with tuple keys, say). A value without one, or one without a setter when assigned, is `TP0200`, `cannot index <T>` / `TP0202`.
- **Operators:** `+ - * / %`, unary `-`, and `< <= > >=` on .NET values call the operators their types define (`op_Addition`, `op_LessThan`, …), chosen by §13.5's rule among both operand types' operators, so `DateTime.Now - start` is a `TimeSpan` and `span < TimeSpan.FromSeconds(1)` a `bool`. `==` and `!=` still use `Equals` (§13.3). An operator neither type defines is `TP0200`, `operator '<op>' cannot be applied to <S> and <T>`. Taipan's `//` and `??` have no .NET operators.

### 13.11 Tasks and await (M33)
- A .NET method returning `Task<T>` or `ValueTask<T>` gives an `Awaitable[T]` (§3.14), and one returning `Task` or `ValueTask` an `Awaitable[None]`, so `await HttpClient().GetStringAsync(url)` is a `str` and `await Task.Delay(100)` gives no value.
- An `Awaitable` passed to a `Task<T>` parameter is the task itself, so `Task.WhenAll(first, second)` works on Taipan async functions' results (and gives an `Awaitable[list[T]]`, since `T[]` is `list[T]`).
- A .NET exception a task ends with is raised by the `await`, as §13.6 says. `TaskCanceledException` is an ordinary .NET exception class.

### 13.13 Generic types (M43)
```python
from dotnet.System import Int32
from dotnet.System.Collections.Generic import HashSet, Queue
from dotnet.System.Linq import Enumerable

let seen = HashSet[str]()
seen.Add("a")
print(seen.Add("a"), seen.Count)                      # False 1
print([1, 2, 3, 4].Where(lambda x: x > 2).Select(str))   # ["3", "4"]
print(Int32.TryParse("12"))                           # (True, 12)
```

- **Written** with type arguments in brackets, as Taipan's generic classes are (§3.6): `HashSet[str]` in a type, `HashSet[str]()` and `Queue[int](capacity=8)` constructing one, `Comparer[str].Default` reaching a static member. A type argument may be any Taipan type, in its .NET form (§8), as for a generic method (§13.8): `HashSet[int]` is `HashSet<long>`, and `Queue[list[Shape]]` is `Queue<List<Shape>>`. The wrong number of type arguments, or none, is `TP0209`, as for a class: `'HashSet' takes 1 type argument, found 0`. Type arguments are never inferred, as in C#. Constraints are checked as §13.8 says.
- **Types §13.3 maps** (`List`, `Dictionary`, the collection interfaces, `Nullable`, `Task`, `ValueTask` and delegates) aren't written this way: `List[int]` is `TP0231`, `'List[int]' is list[int] in Taipan; write 'list[int]'`.
- **Members** are the constructed type's, with its type parameters standing for the Taipan type arguments: `HashSet[int]`'s `Add` takes an `int` and gives a `bool`. They're used as §13.4 says.
- **As a result**, a constructed generic type is that opaque type when each type argument is the .NET form of a Taipan type: `HashSet<string>` is `HashSet[str]`. Otherwise it's `TP0231`, like other types Taipan can't use: `HashSet<int>`, since no Taipan type's .NET form is `Int32`.
- **Assignability (§3.3):** a constructed type is assignable to its .NET base classes and interfaces, with the type arguments put in (`HashSet[str]` to `ISet[str]`). Type arguments are invariant: .NET's `in` and `out` variance doesn't apply.
- **Iteration:** a .NET value whose type implements `IEnumerable<T>`, for a `T` that maps as a result (§13.3), is an `Iterable[T]` (§3.13), so `for`, unpacking, comprehensions and the iterator built-ins (`list`, `sorted`, `sum`, …) take it, and each element converts as a result does. As an argument it's also passed, as is, to an `IEnumerable<T>` parameter or another collection interface it implements (§13.5). Changing a .NET collection while iterating over it raises .NET's `InvalidOperationException`, as a `DotNetError`.
- **Display:** in diagnostics, Taipan's form: `HashSet[str]`. At run time, .NET's `ToString()`, as for any .NET value.

### 13.14 `ref`, `out` and `params` (M43)
- **An `out` parameter** isn't given in the call. The call's value is a tuple of the method's result followed by the value of each `out` and `ref` parameter after the call, in order, as IronPython and pythonnet give them: `Int32.TryParse("12")` is `(True, 12)`, a `tuple[bool, int]`, and `Queue[str]`'s `TryDequeue()` gives `(found, value)`. Without a result, the tuple is just the parameters' values. A single value, from a method returning nothing with one `out`, is that value itself. The values convert as results do (§13.3).
  - Arguments fill the other parameters by position and keyword (§4.2). Giving an `out` parameter, by keyword or as one too many positional arguments, means the overload doesn't take the call.
- **A `ref` parameter** is given as an argument, converted as arguments are, and its value after the call comes back as an `out` parameter's does. **`in` and `ref readonly` parameters** are given as ordinary arguments, and nothing comes back.
- A `ref` return, and `ref`, `out` or `in` parameters of delegates, properties and indexers, are still `TP0231`.
- **A `params` parameter** whose type maps to `list[T]` (§13.3), such as `params string[]`, takes either a list, its *normal form*, or any number of arguments, none included, each converting to `T`, its *expanded form*: `Path.Combine("a", "b", "c")`, `String.Join(", ", "x", "y")`. As in C#, the expanded form applies only when the normal form doesn't, and each extra argument's fit is its fit to `T` (§13.5). The parameter can be named as a keyword only in its normal form. `params ReadOnlySpan<T>` overloads use a ref struct, so they aren't candidates (§13.3).

### 13.15 Extension methods (M43)
- **`x.m(…)`** calls an extension method `m` when `x`'s type has no member named `m`: none of Taipan's own, of `List<T>`'s for a list (§13.4), or of the .NET type's. `x` is passed as its first argument, so `xs.Where(lambda x: x > 2)` is `Enumerable.Where(xs, lambda x: x > 2)`, and `xs.Order()` sorts with .NET's comparer, which a class implementing `IComparable<T>` takes part in (§13.16).
- **The candidates** are the extension methods named `m` of the static classes the file imports by name with `from dotnet.<ns> import` (not through a namespace import, §13.1), chosen among as overloads are (§13.5), with `x` as the first argument. If none takes `x` as its first argument, it's `TP0210` as before, `<T> has no method '<m>'`.
- `x` may be any value whose type fits the first parameter: a list, dict or str, a .NET value, or a Taipan class deriving from a .NET type. An optional `x` needs narrowing first (`TP0203`), as for any member.
- C# 14's extension members other than methods (properties, operators) are deferred.

### 13.16 Deriving from .NET classes, and implementing .NET interfaces (M43)
```python
from dotnet.System import IComparable, Random

class Version(IComparable[Version]):
    major: int
    minor: int

    def __init__(self, major: int, minor: int):
        self.major = major
        self.minor = minor

    def CompareTo(self, other: Version?) -> int:
        if other is None:
            return 1
        return (self.major - other.major) * 1000 + (self.minor - other.minor)

class Loaded(Random):
    def __init__(self):
        super().__init__(7)

    @override
    def Next(self) -> int:
        return 4

let versions = [Version(2, 0), Version(1, 5)]
versions.Sort()             # List<T>.Sort, through IComparable<Version>
let r: Random = Loaded()
print(r.Next())             # 4
```

- **Bases:** a plain class may list .NET interfaces among its bases, generic ones with type arguments (which may name the class itself), and at most one .NET class, which counts as its one base class (§4.3, `TP0225`). A dataclass may list interfaces, but not a .NET class (`TP0225`, as for a plain base). The .NET class must be public, neither sealed nor static, not a struct or exception class, and have a public or protected constructor; otherwise it's `TP0225`, `cannot derive from '<B>': it's <sealed|static|a struct|an exception class|without a constructor Taipan can call>`, and with help `derive from 'Exception'` for an exception class.
- **The class is assignable** to its .NET base, its bases' bases and the interfaces it and they implement (§3.3), so it's passed to .NET as is (*conversion* fit, §13.5), and .NET code that calls the interface's or base's members calls the class's methods.
- **Implementing an interface:** the class must define a method for each method of the interface, and of the interfaces it extends, that has no default implementation, with the interface method's name (`TP0225`, `'<C>' must define '<m>' to implement '<I>'`). It takes `self`, then a parameter for each of the .NET method's, typed as the parameter's type maps as a result (§13.3), since .NET passes them in, and returns the type the .NET result maps to as an argument, since Taipan gives it back: `int CompareTo(T? other)` is `def CompareTo(self, other: Version?) -> int`. Parameter names don't have to match. Any other signature is `TP0228`, `'<m>' must be 'def <m>(self, <params>) -> <R>', as in '<I>.<m>'`. These methods need no `@override`, as for a protocol (§3.11). An interface with a property, event or indexer, a generic method, or two methods of one name, that the class would have to implement is `TP0231`, `'<I>' has <the property '<p>'|…>, which a Taipan class can't implement yet`.
- **Overriding a .NET class's methods:** a method with the name of a public `virtual` or `abstract` method of the .NET base, or of a class above it, overrides it, marked `@override` (`TP0227`). Of overloads with that name, it overrides the one whose parameters it takes, typed as for an interface method (above); if it takes none of them, it's `TP0228`. The class must override every `abstract` method of its .NET bases (`TP0225`, `'<C>' must override '<B>.<m>'`), and a .NET base with an abstract property, event or indexer is `TP0231`. In Taipan code, the method hides the base's other overloads of that name on the class's own type. `ToString`, `Equals` and `GetHashCode` are `__str__` (or `__repr__`), `__eq__` and `__hash__` (§4.8): defining a method with one of those names is `TP0225`, `'<m>' is defined by '<special>'`.
- **The base's members:** the .NET base's public instance members, inherited ones included, are members of the class, after its own fields and methods: `self.Next(10)` on `Loaded` is `Random.Next(int)`, and a `Loaded` can be passed wherever a `Random` is expected.
- **Construction:** `super().__init__(…)` calls the .NET base's constructor, chosen as overloads are (§13.5). .NET constructs the base before anything else, so in a class with a .NET base it must be `__init__`'s first statement, and its arguments may use `__init__`'s parameters but not `self` (`TP0226`, `'super().__init__' must come first, since '<B>' is a .NET class`); the class sets its own fields after it. A .NET constructor that calls a virtual method the class overrides runs that method before the class's fields are set, as in C#; reading one there gives its .NET default.
- **What's deferred:** deriving from a Taipan class whose base is a .NET class (`TP0225`, `'<C>' derives from the .NET class '<B>', so it can't be derived from yet`), generic classes implementing .NET interfaces (`TP0225`, `generic classes cannot implement .NET interfaces yet`), protected members, and overriding properties. A class that only implements interfaces can be derived from as usual.

### 13.17 Type tests on .NET types and `object` (M43)
- **`isinstance(x, T)`, `x as T`, `x as T?` and class patterns** take a .NET class, interface, struct or enum `T` that isn't generic (`TP0200`, `'<T>' is generic`, as for classes, §3.18), when `x` is an `object`, a .NET class or interface, a Taipan class with .NET bases, or an optional of one. They test the runtime type, as `is` does in C#, and `isinstance` narrows `x` (§3.9). `x as T` raises `TypeError`, `cannot convert <runtime type> to <T>`, when it fails, as for classes (§4.3).
  - `T` must be able to hold `x`'s value: one derives from the other, or one is an interface and the other isn't a sealed class or struct that doesn't implement it, or `x` is an `object`. Otherwise it's `TP0200`, `<S> can never be a <T>`.
- **An `object`** takes any `T`, Taipan's types and classes included: `isinstance(o, int)` is `True` when `o as int` would succeed (§3.20), and narrows `o`, whose reads then convert it as `as` does, as for `py` (§3.18). `as int` takes any .NET integer, widened (and `OverflowError` for a `ulong` too big), `as float` a `double` or `float`, and `as str` a `string` or a `char`, since .NET code gives those as `object`s.
- **Class patterns** on these types name a .NET type: `StringBuilder()`, `FileInfo(Name=n, Length=size)`. Keyword sub-patterns name public instance properties and fields, and match their values, converted as results. Positional sub-patterns are `TP0209`, `'<T>' is not a dataclass, so its fields can only be bound by name`. A `match` on an `object` takes class patterns of any class, .NET or Taipan, and is never exhaustive without a `_` or binding arm.

### 13.18 Events (M43)
- **`x.E += f`** subscribes to the public event `E` of `x`'s type, and **`x.E -= f`** unsubscribes; a static event is `T.E += f`. `f` is a function value, a `__call__` object or a lambda, converted to the event's delegate type as a delegate argument is (§13.9): `NotifyCollectionChangedEventHandler` takes a `Callable[[object, NotifyCollectionChangedEventArgs], None]`.
- **One delegate per function value:** converting the same function value to the same delegate type always gives the same delegate, so `-=` removes what `+=` added. A lambda makes a new function value each time it's evaluated (§3.7), so keep it in a `let` to unsubscribe it later.
- A handler runs when .NET raises the event, on the thread that raises it, as any delegate does (§13.9).
- Any other use of an event, reading it, assigning it or calling it, is `TP0200`, `'<E>' is an event; use '+=' or '-='`.

### 13.19 Flags enums (M43)
- A .NET enum with `[Flags]` (`FileAttributes`, `RegexOptions`, …) has `a | b`, `a & b` and `a ^ b`, which give the enum type, and `a in b`, which is `True` when `b` has every flag that's set in `a`, as `b.HasFlag(a)` and Python's `Flag` do: `FileAttributes.Hidden in attrs`.
- On another enum, these are `TP0200`, `operator '<op>' cannot be applied to <S> and <T>` (`in` keeps its own message). A value displays as .NET shows it, `ReadOnly, Hidden`.
- `~` and Taipan's own bitwise operators on `int` are deferred.

### 13.20 Cancelling .NET tasks (M43)
- **Each running async function has a .NET `CancellationToken`**, which is cancelled when the function is (§3.20). A cancellation that's caught doesn't cancel what the function starts after it: from then on the function has a new token.
- **In an async function**, and at a top level with an `await` (§3.14), a .NET call may leave out a `CancellationToken` parameter, even one without a default, and the function's token is passed. Choosing an overload, such a token counts as given, and between candidates whose arguments fit the same, one taking it is better (§13.5). So `await Task.Delay(10_000)` calls `Delay(int, CancellationToken)`, and `await client.GetStringAsync(url)` the overload taking a token, as C# code that passes its token on would.
- So cancelling the function cancels the .NET work it's waiting for: in `async with asyncio.timeout(0.1): await Task.Delay(10_000)`, the delay's task is cancelled, and the `async with` raises `TimeoutError`, as in §3.20.
- The token is the async function's own, and isn't passed by calls in a lambda or nested function inside it, which aren't async. A token can still be passed explicitly, as any argument can.

### 13.21 Deferred
§11 lists what's deferred. Before M43 it included generic .NET types other than `Dictionary`, `Task` and delegates, explicit type arguments, extension-method syntax, events, `ref`/`out`/`in`, `params` arrays, deriving from .NET types, raising .NET exceptions, and type tests on .NET types, which §13.13 to §13.20 describe.

---

## 14. Taipan as a .NET library (M22)
`taipan build <file> --library -o <dir>` compiles a program's files into a .NET assembly that C#, F# and other .NET code can reference. Its definitions are the assembly's public API, with the types §13.3 maps from .NET used in the other direction: `int` is `long`, `str` is `string`, `list[T]` is `List<T>`.

```python
# geometry.tpn
import shapes

const VERSION = "1.0"

def total_area(items: list[shapes.Shape]) -> float:
    var total = 0.0
    for item in items:
        total += shapes.area(item)
    return total
```

```csharp
using Geometry;

var circle = new shapes.Shape.Circle(2.0);
Console.WriteLine(geometry.total_area([circle, new shapes.Shape.Square(3.0)]));
try { shapes.parse("hexagon"); }
catch (shapes.UnknownShape e) { Console.WriteLine(e.message); }
```

### 14.1 Layout and names
- **The assembly** is `<name>.dll`, where `<name>` is the main file's name without `.tpn`, unless `taipan.json`'s `assembly` (or an SDK project's `AssemblyName`) names it (M34, §18.3).
- **The namespace** is `<name>` in PascalCase: each `_`-separated part capitalized and the underscores dropped, so `geometry` is `Geometry` and `health_checks` is `HealthChecks`. Since M34, `taipan.json`'s `namespace` (or an SDK project's `RootNamespace`) sets it instead, as a dotted name: `Acme.Shapes`.
- **Each module**, the main file included, is a public static class in that namespace, named as the module: `Geometry.geometry`, `Geometry.shapes`. Its functions are static methods, and its constants static read-only properties.
- **Classes and enums** are nested in their module's class: `Geometry.shapes.Point`, `Geometry.shapes.Shape.Circle`. So two modules may define the same name, as in a program.
- **Names are as written in Taipan**, case and underscores included: `shapes.area_of(r)`, `p.distance_to(q)`, `p.x`. There's no renaming to PascalCase, just as .NET names aren't renamed in Taipan (§13.4).
- **Private names:** a definition, field or method whose name starts with `_` isn't exported. It's internal to the assembly, and Taipan code uses it as before. Privacy applies only at the library's edge: within the library, every module still exports every name (§4.7).
- **Stable:** only exported definitions are public, by their Taipan names. Everything else the compiler generates (generic instances, default-value helpers, nested functions, renamed locals) is internal, so renaming a local or adding a private definition doesn't change the public API.

### 14.2 What's exported
| Taipan | Public .NET form |
|---|---|
| `def f(a: int, b: str = "x") -> float` | `public static double f(long a, string b)`, plus an overload for each trailing parameter with a default, `f(long a)`, that evaluates the default as a Taipan call would (§4.2) |
| parameter kinds (M30) | Every parameter keeps its place. Positional-only and keyword-only parameters are ordinary C# parameters, which C# callers may pass by position or by name. `*args: T` is a `List<T>` parameter, `params` when it's last, so `def total(*xs: float)` is `total(params List<double> xs)`; `def g(a: int, *rest: str, sep: str)` is `g(long a, List<string> rest, string sep)`. `**kwargs: T` is an `OrderedDictionary<string, T>?` parameter, where `null` means none. |
| `const LIMIT = 10` | `public static long LIMIT { get; }` |
| `@dataclass class P` | a public class with a public constructor over every field (and overloads for trailing defaults), a `{ get; set; }` property per field, its methods as public instance methods (with overloads for trailing defaults, as for functions), and `ToString` giving Taipan's display form |
| `class C` (plain) | a public class with a public constructor taking `__init__`'s parameters (and overloads for trailing defaults), which runs `__init__`; its fields and methods as for a dataclass |
| exception classes | as plain classes, deriving from the runtime's `Taipan.Runtime.TaipanException` (Taipan's `Exception`), or another exception class |
| `enum E` | a public abstract record with a nested public sealed record per variant: `new Shape.Circle(2.0)`, `new Shape.Empty()` |
| generic definitions | real .NET generics, where they can be (below) |
| protocols, type aliases | nothing, as in a program (§8), except that a protocol used as a type (M40) is a public interface, which the classes satisfying it implement. A generic protocol's interface is monomorphized, as in a program (`Source_int`). A generic class that's used as a protocol type is monomorphized too, so each instantiation can implement the interfaces it satisfies, and isn't exported (`TP0302`, `it's used as the protocol '<P>'`). |

- **Types in signatures** are §8's: `int` is `long`, `float` is `double`, `bool`, `str`, `T?` (`long?` or `string?`), `list[T]` is `List<T>`, `dict[K, V]` is `OrderedDictionary<K, V>`, `tuple[A, B]` is a value tuple, `tuple[T, ...]` is `Taipan.Runtime.VarTuple<T>` (M41), a function type is `Taipan.Runtime.Function<D>` (constructed from a delegate: `new Function<Func<long, long>>("twice", x => x * 2)`), `py` is pythonnet's `PyObject`, `Iterable[T]` is `IEnumerable<T>`, which a C# caller may pass any sequence as, and `Iterator[T]` is `Taipan.Runtime.IIterator<T>`, an `IEnumerable<T>` it can loop over once (M31), `Awaitable[T]` is `Task<T>`, so an `async def` is a C# async method (M33), and .NET types are themselves. Lists and dicts are shared, not copied, both ways.
- **Equality and hashing** are .NET's for the C# caller: a class's `Equals` is reference identity (or, since M28, `==` for a class that defines `__hash__`, §8), and an enum's is the record's (field by field, with C#'s `Equals` on each). `Taipan.Runtime.Equality.Equal(a, b)` compares as Taipan's `==` does.
- `__init__`, `__new__` and the helpers that evaluate defaults are internal, as names starting with `_` are. So are special methods (M28), though `__str__` gives `ToString`, and a class whose body defines `__hash__` overrides `Equals` and `GetHashCode` with `==` and `hash()`. A property is a C# property, a static or class method a public static method (a class method's `cls` is its own class), and a class attribute a public static property. Classes implement the runtime's `IStruct` or `IObject`, which display and equality use; they aren't meant for callers. Deriving from a Taipan class in another .NET language isn't supported.
- **Generic definitions.** A generic function, class or enum is exported as a .NET generic (`first<T>`, `Box<T>`) when:
  - none of its type parameters has a bound (§3.11), and
  - no type parameter appears as `T?`, in its signature, fields or body, since .NET has no type that is `T?` for both value and reference types, and
  - every generic definition it uses with its own type parameters is exported as a .NET generic too.

  In a library, such a class or enum is a .NET generic everywhere, so `Box[int]` in a signature is `Box<long>`. Any other generic definition is monomorphized as in a program (§8) and isn't exported.
- **Not exported (`TP0302`).** A definition without a leading `_` that can't be exported is warning `TP0302`, at its name, saying why: a generic definition that can't be a .NET generic, a definition whose signature uses a type that isn't exported (for a class: its base class, or the types of its public fields, constructor and methods), or a definition named the same as its module (which C# can't express). It's still compiled, and Taipan code in the library uses it. Renaming it with a leading `_` says it's private on purpose, and silences the warning.

### 14.3 Running library code
- **No top level:** a library's main file contains declarations only, as other modules do (§4.7), so a top-level statement in it is `TP0222`. Nothing runs when the library loads.
- **Module initialization.** A module initializes when one of its exported functions, constructors or constants is first used. Initializing a module first initializes the modules it imports, then imports its Python modules (§10.1), then evaluates its constants in source order. Each module initializes once, and a cycle is broken at the module reached first, as in a program (§4.7).
  - If initialization raises, the call that triggered it throws that exception, and the next use tries again.
  - If Python can't be loaded, the call throws `Taipan.Runtime.TaipanPanic`, with §10.5's message `cannot load Python: <reason>`. Python is found as §10.6 says, when it's first needed.
- **Exceptions propagate** to the caller as .NET exceptions. There's no panic handler and nothing is printed:
  - Taipan's exceptions are their runtime classes (`Taipan.Runtime.KeyError`, `Taipan.Runtime.ValueError`, …) or the library's own classes (`Geometry.shapes.UnknownShape`), with `message` and `Message` both the Taipan message.
  - Integer overflow is .NET's `System.OverflowException`, as from C#'s checked arithmetic.
  - A Python exception is a `Taipan.Runtime.PythonError`, and a .NET exception from .NET code propagates unchanged.
- **`print`** writes to the process's standard output, as `Console.Out` does.

### 14.4 Building
- `taipan build <file> --library -o <dir>` writes `<name>.dll` with `Taipan.Runtime.dll`, `Python.Runtime.dll` and any assemblies `taipan.json` references (§13.2) beside it. There's no runtimeconfig, since a library doesn't run by itself.
- `taipan check <file> --library` and `taipan emit <file> --library` check and emit the program as a library, with the library's diagnostics (`TP0222` in the main file, `TP0302`).
- **Referencing a library** from a .NET project means referencing `<name>.dll` and the `TaipanLang.Runtime` package (§12.2) of the version that built it, or `Taipan.Runtime.dll` from the build directory. A library that uses Python needs `Python.Runtime.dll` too, which the package brings.
- The library targets the .NET runtime `taipan` runs on (`net10.0`). It compiles against that runtime's reference assemblies, as every .NET library does, so other projects can reference it. They come with the .NET SDK (`packs/Microsoft.NETCore.App.Ref` beside the runtime), so building a library needs the SDK, unlike building a program (§12.2). Without it, `taipan: building a library needs the .NET SDK's reference assemblies (Microsoft.NETCore.App.Ref)` exits with `2`.

### 14.5 Deferred
§11 lists what's deferred: XML documentation from docstrings, `Equals` and `GetHashCode` on dataclasses, deriving from Taipan classes in other .NET languages, and delegates for function types. A configurable namespace, assembly name and version, and packing a library as a NuGet package, came in M34 (§18); PascalCase names aren't planned.

---

## 15. Native executables (M23)
`taipan build <file> --native -o <dir>` compiles a program ahead of time, through .NET's NativeAOT, into one self-contained native executable. It runs without a .NET runtime installed, and starts faster than `taipan run`.

```
$ taipan build examples/health.tpn --native -o out
built out/health
$ out/health
```

### 15.1 Building
- **The executable** is `<dir>/<name>` (`<name>.exe` on Windows), where `<name>` is the main file's name without `.tpn`. Nothing else is needed beside it to run.
- **The platform** is the operating system and architecture `taipan` runs on, by its portable runtime identifier, such as `linux-x64`, even where a distribution's own build of .NET reports its own (`ubuntu.24.04-x64`). Since M45, `--runtime <rid>` chooses another architecture (§15.4).
- **Behavior:** a native program prints the same output and exits with the same codes as under `taipan run`, panics included (§7), with the same `.tpn` locations (§15.3).
- **Requirements:** building natively uses the .NET SDK's `dotnet publish`, with the native toolchain NativeAOT needs on the platform (a C compiler and linker: clang or gcc on Linux, Xcode's command-line tools on macOS, Visual Studio's C++ tools on Windows). The ILCompiler package comes with the SDK or is restored from the configured NuGet sources (§13.2) the first time. Running the result needs none of these.
- **References:** the assemblies and packages of `taipan.json` (§13.2) are compiled into the executable.
- **Errors** in the build environment are reported as `taipan: <message>` and exit with `2` (§9), as for `taipan.json`:
  - `building natively needs the .NET SDK` when `dotnet publish` can't be run
  - `native build failed: <the first error dotnet publish reports>`, for instance when the toolchain is missing
- **Warnings** the AOT compiler reports about referenced assemblies are printed as `taipan: warning: <message>`, and don't stop the build. A program that uses only Taipan and the .NET runtime's own assemblies produces none.
- `--native` can't be combined with `--library` (a usage error). `taipan check <file> --native` and `taipan emit <file> --native` check and emit the program as for a native build, with its diagnostics (`TP0233`) and line tracking (§15.3).

### 15.2 What a native program can't use (`TP0233`)
A program that can't work natively is rejected at compile time, with `TP0233` where it uses what can't work, rather than failing in `dotnet publish` or at run time:
- **Python interop (§10).** pythonnet relies on reflection and on generating code at run time, which NativeAOT doesn't support. Each `from python import …`, and each `py` written as a type, is `TP0233`: `a native program can't use Python`, with help `build it without '--native'`.
- **.NET members that need run-time code generation or reflection.** A .NET method, constructor, property or field that .NET marks `[RequiresDynamicCode]` or `[RequiresUnreferencedCode]`, on itself or on its type, is `TP0233` where it's used: `'<T>.<m>' can't be used in a native program: <the attribute's message>`. These are the members .NET's own AOT analysis warns about.
- Everything else in Taipan works natively.

### 15.3 Line tracking
A native executable has no debug information to map its stack to `.tpn` lines, so the compiler records lines itself (§8). Before M41 this was emitted only for `--native`, to locate a panic. Since M41 every program is emitted this way, native or not, since a traceback (§3.19) needs every frame's line and Python name, which a .NET stack trace doesn't give:
- Each emitted method that's a Python frame (functions, methods, nested `def`s, lambdas, generator expressions and the top level) keeps the `.tpn` line of the statement it's running, and its file, in locals. A generator's body shares its line with the generator object, which adds its frame when an exception leaves the body.
- An exception filter around each such method's body adds the frame to the exception's traceback as the exception passes through, innermost first. The filter never catches the exception. An `except` clause adds its own frame when it catches one.
- Default helpers and the lambdas of list, set and dict comprehensions have no frame of their own, so the frame they run in reports them.

This adds a local store per statement and a filter per method.

### 15.4 Other platforms (M45)
```
$ taipan build examples/health.tpn --native --runtime linux-arm64 -o out
built out/health
```
- **`--runtime <rid>`** builds a native executable (§15.1), native library (§15.6) or extension module (§17) for a runtime identifier other than the one `taipan` runs on. It may be given with `--native`, `--library --native` and `--python-extension` to `build`, and with nothing else (a usage error, `--runtime needs --native or --python-extension`).
- **The runtime identifiers** are `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`, `win-x64` and `win-arm64`. Any other is a usage error: `unknown runtime '<rid>'; use one of linux-x64, linux-arm64, osx-x64, osx-arm64, win-x64, win-arm64`.
- **The same operating system only:** NativeAOT builds for the operating system it runs on, in any of its architectures. A runtime of another operating system is `taipan: cannot build for <rid> on <os>: NativeAOT builds only for the operating system it runs on` (exit `2`, §9), with `<os>` `linux`, `osx` or `win`.
- **What cross-compiling needs**, besides §15.1's toolchain for the platform `taipan` runs on:
  - **Linux:** GCC and binutils for the target, as `<triple>-gcc` and `<triple>-objcopy` on `PATH`, and the target's C library, with `<triple>` `aarch64-linux-gnu` for `linux-arm64` and `x86_64-linux-gnu` for `linux-x64`. On Debian and Ubuntu they're the packages `gcc-<triple>`, `binutils-<triple>` and `libc6-dev-<arch>-cross`. Taipan links with them, and without them it's `taipan: building for <rid> needs <triple>-gcc on PATH` (exit `2`), before anything is built.
  - **macOS:** Xcode's command-line tools build both architectures.
  - **Windows:** Visual Studio's C++ tools for the target architecture.
  - Otherwise the build fails as §15.1 says, `native build failed: …`.
- **The files** are named for the target's operating system: an executable `<name>` (`<name>.exe` for Windows), a native library `<name>.so`, `<name>.dylib` or `<name>.dll`, an extension module `<name>.abi3.so` or `<name>.pyd`. Taipan doesn't run what it builds for another platform.

### 15.5 Size options (M45)
- **`--invariant-globalization`** builds without culture data (ICU on Linux), which makes the executable smaller and needs no ICU on the machine that runs it, as .NET's `InvariantGlobalization` does. Taipan's own operations don't depend on culture (`str.upper`, number formatting and parsing, and ordering are the same either way), so a Taipan program behaves the same; only .NET APIs that use culture data (§13) see the invariant culture, and creating another culture raises a .NET `CultureNotFoundException`.
- **`--optimize-size`** has NativeAOT prefer smaller code to faster code (`OptimizationPreference=Size`).
- Each may be given with `--native`, `--library --native` or `--python-extension`, to `build`; elsewhere it's a usage error, as for `--runtime`.

### 15.6 Native libraries (M45)
`taipan build <file> --library --native -o <dir>` compiles a module into a native shared library with C functions, so C, and anything that calls C, can use it without .NET:

```python
# geometry.tpn
def area(width: float, height: float) -> float:
    return width * height

def label(name: str, sides: int) -> str:
    return f"{name} with {sides} sides"
```

```c
#include "geometry.h"
#include <stdio.h>

int main(void) {
    printf("%f\n", geometry_area(2.0, 3.0));
    char *text = geometry_label("square", 4);
    printf("%s\n", text);
    geometry_free(text);
}
```

- **The files** are `<dir>/<name>.so` (`.dylib` on macOS, `.dll` on Windows) and `<dir>/<name>.h`, a C header declaring the exports, where `<name>` is the main file's name without `.tpn` (or `taipan.json`'s `assembly`, §18.3). The library needs nothing beside it, no .NET runtime in particular. Its requirements, errors and platform are a native build's (§15.1, §15.4), and §15.2's restrictions (`TP0233`) apply. Its main file contains declarations only (`TP0222`), as a library's (§14.3).
- **What's exported:** the main file's own functions whose names don't start with `_` (§14.1), as C functions named `<module>_<function>`, where `<module>` is the main file's name without `.tpn` (always a C identifier, even when `taipan.json` names the file), with C types for Taipan's:

  | Taipan | C |
  |---|---|
  | `int` | `int64_t` |
  | `float` | `double` |
  | `bool` | `bool` (`<stdbool.h>`) |
  | `str` (a parameter) | `const char *`, UTF-8 and null-terminated, which the library only reads during the call |
  | `str` (a result) | `char *`, UTF-8 and null-terminated, newly allocated, which the caller frees with `<module>_free` |
  | no result | `void` |

  A function with any other parameter or result type, or a generic or async one, isn't exported: warning `TP0302`, with `'<T>' can't be passed to C`, `it's generic` or `it's async`. Its parameters are passed by position, all of them, so a default doesn't apply, and a function with `*args`, `**kwargs` or keyword-only parameters isn't exported either (`TP0302`, `it has '*args' or '**kwargs'` or `it has keyword-only parameters`). Constants, classes and enums aren't exported.
- **Initialization:** a module initializes when an exported function is first called, as §14.3 says.
- **Errors:** an exception can't cross into C. A Taipan exception, or a panic, escaping an exported function makes it return `0`, `0.0`, `false` or `NULL`, and sets the calling thread's last error, which **`const char *<module>_last_error(void)`** gives as `<Class>: <message>` (`ValueError: radius -1.0 is negative`), the same text a traceback ends with (§3.19). It's `NULL` after a call that returned normally, and stays valid until the next call into the library on that thread. Nothing is printed.
- **`void <module>_free(char *text)`** frees a string the library returned; freeing anything else, or a string twice, is undefined, as in C.
- `print` writes to the process's standard output, as C code does.
- A native library is built with `--library --native`, which before M45 was a usage error. `taipan check` and `taipan emit` take `--library --native` too, checking and emitting it as for a build.

### 15.7 Deferred
§11 lists what's deferred: Python in native programs, building for another operating system (which NativeAOT can't do), musl-based Linux, and a native library's exports beyond §15.6's. Before M45, building for another architecture, native libraries and size options were deferred too.

---

## 16. Language server (M24, M32)
`taipan lsp` is a Language Server Protocol (LSP 3.17) server for editors. It reports the compiler's diagnostics as you type, shows a name's type on hover, and goes to a name's definition, across modules too. Since M32 it also completes names, shows a call's signature, finds a name's references, renames across modules, and gives semantic tokens (§16.4–§16.7), and the repository has a VS Code extension that starts it (§16.8). Since M47 the extension is published, and Vim and Neovim have their own support (§16.12).

### 16.1 Protocol
- **Transport:** JSON-RPC 2.0 over stdin and stdout, each message framed by a `Content-Length` header, as the LSP specifies. `taipan lsp` takes no arguments but `--stdio`, which clients such as VS Code's pass and which names the only transport. It writes nothing else to stdout.
- **Lifecycle:** `initialize`, `initialized`, `shutdown` and `exit`. `exit` ends the server with code `0` after `shutdown`, and `1` without it, as the LSP specifies.
- **Capabilities** the server declares: full document sync with open and close notifications (`textDocumentSync: 1`), `hoverProvider` and `definitionProvider`; since M32, `completionProvider` (trigger character `.`), `signatureHelpProvider` (trigger characters `(` and `,`), `referencesProvider`, `renameProvider` (with `prepareProvider`), and `semanticTokensProvider` (`full` only, with §16.7's legend); since M44, `documentFormattingProvider` (§16.10) and `codeActionProvider` (`codeActionKinds: ["quickfix"]`, §16.11). Positions are in UTF-16 code units, the LSP's default; Taipan's own columns count code points (§9), and the server converts.
- **Documents** are `file://` URIs; others are ignored. Open documents' text comes from the client (`didOpen`, `didChange`), unsaved changes included, and any other file the server reads from disk.
- A request the server doesn't support is answered with JSON-RPC's `MethodNotFound` error (`-32601`); an unsupported notification is ignored. A request for a document that isn't open, or that can't be analyzed, gets a null result.

### 16.2 Diagnostics
- **Each open document is checked as the main file of a program** (§4.7), as `taipan check <file>` checks it: its imports are loaded from its directory, with `taipan.json` read there (§13.2). It's checked when it's opened and after every change, and when another open document it imports changes.
- **The diagnostics published** for a document (`textDocument/publishDiagnostics`) are those `taipan check` would report in that file, in its order:
  - `range` runs from the diagnostic's start to its end; its start is the line and column `taipan check` prints.
  - `severity` is `1` (error) or `2` (warning), `code` is the diagnostic's code (`TP0200`), and `source` is `taipan`.
  - `message` is the message, followed by `help: <hint>` on its own line when there's help.
- Paths in messages, such as TP0221's `looked for <path>`, are absolute, since the server checks a document by its absolute path.
- Diagnostics in an imported module are published for that module when it's open itself, since each document is checked as its own main file. A module checked as a main file isn't held to §4.7's declarations-only rule (`TP0222`).
- A problem `taipan check` reports as `taipan: <message>` with exit code `2`, such as a malformed `taipan.json`, is one error at the document's start, with that message and no code.
- Closing a document publishes an empty list for it.

### 16.3 Hover and definition
- **Hover** (`textDocument/hover`) on a name, where it's defined or used, shows what it names as a `taipan` code block, with types as diagnostics write them (§9):

  | Name | Hover |
  |---|---|
  | variable | `let total: float`, `var count: int`, or `items: list[Shape]` for a parameter |
  | constant | `const LIMIT: int` |
  | function | `def scale(p: Point, by: float = …) -> Point`, or `async def fetch(url: str) -> str` (M33), with type parameters (`def first[T](xs: list[T]) -> T?`) and parameter kinds as written (§4.2): `def log(msg: str, /, *args: int, sep: str = …, **kwargs: str)` (M32) |
  | method | `def Point.moved(self, by: float = …) -> Point` |
  | field | `Point.x: float` |
  | class, enum | `class Point`, `@dataclass class Point`, `class Box[T]`, `enum Shape` |
  | variant | `Shape.Circle(r: float)` |
  | module | `module shapes` |
  | type alias | `type Grid = list[list[int]]` |
  | .NET type or member | its C# signature, after `.NET`: `.NET System.Text.StringBuilder.Append(string?)` |

  A variable whose type was inferred shows the inferred type, so `let n = len(xs)` shows `let n: int`. Elsewhere hover gives a null result.
- **Go to definition** (`textDocument/definition`) on a name gives the location of the name where it's defined: a local's or parameter's declaration, or a function, class, enum, variant, field, method, constant or type alias in its file. A name imported from another module (`from shapes import area`, or `shapes.area`) goes to `shapes.tpn`, and a module's name in `import shapes` goes to the start of its file. Built-in names, and Python and .NET names, have no definition here, so the result is null.
- Both need the document to parse (§9's stage gating). With type errors, they work wherever names resolved.

### 16.4 Completion (M32)
- **Partial code.** Completion and signature help (§16.5) are asked for while code is half-typed, as in `p.` or `scale(p, `, which doesn't parse. For these two requests only, the server analyzes a copy of the document patched at the cursor, and leaves the document, its diagnostics and its other requests alone:
  - The cursor's logical line (§1.2) is cut at the cursor, and the name being typed there, if any, is replaced with a placeholder name.
  - The brackets left open before the cursor on that logical line are closed, innermost first.
  - If the line starts a block (`if`, `elif`, `else`, `while`, `for`, `def`, `class`, `enum`, `match`, `try`, `except` or `finally`, or a decorator's `def` or `class`), it gets its `:`. If the next line isn't indented more than it, a `pass` body is added too.
  - The rest of the document is unchanged. If the copy still doesn't parse, because of an error elsewhere, completion gives an empty list and signature help a null result.
- **What's offered depends on where the cursor is:**

  | Cursor | Offered |
  |---|---|
  | in a comment, or a string's text (not an f-string's hole) | nothing |
  | after `import` or `from`, or after a comma in `import a, ` | the modules the document may import: the other `.tpn` files in its directory (§4.7), and `python` and `dotnet` |
  | after `from dotnet.` or `import dotnet.`, and after each further `.` of a namespace (M44) | the namespaces inside it that the program's assemblies define (§13.2): `System`, `Microsoft`, …, then `IO`, `Text`, … after `dotnet.System.` |
  | after `from python import`, or after a comma there (M44) | the modules the Python §10.6 finds can import: its built-in modules and those `pkgutil.iter_modules()` lists, the standard library's and installed packages', except names starting with `_`; nothing without Python |
  | after `from dotnet.<ns> import`, or after a comma there (M44) | the namespace's public types, each name once whatever its arities |
  | after `from m import`, or after a comma there | the names `m` exports (§4.7), with an enum's variants |
  | after `.`, on a value of a class | its fields, properties and methods, inherited ones included, and its `ClassVar` attributes, but not static or class methods, or special methods (§4.8) |
  | after `.`, on a class's name | its static and class methods and `ClassVar` attributes |
  | after `.`, on an enum's name | its variants |
  | after `.`, on a module | the names it exports, as after `from m import` |
  | after `.`, on a `list`, `dict` or `str` | §6's methods for it |
  | after `.`, on a .NET value or type | its public instance members, or a type's public static members (§13.4) |
  | after `.`, on anything else (a `py` value, a tuple, a number) | nothing |
  | in a type (after `:` in a binding or parameter, after `->`, inside a type's `[…]`, after `type X =`) | the types in scope: built-in types (§3), classes, enums, protocols, type aliases, type parameters, imported .NET types, and modules (for `m.Point`) |
  | anywhere else a name may go | the names in scope at the cursor, then keywords: every keyword that starts a statement at a statement's start, and `None`, `True`, `False`, `not`, `lambda`, `match` and `await` (M33) inside an expression |

  The names in scope are the locals and parameters of the enclosing functions bound before the cursor, the file's own definitions and imports, and the built-in functions, types and exceptions (§6).
- **Items.** Each item's `label` is the name, its `detail` is what hover would show for it (§16.3), and its `kind` is LSP's for what it names:

  | Name | `kind` |
  |---|---|
  | method, static or class method | `Method` (2) |
  | function, nested function, built-in function | `Function` (3) |
  | field, `ClassVar` attribute | `Field` (5) |
  | `let`, `var`, parameter | `Variable` (6) |
  | class, exception, type alias, built-in type, .NET class | `Class` (7) |
  | protocol, .NET interface | `Interface` (8) |
  | module, a Python module (§10), a .NET namespace (M44) | `Module` (9) |
  | property, .NET property | `Property` (10) |
  | enum, .NET enum | `Enum` (13) |
  | keyword | `Keyword` (14) |
  | variant | `EnumMember` (20) |
  | `const` | `Constant` (21) |
  | .NET struct | `Struct` (22) |
  | type parameter | `TypeParameter` (25) |

- **Order.** The server sends every candidate, and the client filters them by what's typed (`isIncomplete` is `false`). `sortText` puts the innermost scope's names first, then the file's own and imported names, then built-ins, then keywords, each group by name.

### 16.5 Signature help (M32)
- **Signature help** (`textDocument/signatureHelp`) inside a call's parentheses, after `(` or a `,`, shows the signature of the innermost call around the cursor, analyzing the patched copy of §16.4:
  - **A function or method** shows hover's text (§16.3), `def scale(p: Point, by: float = …) -> Point`. `self` and `cls` are in the label but aren't parameters, since no argument fills them.
  - **A class's constructor** shows the class and `__init__`'s parameters, or a dataclass's fields: `Point(x: float, y: float = …)`.
  - **A variant** shows hover's text, `Shape.Circle(r: float)`.
  - **A function value** shows its type with numbered parameters: `f(arg0: int, arg1: str) -> bool` for an `f: Callable[[int, str], bool]`.
  - **A .NET method or constructor** shows each overload as a signature, in C# form as hover writes it, with `activeSignature` the overload the call binds to (§13.5), or the first when it binds to none.
  - **Built-in functions, `super()` and Python callees** give a null result.
- **`parameters`** are each parameter's text in the label, as `[start, end]` offsets, and **`activeParameter`** is the one the argument at the cursor fills as §4.2 binds it: a positional argument the next positional parameter, then `*args`; a keyword argument the parameter it names, then `**kwargs`. An argument that fills none (one too many, or an unpacked `*xs`) gives the number of parameters, which highlights none.

### 16.6 References and rename (M32)
- **The files searched** are the module root: every `.tpn` file in the document's directory, the directory its imports load from (§4.7), whether it's open or not. Each is checked as a main file (§16.2), using open documents' text and reading the rest from disk.
- **The same name.** Since each file is checked on its own, two occurrences name the same thing when what they name is defined at the same place: a module, function, class, enum, variant, field, method, property, `ClassVar` attribute, constant, type alias, type parameter, local or parameter. A module is defined by its file.
  - **Methods** that override one another (§4.3) count as one name, so a base class's method, its overrides, and calls of any of them are found together.
  - **Keyword arguments** name their parameters, so `scale(p, by=2.0)` is a reference of `scale`'s `by`. A dataclass's constructor keywords (`Point(x=1.0)`) and class patterns' fields (`Point(x=0.0)`, §5.1) are references of the fields they name.
  - **An alias** (`from shapes import area as surface`, `import geometry as g`) names what it imports, so references of `area` include the uses of `surface`.
- **Find references** (`textDocument/references`) on a name gives every occurrence of it in the module root, with its definition when `context.includeDeclaration` is `true`, ordered by file path and then position. Built-in, Python and .NET names give a null result, and so does a position that isn't on a name, or a file that doesn't parse.
- **Rename** (`textDocument/rename`) on a name gives a `WorkspaceEdit` whose `changes` replace every reference that's spelled as the name, in every file that has one, with the new name.
  - An alias isn't spelled as the name it imports, so renaming `area` edits the `area` in `from shapes import area as surface` but not the uses of `surface`.
  - Renaming an alias itself edits only that alias and its uses, in its own file.
  - Renaming a name to itself gives no changes.
- **`textDocument/prepareRename`** gives the name's range and its text as the `placeholder`, the error rename would give for a name it refuses (below), or a null result away from a name.
- **What rename refuses.** A refusal is a JSON-RPC error, LSP's `RequestFailed` (`-32803`), with one of these messages, and makes no changes:

  | Refused | Message |
  |---|---|
  | a built-in name (§6) | `cannot rename the built-in '<x>'` |
  | a .NET name | `cannot rename the .NET name '<x>'` |
  | a Python name (§10) | `cannot rename the Python name '<x>'` |
  | a module, before M44 | `cannot rename the module '<x>'; rename its file instead` |
  | a module, when the editor can't rename files (M44, below) | `cannot rename the module '<x>': the editor can't rename files` |
  | a module to a name that another file in its directory has (M44) | `'<new>.tpn' already exists` |
  | a built-in module (`sys`, `asyncio`, …) | `cannot rename the built-in '<x>'` |
  | a special method (§4.8), `__init__` included | `cannot rename the special method '<x>'` |
  | a new name that isn't an identifier (§1.3), or is `_` | `'<new>' is not a valid name` |
  | a new name that's a keyword (`type` isn't) | `'<new>' is a keyword` |
  | a new name that's a special method's (`__x__`) | `cannot rename to the special name '<new>'` |
  | a file in the module root that doesn't parse, since its references can't be found | `cannot rename while <path> has syntax errors` |
  | a rename that would add an error: one more error with some code in some file of the module root, when it's checked again with the changes | `renaming '<x>' to '<new>' would cause an error: <path>:<line>:<col>: <code>: <message>`, naming the first such error |

  The last row catches clashes (`TP0214`), shadowing (`TP0207`), and a class that stops satisfying a protocol (`TP0225`), among others, so a rename never turns a program that checked into one that doesn't. Paths in these messages are relative to the document's directory.

- **Renaming a module (M44)** renames its file too. On a module's name, in `import shapes`, `from shapes import area` or `shapes.area`, rename gives a `WorkspaceEdit` with `documentChanges`: a `RenameFile` from `shapes.tpn` to `<new>.tpn` in the same directory, then the text edits of every reference spelled as the name, in every file of the module root, the renamed file's own references included under its new URI. It needs a client that declares `workspace.workspaceEdit.documentChanges` and the `rename` resource operation; otherwise it's refused (above). An alias (`import shapes as s`) is renamed as before, as a name of its file. The main file has no module name of its own, so renaming starts at a reference to it from another file.

### 16.7 Semantic tokens (M32)
- **`textDocument/semanticTokens/full`** gives a token for each name the binder resolved in the document (§16.3), in LSP's relative encoding, from the document's latest check. It needs the document to parse, and gives a null result otherwise. Keywords, literals, operators and comments get no semantic tokens; the grammar (§16.8) colors them. Names that didn't resolve, and Python names, get no token.
- **The legend's token types**, in order: `namespace`, `class`, `enum`, `interface`, `struct`, `typeParameter`, `type`, `parameter`, `variable`, `property`, `enumMember`, `function`, `method`. **Its modifiers:** `declaration`, `readonly`, `static`, `defaultLibrary`.

  | Name | Type | Modifiers |
  |---|---|---|
  | module, .NET namespace | `namespace` | |
  | class, exception, .NET class | `class` | |
  | enum, .NET enum | `enum` | |
  | protocol, .NET interface | `interface` | |
  | .NET struct | `struct` | |
  | type parameter | `typeParameter` | |
  | type alias | `type` | |
  | built-in type (`int`, `list`) | `type` | `defaultLibrary` |
  | parameter, `self`, `cls` | `parameter` | |
  | `let`, a loop variable, a name a pattern binds | `variable` | `readonly` |
  | `var` | `variable` | |
  | `const` | `variable` | `readonly`, `static` |
  | field, property, .NET property or field | `property` | |
  | `ClassVar` attribute | `property` | `static` |
  | variant | `enumMember` | |
  | function | `function` | |
  | built-in function | `function` | `defaultLibrary` |
  | method, .NET method | `method` | `static` for a static or class method |

  A name where it's defined also has `declaration`.

### 16.8 The VS Code extension (M32)
- **Where.** The extension lives in `editors/vscode`, in plain JavaScript with no build step. Its one runtime dependency is `vscode-languageclient`, Microsoft's LSP client. Its development dependencies are `@vscode/vsce`, which packages it and publishes it to the Visual Studio Marketplace, `ovsx`, which publishes it to Open VSX (M47), and `vscode-textmate` and `vscode-oniguruma`, which its grammar test runs on, the engine VS Code itself uses.
- **Its identity** (M47): the extension `taipan`, by the publisher `discover-edge`, so its ID is `discover-edge.taipan`. Its version is the compiler's, `<Version>` in `Directory.Build.props` (§12.2). Its icon is the Taipan logo, `assets/icon.png`, copied to `editors/vscode/icon.png`, since a package can't include files outside its directory.
- **What it contributes:**
  - the language `taipan`, for `.tpn` files;
  - a language configuration: `#` comments, bracket pairs, auto-closing brackets and quotes, and indenting after a line ending in `:`;
  - the TextMate grammar `source.taipan` (below);
  - the setting `taipan.path`, the `taipan` command to start, `"taipan"` by default (found on `PATH`);
  - since M47, two commands, in the command palette when a Taipan file is active:
    - **Taipan: Run File** (`taipan.runFile`), also a run button in a Taipan editor's title bar. It saves the active Taipan file and runs `<taipan.path> run <file>` in a new terminal named `Taipan`, replacing the one it opened before. The terminal's directory is the workspace folder holding the file, or the file's own directory outside a workspace. The process runs as the terminal's own, not through a shell, so no path needs quoting; its output stays in the terminal after it ends.
    - **Taipan: Restart Language Server** (`taipan.restartServer`), which stops the server and starts it again, finding `taipan` again first (below).
- **It starts** `<taipan.path> lsp --stdio` when a `.tpn` file is opened, as the client for every Taipan document, and stops it when VS Code closes. Since M47 it also restarts it when `taipan.path` changes.
- **When `taipan` can't be found** (M47). Before starting the server, the extension runs `<taipan.path> --version`. If the command doesn't exist, it doesn't start the server, and shows the error `Taipan: '<taipan.path>' wasn't found, so the language server can't start. Install Taipan with 'dotnet tool install -g TaipanLang', or set 'taipan.path'.` with two buttons:
  - **Install**, which runs `dotnet tool install -g TaipanLang` in a new terminal;
  - **Open Settings**, which opens the settings at `taipan.path`.
  After installing, Restart Language Server starts it. Any other failure to start is reported by the LSP client, as before.
- **It packages** with `vsce package` into `taipan-<version>.vsix`, with the extension's own files, its icon and its runtime dependency only.
- **It's published** (M47) by `.github/workflows/publish.yml` on a version tag, with the packages (§12.2): the `.vsix` is published to the Visual Studio Marketplace with `vsce publish --oidc` and to Open VSX with `ovsx publish --trusted-publishing`, the registries' trusted publishing, so no token is stored. Open VSX is the registry of VS Code's other builds, such as VSCodium and Cursor. A version already in a registry is skipped, so a failed run can be run again.
- **Its scripted test** (M47), `test/extension.test.mjs`, runs `extension.js` under Node with stand-ins for the `vscode` module and the LSP client, and checks that activating it registers both commands and starts the server with `lsp --stdio`; that Run File saves the file and opens a terminal running `<taipan.path> run <file>` in the file's workspace folder; that a `taipan.path` that doesn't exist gives the message above and starts no server; and that Restart Language Server stops the client and starts a new one.
- **The grammar** colors code without the server, so a file is colored as soon as it opens. It gives these scopes, each ending in `.taipan`:

  | Text | Scope |
  |---|---|
  | `# …` | `comment.line.number-sign` |
  | `"…"`, `'…'` | `string.quoted.double`, `string.quoted.single`, with escapes `constant.character.escape` |
  | `f"…"` | `string.interpolated`, with `{{` and `}}` as escapes; each hole `meta.interpolation`, with its braces `punctuation.section.interpolation.begin` and `.end`, its conversion (`!r`) and format spec (`:>8`) `storage.type.format`, and the expression inside colored as code |
  | integers, floats | `constant.numeric.integer`, `constant.numeric.float` |
  | `True`, `False`, `None` | `constant.language` |
  | `if elif else while for match return pass break continue try except finally raise yield await with assert` (`with`: M37; `assert`: M44), and a `for` header's `in` | `keyword.control.flow` |
  | `import from as` | `keyword.control.import` |
  | `and or not is in some` | `keyword.operator.logical` |
  | `def`, `lambda`, `async` | `storage.type.function` |
  | `class`, `enum` | `storage.type.class` |
  | `let var const`, and `type` starting an alias | `storage.modifier.declaration` |
  | `del` | `keyword.other.del` |
  | the name after `def` | `entity.name.function` |
  | the name after `class` or `enum` | `entity.name.type` |
  | `@name` | `entity.name.function.decorator` |
  | `self`, `cls` | `variable.language.special` |
  | §6's built-in functions; `int`, `float`, `str`, `list` and `set` only before `(`, since they're types otherwise | `support.function.builtin` |
  | built-in types (`int float str bool list dict set tuple Iterator Iterable Callable Awaitable TextIO`) | `support.type.builtin` |
  | `+ - * / // % ** ??`, and `\| & ^` (M35) | `keyword.operator.arithmetic` |
  | `== != < <= > >=` | `keyword.operator.comparison` |
  | `= += -= *= /= //= %= **=` | `keyword.operator.assignment` |
  | `->` | `punctuation.separator.annotation.result` |

- **The golden token list.** `editors/vscode/test/sample.tpn` exercises every row above, and `sample.tokens` lists the tokens the grammar gives it, one per line: `<line>:<column> <text> <scopes>`, with columns from 1, the text as a JSON string, and the token's scopes after `source.taipan`, space-separated (`-` when there are none). Tokens of only whitespace are left out.

### 16.9 Deferred
§11 lists what's deferred: code actions beyond §16.11's, range formatting, incremental document sync, workspace-wide diagnostics, semantic token deltas and ranges, signature help for built-ins, references to built-in, Python and .NET names, updating imports when the editor renames a file, editors' own support beyond VS Code, Vim and Neovim (§16.12; other editors use the server through their own LSP clients), a tree-sitter grammar, and showing `taipan test`'s tests in VS Code's Test Explorer. Before M47, Vim and Neovim had no support of their own, and the extension was built by hand. Before M44, formatting, code actions, completing .NET namespaces and Python modules' names, and renaming modules were deferred too.

### 16.10 Formatting (M44)
- **`textDocument/formatting`** formats the document as `taipan fmt` would (§19), from the client's text, unsaved changes included. The result is one `TextEdit` replacing the whole document, or an empty list when it's formatted already. A document with lexing or parsing errors isn't formatted, and the result is an empty list.
- The request's `FormattingOptions` (`tabSize`, `insertSpaces`) are ignored, since the style has no options.

### 16.11 Code actions (M44)
- **`textDocument/codeAction`** gives quick fixes (kind `quickfix`) for the document's latest diagnostics that the request's range touches, each with the diagnostic it fixes (`diagnostics`) and its change as a `WorkspaceEdit` (`edit`). Other diagnostics, and requests for other kinds only, get an empty list.
- **The diagnostics with fixes:**

  | Diagnostic | Fix | Title |
  |---|---|---|
  | `TP0201` `undefined name '<x>'`, where another file in the module root (§16.6) exports `<x>` (§4.7) | add `from <m> import <x>`, one for each such module | `Import '<x>' from '<m>'` |
  | `TP0201` `undefined name '<x>'`, where `<x>` is a module in the module root or a built-in module (`sys`, `os`, `types`, `asyncio`) | add `import <x>` | `Import '<x>'` |
  | `TP0227` `'<m>' overrides '<B>.<m>'; mark it '@override'` | add `@override` on its own line before the method's `def`, with the `def`'s indentation, after any decorators | `Mark '<m>' '@override'` |
  | `TP0227` `'<m>' does not override a method of …` | remove the `@override` line | `Remove '@override' from '<m>'` |
  | `TP0200` with help `convert it with 'list(…)' first` (an iterator indexed, sliced or measured, §3.13) | wrap the iterator's expression in `list(…)` | `Convert to 'list(…)'` |
- **Where an import goes:** after the file's last top-level `import` or `from` line, or before its first line of code when it has none, after any comments at the start of the file.
- Each fix makes exactly that change, and nothing else: it doesn't reformat (§19).

### 16.12 Vim and Neovim (M47)
- **Where.** `editors/vim` is a Vim runtime directory, which works in Vim 9 and in Neovim. Putting it on the `runtimepath`, as a package or with a plugin manager, gives:
  - `ftdetect/taipan.vim`: the filetype `taipan` for `.tpn` files;
  - `syntax/taipan.vim`: highlighting (below);
  - `ftplugin/taipan.vim`: `#` comments (`comments`, `commentstring`), 4-space indenting with spaces (`expandtab`, `shiftwidth`, `softtabstop`), and `suffixesadd` `.tpn`, so `gf` on a module's name opens its file;
  - `indent/taipan.vim`: indenting (below);
  - `lsp/taipan.lua`: Neovim's configuration for the language server, which Neovim 0.11 and later read for `vim.lsp.enable('taipan')`. It starts `taipan lsp --stdio` for the filetype `taipan`, with the project's root at the nearest `taipan.json` or `.git`.
  Vim has no LSP client of its own. With a plugin that adds one, the server is set up the same way: the command `taipan lsp --stdio`, the filetype `taipan`, and the root markers `taipan.json` and `.git`.
- **The syntax** colors what the TextMate grammar colors (§16.8), with the same rules, in these groups, each linked to Vim's standard group:

  | Text | Group | Linked to |
  |---|---|---|
  | `# …` | `taipanComment` | `Comment` |
  | `"…"`, `'…'` | `taipanString` | `String` |
  | escapes in strings | `taipanEscape` | `SpecialChar` |
  | `{{` and `}}` in f-strings | `taipanFBraces` | `SpecialChar` |
  | `f"…"`, `f'…'` | `taipanFString` | `String` |
  | an f-string hole's braces | `taipanHoleBrace` | `Delimiter` |
  | the code in a hole | `taipanHole` | nothing: colored as code |
  | a hole's conversion (`!r`) and format spec (`:>8`) | `taipanFormat` | `Special` |
  | integers | `taipanNumber` | `Number` |
  | floats | `taipanFloat` | `Float` |
  | `True`, `False`, `None` | `taipanConstant` | `Constant` |
  | the flow keywords, and a `for` header's `in` | `taipanStatement` | `Statement` |
  | `import from as` | `taipanImport` | `Include` |
  | `and or not is in some` | `taipanOperatorWord` | `Operator` |
  | `def`, `lambda`, `async` | `taipanFunctionKeyword` | `Keyword` |
  | `class`, `enum` | `taipanClassKeyword` | `Structure` |
  | `let var const`, and `type` starting an alias | `taipanDeclaration` | `StorageClass` |
  | `del` | `taipanDel` | `Keyword` |
  | the name after `def` | `taipanFunctionName` | `Function` |
  | the name after `class` or `enum` | `taipanTypeName` | `Type` |
  | `@name` | `taipanDecorator` | `PreProc` |
  | `self`, `cls` | `taipanSelf` | `Special` |
  | built-in functions, with `int`, `float`, `str`, `list` and `set` only before `(` | `taipanBuiltin` | `Function` |
  | built-in types | `taipanBuiltinType` | `Type` |
  | arithmetic operators | `taipanArithmetic` | `Operator` |
  | comparison operators | `taipanComparison` | `Operator` |
  | assignment operators | `taipanAssignment` | `Operator` |
  | `->` | `taipanArrow` | `Delimiter` |

  The keyword lists, built-in names and operators are §16.8's.
- **The golden syntax list.** `editors/vim/test/sample.syntax` lists the groups Vim gives the TextMate grammar's sample, `editors/vscode/test/sample.tpn`, one token per line: `<line>:<column> <text> <group>`, with columns from 1, the text as a JSON string, and the innermost group, after transparent groups are seen through (`-` when there's none). A token is a run of characters with the same group, and a run with no group is also split at spaces. Tokens of only whitespace are left out. `editors/vim/test/syntax.vim` writes the list, in Vim started with no configuration but `editors/vim` on its `runtimepath`.
- **Indenting.** A new line is indented:
  - one level (`shiftwidth`) more than the line above, when that line ends in `:`, ignoring a comment after it;
  - one level less, when the line above is a `return`, `pass`, `break`, `continue` or `raise` statement;
  - as the line above, otherwise.
  A line starting with `elif`, `else`, `except` or `finally` is indented one level less than the line above when its `:` is typed, unless it's already indented less. Other lines keep their indentation when Vim reindents them (with `=`), since the end of a block can't be told from the code above it. Lines inside open brackets aren't treated specially.
- **The indenting test.** `editors/vim/test/indent.vim` types each line of `editors/vim/test/indent.tpn` into an empty buffer without its indentation, as a person would, ending each with Enter, and the buffer must then be the file.
- **The Neovim test.** `editors/vim/test/lsp.lua` runs in Neovim with no configuration but `editors/vim` on its `runtimepath`. It enables `taipan` with `vim.lsp.enable`, changing only its command (to the `taipan` under test), opens a file and waits for its diagnostics. It writes each as `<line>:<column> <severity> <code>: <message's first line>`, and these must be the diagnostics `taipan check` reports for the file, in its order.

---

## 17. CPython extension modules (M25)
`taipan build <file> --python-extension -o <dir>` compiles a Taipan module into a native CPython extension module, through NativeAOT (§15), so Python code can import it and call its functions. This is interop in the other direction from §10.

```python
# geometry.tpn
const PI = 3.14159

class NegativeRadius(Exception):
    pass

def area(r: float) -> float:
    if r < 0.0:
        raise NegativeRadius(f"radius {r} is negative")
    return PI * r * r
```

```
$ taipan build geometry.tpn --python-extension -o out
built out/geometry.abi3.so
$ cd out && python3 -c "import geometry; print(geometry.area(2.0))"
12.56636
```

### 17.1 Building
- **The module** is `<dir>/<name>.abi3.so` (`<name>.pyd` on Windows), where `<name>` is the file's name without `.tpn`, which is also the Python module's name. Its imports are compiled in, but only its own definitions are exported (§17.2).
- **Python versions:** the module uses only CPython's stable ABI (the limited API of Python 3.10), so one build imports into any CPython from 3.10 on, for the platform it was built for. Python isn't needed to build it. Free-threaded builds of CPython (`python3.13t`) can't load stable-ABI modules, and aren't supported.
- **Building** is a NativeAOT build as §15.1 says, of a shared library rather than an executable, with the same requirements (the .NET SDK and the native toolchain), errors (`taipan: …`, exit `2`) and platform. Nothing beside the module is needed to import it; in particular, no .NET runtime.
- **Restrictions:** an extension module runs natively, so §15.2 applies (`TP0233`): it can't use Python interop (§10) itself, or .NET members that need run-time code generation. Since M45 it may hold Python objects it's given, as `py` values (§17.6), but not import Python modules or operate on them. Like a library's main file (§14.3), it contains declarations only, so a top-level statement is `TP0222`.
- `--python-extension` can't be combined with `--library` or `--native` (a usage error). `taipan check <file> --python-extension` and `taipan emit <file> --python-extension` check and emit the module as for a build, with its diagnostics.

### 17.2 What's exported
The module file's own definitions whose names don't start with `_` (§14.1), as module attributes:
- **Functions** whose parameter and result types are all *convertible* (§10.3), other than `py`: `int`, `float`, `bool`, `str`, and lists, dicts, tuples and optionals of those, or no result. Each is a Python built-in function (`geometry.area`).
- **Constants** of convertible types, as attributes holding their values (`geometry.PI`), set when the module is imported.
- **Exception classes** (§3.10) defined in the file, as Python exception classes named `<name>.<Class>` (`geometry.NegativeRadius`). Each derives from the Python class of its Taipan base: the module's own class for an exported exception class, or the built-in of the same name (§17.4), so `except ValueError` catches a Taipan class deriving from `ValueError`. A base that isn't exported (`_Base`) is skipped for its nearest exported or built-in ancestor.
- **Not exported (`TP0302`):** a function or constant that uses a type that isn't convertible, such as a class, an enum or a function type, is warning `TP0302`, with `'<T>' can't be passed to Python` as the reason, as is a generic function (`it's generic`) and, since M33, an async function (`it's async`). Before M45, other classes and enums weren't exported; since M45 they're Python types (§17.5), and exported classes, enums and function types are convertible (§17.5, §17.6).

### 17.3 Calls and conversions
- **Arguments** are passed by position or by keyword, using the Taipan parameter names, and a parameter with a default may be left out; its default is evaluated at each call, as in Taipan (§4.2). Since M30, positional-only and keyword-only parameters, `*args` and `**kwargs` are Python's, so Python callers bind arguments as Taipan does (§4.2), and each collected argument converts to the element type, as a list's or dict's would. Python's own errors for a wrong call are `TypeError`s with Python's messages: `area() takes 1 positional argument but 2 were given`, `area() got an unexpected keyword argument 'radius'`, `area() missing required argument 'r'`, `area() got multiple values for argument 'r'`, and since M30 `area() got some positional-only arguments passed as keyword arguments: 'r'` and `area() missing required keyword-only argument 'k'`. A keyword-only parameter passed by position is one too many positional arguments: `scale() takes 1 positional argument but 2 were given`.
- **Each argument converts** from Python to its parameter's Taipan type:

  | Taipan | Python argument |
  |---|---|
  | `int` | an `int` (Python's `bool` is an `int`, so `True` is `1`); outside Taipan's 64-bit range, `OverflowError` |
  | `float` | a `float`, or an `int`, which widens |
  | `bool` | a `bool` |
  | `str` | a `str` |
  | `T?` | `None`, or a `T` |
  | `list[T]` | a `list` whose elements convert to `T`, copied |
  | `dict[K, V]` | a `dict` whose keys and values convert, copied in order |
  | `tuple[A, B, …]` | a `tuple` of that many elements, each converting |

  Anything else is a `TypeError`: `area() argument 'r' must be float, not str`, naming the Taipan type and the Python type of the value that doesn't convert (`must be list[float], not str` for a list holding a `str`).
- **Results** convert from Taipan to Python as §10.3 says: `int`, `float`, `bool` and `str` to their Python types, lists to new lists, dicts to new dicts, tuples to tuples, and `None` for a `None` optional or a function with no result.
- Calls run while holding Python's GIL, which Taipan code doesn't release.

### 17.4 Exceptions
A Taipan exception raised by an exported function, or while the module's constants are evaluated, raises a Python exception whose argument is the Taipan message, so `str(e)` is the message (for `KeyError`, Python quotes it: `str(KeyError("x"))` is `'x'`), and `e.args` is `(message,)`:
- Taipan's built-in exception classes raise the Python built-ins of the same names: `Exception`, `ArithmeticError`, `ZeroDivisionError`, `OverflowError`, `LookupError`, `IndexError`, `KeyError`, `ValueError`, `TypeError`, `RuntimeError`. Integer overflow is `OverflowError("integer overflow")`.
- A user exception class raises the module's Python class for it, or for its nearest exported ancestor (§17.2).
- A .NET exception raises `RuntimeError` with `.NET <class>: <message>`, as an uncaught one panics (§13.6).
- An exception while the module is imported propagates from Python's `import`.

`print` writes to the process's standard output, as C code does, which may interleave with Python's own buffered `sys.stdout` differently than Python's `print` would.

### 17.5 Classes and enums as Python types (M45)
```python
# shapes.tpn
@dataclass
class Point:
    x: float
    y: float = 0.0

    def moved(self, by: float) -> Point:
        return Point(self.x + by, self.y)

enum Shape:
    Circle(r: float)
    Empty
```

```
>>> import shapes
>>> p = shapes.Point(1.0)
>>> p.moved(2.0)
Point(x=3.0, y=0.0)
>>> p.x = 5.0; p.x
5.0
>>> shapes.Shape.Circle(2.0).r, shapes.Shape.Empty.name
(2.0, 'Empty')
```

- **Which:** the module file's own classes and enums whose names don't start with `_`, that aren't generic, protocols or exception classes (which §17.2 exports already), are Python types `<module>.<Name>`, attributes of the module. A generic class or enum is warning `TP0302`, `it's generic`.
- **A class** is a Python type whose instances each hold a Taipan object:
  - **Calling the type** constructs one, as Taipan's construction does (§4.3), with the arguments bound as a function's are (§17.3): `__init__`'s parameters, or a dataclass's fields. An abstract class's type can't be called (`TypeError`, `cannot create '<Class>' instances`).
  - **Fields** are attributes, converting as arguments and results do, and assigning one assigns the field: a value that doesn't convert is a `TypeError`, `'<Class>.<f>' must be <T>, not <type>`. A frozen dataclass's fields (§4.9), and properties without setters, are read-only, so assigning one is Python's `AttributeError` for a read-only attribute. Properties are attributes too.
  - **Instance methods** are methods of the type, called with arguments bound as a function's are (§17.3); a method whose parameters or result don't convert isn't on the type, and is warning `TP0302` at its name. Fields, properties and methods whose names start with `_` aren't on it.
  - **`str()` and `repr()`** give Taipan's display and `repr` (§4.8); **`==` and `!=`** compare as Taipan's `==` does (§4.8), giving `NotImplemented` for an object of another Python type; and **`hash()`** is Taipan's `hash()` for a hashable class, while an unhashable one has `__hash__` set to `None`, as in Python.
  - **Inheritance:** a class's Python type derives from its base's, when the base is exported too, so `isinstance(dog, zoo.Animal)` is true and a `Dog` is accepted where an `Animal` is. A Python class can't derive from it: Python's `TypeError` at the `class` statement, or, for a class with Taipan subclasses, whose type must accept them, `TypeError`, `a Python class can't derive from a Taipan class`, when it's instantiated. Its other special methods (§4.8) aren't Python's.
- **An enum** is a Python type whose instances hold its values:
  - A variant without fields is a class attribute holding its value (`Shape.Empty`). A variant with fields is a class attribute that's called to make one, with its fields as arguments, bound as a function's are (`Shape.Circle(2.0)`, `Shape.Circle(r=2.0)`).
  - A value has `name`, its variant's name, and its fields as read-only attributes; a field its variant doesn't have is `AttributeError`, `'<Enum>' value <Variant> has no attribute '<f>'`. `str()`, `repr()`, `==` and `hash()` are Taipan's, as for a class.
- **Passing them:** an exported class or enum is convertible (§10.3, §17.2). A parameter of its type takes an instance of its Python type, or of a type deriving from it, and gives the Taipan function the very object it holds, so changes are shared: anything else is a `TypeError`, as for any argument. A result, field or element of its type gives Python an instance holding the Taipan object; the same object given to Python twice may be two Python objects, which `==` finds equal and `is` doesn't. An unexported class (`_Base`) in a signature still keeps a function from being exported (`TP0302`).

### 17.6 Python objects as arguments (M45)
- **A function type** is convertible as a parameter (§17.2): `def apply(f: Callable[[int], int], x: int) -> int` takes any Python callable. Calling it from Taipan calls the Python object with the arguments converted to Python, and converts what it returns to the function type's result, raising `TypeError` if it doesn't convert. A Python exception the call raises propagates through the Taipan code as a `PythonError` (§3.10), which an `except PythonError` may catch, and if it isn't caught, back into Python as the original exception. Its parameter and result types must be convertible both ways (`TP0302` otherwise). A function value as a result or a field isn't convertible.
- **`py`** in an extension module is a Python object it's been given: a parameter, result, field or element of type `py` takes or gives any Python object, unconverted, which Taipan keeps alive while it holds it. Inside the extension, a `py` value can be passed, stored, returned, and displayed (`print`, `str()`, f-strings), which uses Python's `str()`; anything else on it (attributes, calls, indexing, operators, `as`, `isinstance`) is `TP0233`, `an extension module can only pass Python objects along`, with help `do this in Python, and pass the result`.
- Calls into Python and the conversions run while holding the GIL, which the extension never releases.

### 17.7 Deferred
§11 lists what's deferred: Python subclasses of Taipan classes, special methods beyond `__str__`, `__repr__`, `__eq__` and `__hash__`, static and class methods, operations on Python objects inside an extension module, generic functions and classes, and builds for one exact Python version. Before M45, classes and enums as Python types, and Python functions and objects as arguments, were deferred too. Free-threaded CPython isn't planned.
---

## 18. Build integration (M34)
Taipan projects build with `dotnet build`, alongside C# projects in one solution, through an MSBuild project SDK, and Taipan libraries pack as NuGet packages.

### 18.1 SDK projects
A Taipan project is a `.tpnproj` file using the `TaipanLang.Sdk` project SDK, at the version of the compiler it builds with:

```xml
<Project Sdk="TaipanLang.Sdk/0.4.1">
  <PropertyGroup>
    <OutputType>Library</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../Acme.Core/Acme.Core.csproj" />
    <PackageReference Include="Humanizer.Core" Version="2.14.1" />
  </ItemGroup>
</Project>
```

- **It's an ordinary .NET project.** `TaipanLang.Sdk` builds on `Microsoft.NET.Sdk`, replacing only the compile step, so restore, `PackageReference`, `ProjectReference` (both ways), `Reference`, `dotnet build`, `dotnet run`, `dotnet pack`, `dotnet sln add` and copying to the output directory work as for a C# project. It targets `net10.0`, the runtime the compiler runs on (§14.4).
- **Sources** are every `.tpn` file under the project's directory, as C# projects glob `.cs` files. **The main file** is `$(TaipanMain)`, which defaults to `<project name>.tpn` beside the project file; the others are its modules, which must be in its directory (§4.7). A missing main file is an error, `TPN001: the main file '<path>' was not found; set TaipanMain`, reported by the build.
- **`OutputType`** is `Library` (the default) for a Taipan library (§14), or `Exe` for a program, which `dotnet run` runs. A library's main file has no top level (`TP0222`), as for `--library`.
- **Settings come from MSBuild** (§13.2's `taipan.json` doesn't apply):
  - `AssemblyName` and `RootNamespace` (each defaulting to the project's name, as MSBuild's do) name a library's assembly and namespace (§14.1), and `Version` its assembly's version.
  - The assemblies a Taipan file may import from (`from dotnet.<ns> import T`, §13.1) are those MSBuild resolves for the project: the framework's, its `PackageReference`s and their dependencies, its `ProjectReference`s, and its `Reference`s.
  - A `taipan.json` beside a `.tpnproj` may not list `references` or `packages`: `TPN002: list references and packages in the project file, not taipan.json`. Its other keys are ignored.
- **The runtime:** the SDK adds a `PackageReference` to `TaipanLang.Runtime` at its own version, so the program or library and anything referencing it get `Taipan.Runtime.dll`.
- **Diagnostics** are reported to MSBuild in its format, `<path>(<line>,<col>): error TP0200: <message> (help: <hint>)`, so they show in `dotnet build`'s output and IDEs' error lists; errors fail the build, and warnings (`TP03xx`) don't, unless `TreatWarningsAsErrors` is set.
- **Referencing a Taipan project** from a C# project is a `ProjectReference` to the `.tpnproj`: the library's modules are its public classes (§14.1). **Referencing a C# project** from a Taipan project is a `ProjectReference` to the `.csproj`, whose public types are imported as .NET types (§13).
- **Incremental:** the compile step runs again only when a `.tpn` file, a reference, or the project changes.

### 18.2 The compile command
`taipan compile <file> [--library | --test] --out <dll> [--reference <dll>]… [--assembly <name>] [--namespace <ns>] [--version <v>]` is how the SDK runs the compiler. It compiles the main file and its modules into one assembly at `<dll>`, against exactly the assemblies given with `--reference` (framework included), and writes nothing else: no runtime, runtimeconfig or copied references, which MSBuild provides. `taipan.json` isn't read. Diagnostics are written to stderr in MSBuild's format (§18.1), and exit codes are §9's. An argument `@<file>` stands for the arguments in a file, one per line, and `--reference=<dll>` is the one-argument form of `--reference <dll>`, so the SDK passes a long reference list in a response file. It's meant for the SDK, though it works from a shell too.

### 18.3 Naming a library in taipan.json
For `taipan build --library` and `taipan pack`, `taipan.json` may name the library:

```json
{
  "assembly": "Acme.Shapes",
  "namespace": "Acme.Shapes",
  "version": "1.2.0",
  "authors": "Acme",
  "description": "Shapes and their areas."
}
```

- `assembly` is the assembly's name and the package's ID (default: the main file's name), and `namespace` the namespace (default: as §14.1 says). Each must be a dotted .NET name (`Acme.Shapes`), or it's `<path>: 'assembly' must be a dotted name` (and `'namespace'`).
- `version` is the assembly's and package's version, as NuGet writes one (`1.2.0`, `2.0.0-beta.1`; default `1.0.0`); anything else is `<path>: 'version' must be a version, found '<v>'`.
- `authors` and `description` go in the package's metadata (defaults: the assembly name, and `A Taipan library.`).
- These are errors as §13.2's are: `taipan: <message>`, exit code `2`.

### 18.4 Packing
`taipan pack <file> -o <dir>` builds the library (as `build --library`, §14.4) and writes `<dir>/<assembly>.<version>.nupkg`:

- `lib/net10.0/` holds the library's assembly and the assemblies `taipan.json`'s `references` list, which no package brings.
- Its dependencies are `TaipanLang.Runtime` at the compiler's version, and each package `taipan.json` lists, at its version.
- A program (with a top level) can't be packed: `TP0222`, as for a library.

An SDK project packs with `dotnet pack`, as any .NET project does, with MSBuild's package metadata (`PackageId`, `Version`, `Authors`, `Description`).

### 18.5 Build diagnostics
The SDK's own errors, outside any Taipan file:

| Code | Message |
|---|---|
| `TPN001` | `the main file '<path>' was not found; set TaipanMain` |
| `TPN002` | `list references and packages in the project file, not taipan.json` |

### 18.6 Test projects (M44)
A test project is a `.tpnproj` with `<IsTestProject>true</IsTestProject>`, MSBuild's usual property for one:

```xml
<Project Sdk="TaipanLang.Sdk/0.4.1">
  <PropertyGroup>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../Acme.Shapes/Acme.Shapes.tpnproj" />
  </ItemGroup>
</Project>
```

- **Its main file is a test file** (§20), whose `test_*` functions are the project's tests. Its other `.tpn` files are modules it may import, as in any project. `OutputType` is `Exe` whatever the project says, since the built program runs the tests.
- **`dotnet test`** builds it and runs the program, which runs the tests as `taipan test` would and writes §20's report, failing when the program exits non-zero; **`dotnet run`** runs it the same way. The SDK's `VSTest` target, which `dotnet test` calls, runs the program instead of a test adapter, so the results are the report's, not .NET test results (TRX files and IDE test explorers are deferred).
- A test file in a project reports itself by its path relative to the project's directory.
- `TaipanLang.Sdk` and the other packages are version `0.4.1` since M47 (§12.2).

### 18.7 Deferred
§11 lists what's deferred: multi-targeting, test results in .NET's formats, native and Python-extension SDK projects, and symbols packages. Design-time builds for Visual Studio aren't planned.

---

## 19. Formatting (M44)
`taipan fmt` rewrites Taipan files in one style, as Black does Python's. It changes only the whitespace between tokens, so it can't change what a program means, and formatting its output again changes nothing.

```
taipan fmt <path>…          format each file, and each .tpn file under each directory, in place
taipan fmt --check <path>…  write `would reformat <path>` for each that isn't formatted, and change nothing
```

`fmt` exits `0` when it formatted every file, or with `--check` when every file was formatted already, and `1` when a file had errors or, with `--check`, would be reformatted.

### 19.1 What it changes, and what it doesn't
- **Only whitespace:** spaces within a line, indentation, and blank lines. It keeps every token, in order, and every line break inside brackets, so it never joins or splits a line. String and f-string literals, and comments' text, are kept exactly as written, apart from a comment's trailing spaces. Before writing, it lexes its output and checks that the tokens are the input's; a difference would be a bug in the formatter, reported as one (exit `2`) without writing anything.
- **It formats only valid code:** a file with lexing or parsing errors (`TP00xx`, `TP01xx`) has them printed as `taipan check` prints them, isn't changed, and makes the exit code `1`. Type errors don't stop it, since they don't depend on layout.
- Line endings are `\n`, and the file ends with exactly one. Trailing whitespace is removed from every line.

### 19.2 The style
- **Indentation:** four spaces per block level, whatever the file used.
- **Continuation lines** inside brackets are indented four spaces more than their logical line's start for each bracket still open at the start of the line. A line that starts with closing brackets is indented as the bracket depth after them, so a closing bracket on its own line lines up with the line that opened it. A match expression's arms inside brackets (§5.1.1) are a block, indented four spaces more than the line with the `match`, and the line after them is indented as that line, since less indentation is what ends them:

  ```python
  let shapes = [
      Circle(1.0),
      Square(
          2.0),
  ]
  ```
- **Within a line**, between two tokens there's one space, unless a rule here says none:
  - none before `,`, `)`, `]`, `}`, `.`, `?` (a type's optional), and before `:` everywhere;
  - none after `(`, `[`, `{`, `.`, a decorator's `@`, and a unary operator (`-x`, `+x`, `*args`, `**kwargs`). `-`, `+`, `*` and `**` are unary at the start of a line, and after an opening bracket, a `,`, an operator, `=`, `:`, `->` or a keyword other than `None`, `True` and `False`;
  - none between a name, a closing bracket, a string or a number and the `(` or `[` after it: calls, indexing and type arguments (`f(x)`, `xs[0]`, `list[int]`, `first[T]`). After a keyword there is one: `if (a or b)`, `in [1, 2]`;
  - none around `=` inside brackets (keyword arguments, `@dataclass(order=True)`, class patterns' fields) except in a `def`'s parameter list, where a default has one on each side: `def f(x: int = 1, *, sep: str = " ")`;
  - none around `:` in a subscript's brackets, a slice (`xs[1:n]`, `xs[::2]`). Elsewhere `:` has one space after it, unless the line ends there or a closing bracket follows: `x: int`, `{"a": 1}`, `lambda x: x`, `[T: Animal]`.
  - `,` has one space after it, unless the line ends there or a closing bracket follows: `(x,)`.
  - Binary operators (`+ - * / // % ** == != < <= > >= ?? | & ^`), `=` and the augmented assignments, `->`, and keywords (`and`, `in`, `if`, `else`, `as`, …) have one space on each side.
- **Comments:** a comment after code on its line has two spaces before it. A comment on its own line is indented as the code after it, unless it was indented deeper than that code, when it's indented as the code before it, so a comment at the end of a block stays in the block.
- **Blank lines:** none at the start of the file or of a block, and none inside brackets. At most two in a row at the top level, and one inside a block. Others are kept as they are.
- There are no options: the style is the same for every file.

---

## 20. Tests (M44)
`taipan test` finds and runs test functions, as pytest does Python's.

```python
# test_shapes.tpn
from shapes import area, Circle

def test_area():
    assert area(Circle(1.0)) > 3.14

def test_negative():
    assert area(Circle(-1.0)) == 0.0, "a negative radius has no area"
```

```
$ taipan test
PASS test_shapes.tpn::test_area
FAIL test_shapes.tpn::test_negative
Traceback (most recent call last):
  File "test_shapes.tpn", line 8, in test_negative
    assert area(Circle(-1.0)) == 0.0, "a negative radius has no area"
AssertionError: a negative radius has no area
1 passed, 1 failed
```

### 20.1 Finding tests
- **`taipan test [<path>…]`** takes test files and directories, and `.` when there are none. A directory's test files are the `.tpn` files under it, at any depth, named `test_*.tpn` or `*_test.tpn`, in path order. A file given by name is a test file whatever its name.
- **Each test file is a main file** (§4.7): it's compiled as `taipan check` compiles it, with its modules loaded from its directory and its `taipan.json` read there. Its diagnostics are printed as `check` prints them. A file with errors has none of its tests run, and counts as an error in the summary.
- **Its tests** are its own top-level functions whose names start with `test_`, in the order they're defined; functions it imports aren't its tests. A test may be `async` (§3.14), and is then run on the event loop as `asyncio.run` runs an awaitable (§3.20). What a test returns is ignored.
- A `test_` function with parameters, or with type parameters, can't be called as a test: it's warning `TP0303`, `'<f>' takes parameters, so it isn't run as a test`, reported only by `taipan test`, and the function isn't run.

### 20.2 Running them
- **The file's top level runs first**, once, as `taipan run` would run it, then each test in order, in the same process. A top level that raises is reported as `ERROR <file>` with the traceback, and the file's tests aren't run.
- **A test passes** if it returns, and **fails** if it raises any exception, an `AssertionError` from `assert` (§5.6) or any other. A test can't stop the others: `sys.exit` in a test is a failure too.
- Tests run one after another in one process, so state a test changes, such as a class attribute (§4.8), stays changed for the tests after it. (Functions can't use top-level `var`s, §4.5, so neither can tests.)
- A program's output, and its input, are as in `taipan run`: the tests' own `print`s appear where they run.

### 20.3 The report
- After each test, on stdout: `PASS <file>::<test>`, or `FAIL <file>::<test>` followed by the exception's traceback (§3.19), whose first frame is the test's, so the failing `assert` and its line are shown. `<file>` is the path as given, or for a file found in a directory, the directory as given followed by the file's path under it (without a leading `./` for `.`).
- At the end, a summary: `<p> passed, <f> failed`, with `, <e> errors` (`1 error` for one) added when files had errors or top levels raised, or `no tests found` when there were none.
- **The exit code** is `0` when tests ran and all passed, and `1` when a test failed, a file had an error, or no tests were found. Bad arguments, or a path that doesn't exist, are `2`, as for other commands (§9).
