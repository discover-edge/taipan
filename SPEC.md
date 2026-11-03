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
  `def class enum let var const if elif else while for in match return pass break continue and or not is some None True False as from import lambda try except finally raise del yield`
  - `yield` is a keyword since M31 (§3.13).
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
  `+ - * / // % == != < <= > >= = += -= *= /= -> ?? ? . ... , : ( ) [ ] { } @`
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

import_stmt   = "import" NAME [ "as" NAME ] NEWLINE                                  (* M9, §4.7 *)
              | "from" module_path "import" import_name { "," import_name } NEWLINE ;
module_path   = NAME { "." NAME } ;                                  (* dotted: .NET namespaces, M21, §13.1 *)
import_name   = NAME [ "as" NAME ] ;

const_def     = "const" NAME [ ":" type ] "=" expr NEWLINE ;

func_def      = { decorator } "def" NAME [ type_params ] "(" [ params ] ")" [ "->" type ] ":" block ;
decorator     = "@" NAME [ "." NAME ] NEWLINE ;                      (* M15: @dataclass, @override; §4.3. M28: §4.8 *)
params        = param_item { "," param_item } [ "," ] ;
param_item    = param | "/" | "*" [ NAME ":" type ] | "**" NAME ":" type ;   (* M30, §4.2 *)
param         = NAME ":" type [ "=" expr ] ;                         (* default: M14, §4.2 *)
type_params   = "[" type_param { "," type_param } "]" ;             (* M8, §3.6 *)
type_param    = NAME [ ":" ( "(" type { "," type } [ "," ] ")"     (* constraints: M8.1; fewer than two types is TP0220 *)
                           | type ) ] ;                            (* a bound: M17, §3.11; anything but a protocol is TP0220 *)

class_def     = { decorator } "class" NAME [ type_params ] [ "(" base { "," base } ")" ] ":"   (* M15, §4.3 *)
                NEWLINE INDENT class_member { class_member } DEDENT ;
base          = NAME [ "." NAME ] [ "[" type { "," type } "]" ] ;   (* more than one class base is TP0225; protocols may be added: M17 *)
class_member  = NAME ":" type [ "=" expr ] NEWLINE | method_def | "pass" NEWLINE ;
method_def    = { decorator } "def" NAME "(" [ method_params ] ")" [ "->" type ] ":" ( block | "..." NEWLINE ) ;   (* M5; "...": M17 *)
method_params = ( "self" | "cls" ) { "," param } [ "," ] | params ;  (* M28: "cls", or neither, for class and static methods, §4.8 *)

enum_def      = "enum" NAME [ type_params ] ":" NEWLINE INDENT variant { variant } DEDENT ;
variant       = NAME [ "(" named_fields ")" ] NEWLINE ;
named_fields  = NAME ":" type { "," NAME ":" type } [ "," ] ;

block         = NEWLINE INDENT stmt { stmt } DEDENT ;

stmt          = simple_stmt NEWLINE | compound_stmt ;
simple_stmt   = let_stmt | var_stmt | assign_stmt | return_stmt | raise_stmt | del_stmt | yield_stmt
              | "pass" | "break" | "continue" | expr ;
del_stmt      = "del" target { "," target } ;                       (* M26, §5.5 *)
let_stmt      = "let" ( NAME [ ":" type ] | names ) "=" expr_list ;
var_stmt      = "var" ( NAME [ ":" type ] | names ) "=" expr_list ;
names         = unpacked "," unpacked { "," unpacked } [ "," ] ;      (* unpacking: M12, §3.8 *)
unpacked      = NAME | "*" NAME | "(" names ")" ;                    (* "*" and nesting: M29 *)
assign_stmt   = target ( "=" | "+=" | "-=" | "*=" | "/=" ) expr
              | targets "=" expr_list ;                              (* M12 *)
targets       = unpack_target "," unpack_target { "," unpack_target } [ "," ] ;
unpack_target = target | "*" target | "(" targets ")" ;              (* "*" and nesting: M29 *)
target        = NAME | postfix "." NAME | postfix "[" subscript "]" ;   (* a slice: M26, §5.5 *)
return_stmt   = "return" [ expr_list ] ;
match_expr    = "match" expr ":" NEWLINE INDENT value_arm { value_arm } DEDENT ;   (* M19, §5.1.1; an atom since M29 *)
value_arm     = pattern ":" ( ( expr | raise_stmt ) NEWLINE | block ) ;   (* block: M29 *)
expr_list     = expr { "," expr } ;                                  (* two or more make a tuple: M12 *)
raise_stmt    = "raise" [ expr ] ;                                   (* M16, §3.10 *)
yield_stmt    = "yield" [ "from" ] expr ;                            (* M31, §3.13 *)

compound_stmt = if_stmt | while_stmt | for_stmt | match_stmt | try_stmt | func_def ;   (* in a block: nested function, M11 *)
try_stmt      = "try" ":" block { except_clause } [ "else" ":" block ] [ "finally" ":" block ] ;   (* M16, §3.10 *)
except_clause = "except" [ except_types [ "as" NAME ] ] ":" block ;
except_types  = qual_name | "(" qual_name { "," qual_name } [ "," ] ")" ;
if_stmt       = "if" condition ":" block { "elif" condition ":" block } [ "else" ":" block ] ;
condition     = expr "is" "some" NAME | expr ;
while_stmt    = "while" condition ":" block ;
for_stmt      = "for" ( NAME | names ) "in" expr ":" block ;       (* nested and starred names: M29 *)
match_stmt    = "match" expr ":" NEWLINE INDENT arm { arm } DEDENT ;
arm           = pattern ":" ( simple_stmt NEWLINE | block ) ;
pattern       = "_" | [ "-" ] INT | STRING | "True" | "False"
              | qual_name [ "(" [ pat_args ] ")" ]                   (* a bare NAME: a variant or a binding, M29, §5.1 *)
              | "(" pattern "," [ pattern { "," pattern } [ "," ] ] ")"   (* tuple: M29; one element is TP0209 *)
              | "[" [ list_pat { "," list_pat } [ "," ] ] "]" ;      (* list: M29 *)
list_pat      = pattern | "*" NAME ;                                 (* "*_" ignores the rest *)
pat_args      = pat_arg { "," pat_arg } [ "," ] ;
pat_arg       = pattern | NAME "=" pattern ;                         (* keyword form: class patterns, M15; sub-patterns: M29 *)
qual_name     = NAME [ "." NAME [ "." NAME ] ] ;                    (* module.Enum.Variant: M9 *)

type          = base_type { "?" } ;                               (* more than one "?" is TP0213, §3.1 *)
base_type     = "int" | "float" | "bool" | "str" | "py"           (* py: M10 *)
              | "list" "[" type "]"
              | "dict" "[" type "," type "]"
              | "tuple" "[" type { "," type } "]"                    (* M12; fewer than two types is TP0209 *)
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
comparison    = additive [ comp_op additive | "is" [ "not" ] "None" ] ;   (* no chaining; 'is not None': M15 *)
comp_op       = "==" | "!=" | "<" | "<=" | ">" | ">=" | "in" | "not" "in" ;
additive      = term { ( "+" | "-" ) term } ;
term          = unary { ( "*" | "/" | "//" | "%" ) unary } ;
unary         = "-" unary | cast ;
cast          = postfix [ "as" type ] ;                            (* M10 *)
postfix       = atom { call | "." NAME | "[" subscript "]" } ;
subscript     = expr | [ expr ] ":" [ expr ] [ ":" [ expr ] ] ;      (* a slice: M12, §5.4 *)
call          = "(" [ arg { "," arg } [ "," ] ] ")" ;
arg           = expr | NAME "=" expr | "*" expr | "**" expr ;        (* "*" and "**": M30, §4.2 *)
atom          = INT | FLOAT | STRING | FSTRING | "True" | "False" | "None"
              | NAME | "self" | "(" expr ")" | "..."                 (* "...": M17, a protocol method's body *)
              | "(" expr "," [ expr { "," expr } [ "," ] ] ")"         (* a tuple: M12; one element is TP0209 *)
              | "[" [ expr { "," expr } [ "," ] ] "]"
              | "{" [ expr ":" expr { "," expr ":" expr } [ "," ] ] "}"
              | match_expr ;                                         (* anywhere: M29, §5.1.1 *)
```

**Grammar notes**
- `a < b < c` is `TP0212`. Write `a < b and b < c`.
- An `arm` may take a simple statement on the same line (`Ok: print("ok")`) or an indented block.
- **`match` expressions (M19)** were only the whole right-hand side of a `let`, `var` or assignment, or the whole value of a `return`, before M29 (`TP0100`, `'match' can only be the whole value of 'let', 'var', an assignment or 'return'`). Since M29 a `match_expr` is an atom, so it may appear anywhere an expression may (§5.1.1). Its `:` must end its line (`TP0100`, `expected newline, found <token>`), and its arms are indented under it. Outside brackets, a statement whose last expression is a `match_expr` has no `NEWLINE` of its own: the arms' `DEDENT` ends it. A `match` at the start of a statement is always a `match_stmt`.
- A `value_arm` is an expression or a `raise` on the same line as its pattern, `Ok: "up"`, `_: raise ValueError("bad status")`, or since M29 an indented block (§5.1.1). Before M29 a block was `TP0100`, `expected expression, found newline`.
- **Unpacking (M29):** a parenthesized group in `names` or `targets` nests, and `*` marks the name or target that takes the rest. In `for`, `names` may also be parenthesized as a whole: `for (a, b) in pairs`. Two starred names at one level are `TP0100`, `an unpacking can have only one '*' at each level`; a starred name alone is `TP0100`, `a starred name can't be unpacked to alone`; and `*x` anywhere else is `TP0100`, `'*' can only mark a name or target being unpacked`. A trailing comma needs two names before it, so `let a, = t` is still `TP0100`.
- Keyword arguments are allowed in calls to functions and methods (M14, §4.2), class and enum-variant constructors (§4.3), `py` values (§10.2), and the keyword parameters of built-ins (`key` and `reverse` of `sorted` and `list.sort`, `start` of `enumerate`; §6). Keyword arguments may follow positional ones, as in Python. A positional argument after a keyword argument is `TP0100`.
- In a parameter list, a parameter without a default after one with a default is `TP0100`, as in Python. Since M30 that applies only before `*`, and the order of `/`, `*`, `*args` and `**kwargs` is checked as §4.2 says (`TP0100`).
- **Decorators (M15)** are only `@dataclass` on a `class` and `@override` on a method (§4.3), and since M28 `@property`, `@<name>.setter`, `@staticmethod`, `@classmethod` and `@abstractmethod` on a method (§4.8). A decorator takes no arguments: `@dataclass(frozen=True)` is `TP0100`. Any other decorator name, a decorator in any other place, a repeated one, or one that can't be combined with another (§4.8) is `TP0224`. A dotted decorator other than `@<property>.setter` is `TP0224`.
- **Receivers (M28):** an instance method, property or setter must take `self` first, a `@classmethod` must take `cls` first, and a `@staticmethod` takes neither. Otherwise it's `TP0225`, `'<m>' must take 'self' first` (or `'cls'`, or `a static method takes neither 'self' nor 'cls'`). As before M28, `self` and `cls` are written without a type.
- **`is`:** `x is None` and `x is not None` are comparisons that give `bool`, usable in any expression (M15). `x is some v` binds a name, so it's only a whole `if`/`elif`/`while` condition. Any other right operand of `is`, including another expression (`x is y`), is `TP0100`, `expected 'some', 'not' or 'None'`.
- **`try` (M16):** a `try` needs at least one `except` clause or a `finally`, and an `else` needs at least one `except`. A bare `except:` must be the last clause. Otherwise it's `TP0100`. An `except` clause names its types (`except KeyError`, `except zoo.NotFound`, `except json.JSONDecodeError`) rather than taking an expression, so in `except KeyError as e` the `as` names the binding rather than starting a cast.
- A class body holds field declarations, methods and `pass`. Classes are top-level only, so a `class` inside a block is `TP0100`.
- **Type aliases (M18)** are top-level only too. A line that starts with `type`, a name and `=` or `[` inside a block or class body is `TP0100`, `a type alias must be at the top level`. Python allows them in functions and classes; that's deferred.
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
  - Specific messages: `cannot assign to this expression` (an `assign_stmt` target that isn't a `target`), `positional argument follows keyword argument`, `parameter without a default follows parameter with a default` (M14), `a bare 'except' must be the last except clause` (M16, at the bare `except`, after which parsing continues normally), `a type alias must be at the top level` (M18, at `type`), and `'match' can only be the whole value of 'let', 'var', an assignment or 'return'` (M19, at `match`). A `try` with no `except` or `finally`, or an `else` with no `except`, is `expected 'except' or 'finally', found …`.
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
| `T?` | optional `T` | `T?` (C# nullable) |
| `py` | dynamic Python object (M10, §10) | `Python.Runtime.PyObject` |
| `C` | instance of a class `C` (M15, §4.3) | a C# class (§8) |
| `Exception`, `ValueError`, … | built-in exception classes (M16, §3.10) | classes in `Taipan.Runtime` (§8) |
| `Callable[[A, B], R]` | function value (M11, §3.7) | `Taipan.Runtime.Function<D>` (§8) |
| `Iterator[T]`, `Iterable[T]` | single-pass stream, and anything a `for` loop goes over (M31, §3.13) | `Taipan.Runtime.IIterator<T>`, `IEnumerable<T>` (§8) |
| `Comparable`, user protocols | structural interface, only as a type parameter bound (M17, §3.11) | none: bounded generics are monomorphized |

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
- `x == None` is allowed and returns `bool`, but it doesn't narrow.
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
- **Type parameters are opaque** unless they have a bound (M17, §3.11) or a constraint (M8.1, below). Bodies are checked once, with each type parameter treated as an opaque type `T`:
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
  - Generic functions, built-in functions, methods, class constructors and enum variants aren't values (`TP0200`, §9.1). Wrap them in a lambda instead: `lambda s: s.total()`.
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
  - A tuple has a fixed number of elements, at least two. `tuple[int]` is `TP0209`. Variable-length `tuple[int, ...]` isn't supported; use a list.
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
  - Nothing else, such as an element (`xs[0]`) or a call's result. Copy it to a `let` first.
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
  - `copy it to a 'let' first` for anything else that can't be narrowed, such as `xs[0]` or a path rooted at a `var`.
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
  | `Exception` | | |
  | `ArithmeticError` | `Exception` | |
  | `ZeroDivisionError` | `ArithmeticError` | division by zero |
  | `OverflowError` | `ArithmeticError` | int overflow, and `int(x)` of an infinite or out-of-range float |
  | `LookupError` | `Exception` | |
  | `IndexError` | `LookupError` | an index out of range, and `pop()` on an empty list |
  | `KeyError` | `LookupError` | a missing dict key |
  | `ValueError` | `Exception` | `int(x)` of a NaN, `split("")` and `replace("", …)`, and a slice step of 0 |
  | `TypeError` | `Exception` | a failed `as` (§4.3, §10.4) |
  | `RuntimeError` | `Exception` | changing a list or dict while iterating over it; `StopIteration` escaping a generator (M31) |
  | `StopIteration` | `Exception` | `next()` on an exhausted iterator, or a class's `__next__` ending (M31, §3.13) |
  | `PythonError` | `Exception` | a Python exception from a `py` operation (§10.5) |
  | `DotNetError` | `Exception` | a .NET exception from a .NET call, caught as `Exception` (M21, §13.6) |

- `Exception` has one field, `message: str`, and an `__init__(self, message: str = "")`. Every built-in class except `PythonError` takes the same arguments: `raise ValueError("bad port")`. Python has no `message` field; it keeps the arguments in `args`, and `str(e)` gives the message. In Taipan, `str(e)` gives the message too (§6).
- `PythonError` has two more fields: `type: str`, the Python exception's class name, and `value: py`, the Python exception object. Its `message` is Python's `str(e)`. Only the runtime creates one: constructing it is `TP0200`, and deriving from it is `TP0225`. The same holds for `DotNetError` (M21), whose one more field is `type: str`, the .NET exception's full class name (§13.6).
- The built-in exception classes' names are global, like other classes', so a definition with one of their names is `TP0214`.
- **User exceptions** are plain classes deriving, directly or not, from `Exception`. They follow every rule of §4.3: a subclass with its own fields sets them and then calls `super().__init__(message)`, and one without `__init__` takes `Exception`'s `message` argument. Since a dataclass can only derive from a dataclass (§4.3), `@dataclass class NotFound(Exception)` is `TP0225`.
- Exceptions compare by identity, like other plain classes, and may be stored, passed and returned like any value.

**`raise`**
- `raise e` raises `e`, which must be an `Exception` (a class deriving from it). Anything else is `TP0200`. Raising the class itself, as in `raise ValueError`, which Python allows, is `TP0200` with help `write 'ValueError(…)'`.
- A bare `raise` re-raises the exception being handled. It's valid only inside an `except` block, and not in a function nested in one (`TP0215`).
- A `raise` terminates (§4.5).

**`try`**
- The `try` block runs first. If it raises, the first `except` clause that matches handles the exception. If none matches, the exception propagates.
- The `else` block runs only if the `try` block finishes without raising, returning, breaking or continuing. An exception raised in an `except` or `else` block isn't caught by the same `try`'s clauses.
- The `finally` block runs last, on every path out of the `try`: normally, through `return`, `break` or `continue`, and while an exception propagates. If the `finally` block raises, its exception replaces the one in flight, as in Python.
- `return`, `break` and `continue` aren't allowed in a `finally` block (`TP0215`). In Python they silently discard a propagating exception, and Python 3.14 warns about them (PEP 765).

**`except` clauses**
- `except C` matches an exception of class `C` or a class deriving from it. `C` must be `Exception` or derive from it, and may be qualified with a module: `except zoo.NotFound`. A bare `except:` is `except Exception:`. `except (A, B)` matches either.
- `except m.E` where `m` is a Python module (`py`, §10) takes a Python exception class: `except json.JSONDecodeError`. It matches a `PythonError` whose `value` is an instance of that class, as tested by Python's `isinstance`. The class is looked up when an exception reaches the clause.
- Anything else, such as a class not deriving from `Exception`, an enum or a type parameter, is `TP0200`.
- `except … as e` binds `e`, a `let` visible only in that clause's block. Its type is the clause's class, `PythonError` for a Python class, or the nearest class all of the listed classes derive from (§3.3), so `except (KeyError, IndexError) as e` gives a `LookupError`.
- A clause whose classes all derive from those of an earlier clause can never match, and is warning `TP0300`. Python classes aren't checked.
- **Broad clauses (`TP0301`).** A bare `except:`, or a clause listing `Exception`, also catches mistakes such as an `IndexError`. Such a clause is warning `TP0301`, at `except`, unless its block contains a `raise` (outside any nested function), or the clause binds the exception with `as e` and the block uses `e`. So logging the exception, or re-raising it, is deliberate handling. A clause that silently discards everything is warned about, as Python linters do (flake8's E722, pylint's `broad-exception-caught`).

**Return paths and initialization**
- A `try` terminates (§4.5) if its `finally` block terminates, or if every `except` block terminates and the `try` block or the `else` block terminates.
- In `__init__` (§4.3), a `raise` before the object is complete is valid, since the object is never seen. Fields assigned in a `try` block count as assigned only after the whole `try`, and only if the `else` block or the `try` block assigns them, every `except` block assigns them too, or the `finally` block assigns them. `super().__init__(…)` inside a `try` isn't directly in the body (`TP0226`).

**Uncaught exceptions** are panics (§7). They print the line where the exception was raised and exit with 101.
- A Taipan built-in exception prints its message alone, as before M16: `panic: missing key "a"`. A `PythonError` prints `Python <type>: <message>`, as in §10.5. A user exception prints its class name and message, as Python does: `panic: NotFound: no service named db`. An empty message prints the class name alone.
- `raise e` reports its own line, even when `e` was caught elsewhere. A bare `raise` keeps the line where the exception was first raised.

**What isn't catchable**
- Importing Python modules happens at startup (§10.1), before any `try` runs, so a failure to load Python or to import a module is always a panic. So is a failure to load Python at a program's first conversion to `py`, when it imports no Python module (§10.5).
- Running out of stack ends the process, as in .NET; it isn't an exception. Internal compiler errors aren't exceptions either.

### 3.11 Protocols (M17)
A protocol is a structural interface, as in Python's `typing.Protocol` (PEP 544): a set of method signatures that a type satisfies by having them, without declaring it. In v0.1 a protocol is used only as the bound of a function's type parameter.

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
  - a field, a method body other than `...`, or a parameter default
  - type parameters on a protocol (generic protocols are deferred)
  - another base besides `Protocol`
  - any special method other than `__lt__` (below), including `__init__`. A protocol's `__lt__` must be `def __lt__(self, other: Self) -> bool`.
- A decorator on a protocol (`@dataclass`) or on one of its methods (`@override`) is `TP0224`.
- A method name repeated in a protocol is `TP0214`.
- `...` anywhere but a protocol method's body is `TP0200`. A function or method whose body is just `...` reports only that, and not also `TP0208`. `Self` outside a protocol method's signature is `TP0201`, `undefined type 'Self'`, with help `'Self' can only be used in a protocol`.
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
  - a bound that isn't a protocol, such as a class (class bounds are deferred) or a built-in type
  - a bound on a class's or enum's type parameter, as for constraints
- So that every bounded body is valid for every type argument that satisfies its bound, instantiation still never reports new errors (§3.6).

**Protocols aren't types of values.** A protocol can't be the type of a binding, parameter, field, return value or element, or be constructed, given to `isinstance`, matched in a `class` pattern or caught in an `except`. Each is `TP0229`, with help `use a bounded type parameter, e.g. 'def f[T: Shape](x: T)'`. Python allows protocols as types; in Taipan it would need a runtime representation for values of many types, and is deferred.

There are no run-time checks: satisfaction and bounds are entirely static, and a bounded function is monomorphized like any generic (§8).

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
- `type Name = T` and `type Name[A, B] = T` are top-level definitions (§2). Like classes, they're hoisted, so an alias may be used before its definition, and may refer to aliases defined after it.
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
- **Display:** diagnostics show the expanded type, not the alias's name: `expected Callable[[Request], Response], found int`.

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
- **`Iterable[T]`** is anything a `for` loop can go over: `list[T]`, `dict[T, V]` (its keys), `str` (as `Iterable[str]`, one character each), an `Iterator[T]`, or an instance of a class with `__iter__` (below). Each loop over a list, dict, str or class starts again from its beginning; a loop over an iterator continues from where it is.
- `Iterator` and `Iterable` are built-in generic names, like `list`, written as in Python's `typing`. Each takes one type argument (`TP0209`).
- **Assignability (§3.3):** each type above is assignable to `Iterable[T]` of its element type, and a class with `__next__` to `Iterator[T]`. Since iterators only produce values, `Iterator[Dog]` and `Iterable[Dog]` are also assignable to `Iterator[Animal]` and `Iterable[Animal]`, for classes, as Python's are covariant. Nothing else converts: a `list[T]` isn't an `Iterator[T]` (use `iter(xs)`).
- **Inference (§3.6):** an `Iterable[T]` parameter infers `T` from the element type of whatever is passed.
- **What they support:** `for`, unpacking (§3.8, checked at run time as for a list), `in` and `not in` (which take values until one is equal, as in Python), the built-ins below, and `==` and `!=`, which compare identity. They can't be indexed, sliced, measured with `len` or used as dict keys. Each is `TP0200`, and for indexing, slicing and `len` the help is `convert it with 'list(…)' first`.
- **Display:** `<generator object naturals>`, `<map object>`, `<filter object>`, `<zip object>`, `<enumerate object>`, `<list_iterator object>` from `iter` on a list, and `<iterator object>` for the rest, without Python's address. A class's instance displays as its class does (§4.8).
- Iterators aren't convertible to Python (§10.3). A library exports `Iterable[T]` as .NET's `IEnumerable<T>`, and `Iterator[T]` as the runtime's `IIterator<T>`, which is one (§14.2).

**Generators**
- **A generator function** is one whose body has a `yield`, not counting nested functions. It's declared to return `Iterator[T]` or `Iterable[T]`, as in Python's type hints. Any other return type, or none, is `TP0200`, `'<f>' yields, so it must return 'Iterator[T]'`.
- **`yield e`** is a statement. It gives `e`, which is checked against `T`, to whatever asked for the next value, and pauses the function there until it's asked again. The value of a `yield` expression (`x = yield e`, Python's `send`) is deferred: `yield` anywhere but at the start of a statement is `TP0100`. A bare `yield` is `TP0100`, `'yield' needs a value`.
- **`yield from e`** gives each value of `e`, an `Iterable[U]` with `U` assignable to `T`, in turn.
- **Calling a generator function** runs none of its body. It gives a new iterator; the body runs up to the first `yield` when the first value is asked for, and so on. The arguments are evaluated at the call, and the defaults too.
- **`return`** without a value ends the generator, and so does reaching the end of its body. `return e` is `TP0200`, `a generator can't return a value` (generator return values are deferred). Since a generator needn't return, `TP0208` doesn't apply.
- **Where:** `yield` outside a function is `TP0215`, `'yield' outside a function`. A lambda's body is an expression, so it can't yield. Methods may be generators, including `__iter__` (below).
- **Inside `try` (deferred):** `yield` in the body of a `try` that has an `except` clause, or in an `except` or `finally` block, is `TP0215`, `'yield' can't be inside a 'try' with 'except', or in 'finally'`. A `try` with only `finally` may yield. Its `finally` runs when the generator finishes. A `for` loop that stops early (`break`, `return` or an exception) leaves the generator where it is, so a later loop or `next` continues it, as in Python; one that's never finished never runs its `finally`, where Python would run it when the generator is collected.
- **`StopIteration`** raised inside a generator, and not caught there, is `RuntimeError`, `generator raised StopIteration`, as Python 3.7 and later do.

**The iterator protocol for classes (§4.8)**
- **`__iter__(self) -> Iterator[T]`** makes a class `Iterable[T]`: a `for` loop over an instance calls `__iter__` once, and takes values from its result. It's often a generator method.
- **`__next__(self) -> T`** makes a class `Iterator[T]`: each value asked for is a call to `__next__`, and it ends when `__next__` raises `StopIteration`. As in Python, an iterator class usually also has `def __iter__(self) -> Iterator[T]: return self`; Taipan treats a class with `__next__` as its own iterator either way.
- Both follow the rules for special methods (§4.8): calling them directly is `TP0200`, with help `use 'iter(x)'` and `use 'next(x)'`.
- A class's `__iter__` result and `__next__` result types give `T`. A class with both must give the same `T` (`TP0225`).

---

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
  - A dataclass can't define `__init__` (`TP0225`). `__post_init__` is deferred.

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
  - Generic classes can't derive or be derived from.
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
- **Shadowing (`TP0207`)** covers every name visible at the declaration, including globals and built-ins: `let len = 3` is `TP0207`. A function body can't see top-level `let`/`var` bindings (§4.1), so declaring the same name inside a function is allowed.
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
  - The module name `python` is reserved for Python interop (§10), and `dotnet` for .NET interop (M21, §13). A dotted `from` path is a .NET namespace under `dotnet` (§13.1); any other dotted path is `TP0221`, since modules aren't dotted.
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
| `__add__`, `__sub__`, `__mul__`, `__truediv__`, `__floordiv__`, `__mod__` | `(self, other: T) -> R`, for any `T` and `R` | `+ - * / // %` with the class value on the left |
| `__radd__`, `__rsub__`, `__rmul__`, `__rtruediv__`, `__rfloordiv__`, `__rmod__` | `(self, other: T) -> R` | the same operators with the class value on the right |
| `__neg__` | `(self) -> R` | unary `-` |
| `__iter__` | `(self) -> Iterator[T]` | `for`, `iter()` and every built-in that takes an `Iterable[T]` (M31, §3.13) |
| `__next__` | `(self) -> T` | `next()`, and taking a value from the class as an `Iterator[T]` (M31, §3.13) |

- Special methods are inherited and overridden like other methods (§4.3), with `@override` when a base class defines the method in its body. Defining one that only Python's `object` has (`__str__`, `__eq__`, …), or that a dataclass generates (below), needs no `@override`.
- Calling one directly (`m.__add__(n)`, `m.__str__()`) is `TP0200`, `'<m>' cannot be called directly`, with help naming its form (`use '+'`, `use 'str(m)'`, `use 'repr(m)'`, `use 'hash(m)'`), except as `super().__m__(…)` in an override.
- A special method can't be a property, static method or class method (`TP0224`).
- `x op= y` is `x = x op y` (§4.5), as in Python without `__iadd__`.

**Display.** `str(x)`, `print` and f-string holes call `__str__`. A class without one in its hierarchy uses `__repr__`, as Python's `object.__str__` does. A class without `__repr__` gets the M15 forms: `<Dog object>` for a plain class, and for a dataclass the generated `Point(x=1.0, y=0.0)`. As in Python, every dataclass generates its `__repr__` unless its own body defines one, so a dataclass deriving from one with a custom `__repr__` displays in the generated form. `repr(x) -> str` (§6) gives the form inside a container for any value, which `!r` writes. Format specs on a class value are still `TP0200` (`__format__` is deferred).

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
- `functools.total_ordering` is deferred, so `<=` needs `__le__` (or a reflected `__ge__`) even when the class has `__lt__` and `__eq__`.

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
- Using a static or class method without calling it is `TP0200`, with help `wrap it in a lambda`, as for other methods.
- A generic class can't have static or class methods or class attributes (`TP0225`, `generic classes cannot have <static methods|class methods|class attributes>`); that's deferred.

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

---

## 5. Statements and expressions

### 5.1 `match`
- The scrutinee can be an enum, `int`, `str`, `bool`, a class (M15), or since M29 a tuple or a list.
- **Patterns** (M29 makes them nest, as in Python):
  - `_` matches anything and binds nothing.
  - **A name** binds the value, as a `let` scoped to the arm: `x`, `Degraded(h, t)`. It's a binding unless it names a variant of the enum its position has (below).
  - **Literal patterns** (`0`, `-1`, `"api"`, `True`, `False`) apply to `int`, `str` and `bool`.
  - **Variant patterns:** `Ok`, `Degraded(h, t)`, `Health.Down`. Each argument is a sub-pattern for a payload field, by position. With a payload, the argument count must equal the field count. A variant with a payload may also be matched as `Degraded` without parentheses, matching any payload.
  - **Class patterns (M15):** `Dog()`, `Dog(name=n, breed="lab")`, and for a dataclass `Point(0, y)` by position, in constructor order, as Python's `__match_args__` does. Each argument is a sub-pattern for a field.
  - **Tuple patterns (M29):** `(0, y)`, `(Circle(r), _)`, with one sub-pattern per element.
  - **List patterns (M29):** `[]`, `[a, b]`, `[first, *rest]`, `[*init, last]`, `[a, *_, z]`. Without a star, a list matches when it has exactly as many elements as sub-patterns; with one, when it has at least as many as the others. `*name` binds the rest as a new list, and `*_` ignores it.
- **Bare names (M29).** In a position whose type is an enum, a bare name that's one of its variants is a variant pattern: `Some(Ok)` matches the variant `Ok`. A bare name that names a variant or a class in scope is never a binding either, since a local can't shadow it (§4.4, §4.6): it's checked as that pattern, so a variant of another enum is `TP0200`, `expected <T>, found <E>`, and a class is `'Cat' is a class; write 'Cat()'`. Any other bare name binds. So that a misspelled variant isn't silently a binding, a bare name in an enum-typed position that starts with an uppercase letter and isn't a variant of that enum is `TP0210`, `<E> has no variant '<x>'`, as before M29. A qualified name (`Health.Ok`, `m.Health.Ok`) is always a variant pattern.
- **Class patterns** match when the value's runtime class is the named class or derives from it, as with `isinstance`:
  - Unlike a variant, a class pattern always has parentheses, as in Python: a bare `Dog` is `TP0200`.
  - Keyword arguments name any field of the class, inherited ones included, in any order. Positional ones may come first, for a dataclass only (`TP0209` on a plain class, and more than there are fields is `TP0209`).
  - A field named twice is `TP0209`, and an unknown one is `TP0210`.
  - The class must derive from the type of its position, or be it (`TP0200` otherwise). In its arm, a scrutinee that's a name that can't change (§3.9) is narrowed to that class.
- **Types.** Each position has a type: the scrutinee's, then a payload field's, a class field's, a tuple element's, or a list's element type. A sub-pattern must fit it: a literal of another type, a variant pattern where there's no enum, a tuple pattern of another length (`TP0209`, `<T> has <n> elements, found <m> patterns`), or a list pattern on anything but a list, is `TP0200`. A binding gets its position's type, and `*rest` gets a `list` of the element type. An optional position takes only `_` and bindings; other patterns there are `TP0203`, since `None` patterns are deferred.
- **Exhaustiveness** (generalized in M29): a `match` must cover every value its scrutinee's type can have, through all nesting. Otherwise it's `TP0204`. It's checked as Python's type checkers do (pyright's "exhaustive match"), by the usefulness of each arm against the arms before it:
  - An enum is covered by its variants, a `bool` by `True` and `False`, a tuple or a class of the scrutinee's own type by one pattern whose sub-patterns cover each element or field, and a list by patterns covering every length.
  - `int` and `str` positions, and class patterns of a subclass, are covered only by a `_` or a binding there: subclasses are open-ended, so listing them never covers the base.
  - The message lists, in order, a missing case for each kind of value no arm covers, written as patterns with `_` for any value: `missing: Down, Degraded(0, _), Move((_, True))`, at most 12 of them. A variant no arm names is written bare, as before M29 (`missing: Down, Ok`); one that an arm names is written with its payload, `Level(_)`. A class pattern shows only the fields that matter, `Point(y=False)`. Lists are written by length, `[]` or `[_, _, *_]` for any longer.
- **Reachability:** an arm that can't match anything the arms before it don't, such as one after `_`, a duplicate, or `(0, _)` after `(_, _)`, is warning `TP0300` (unreachable). Unreachable arms are still checked, but not emitted.
- `match` is a statement, and since M19 also an expression (§5.1.1).
- **Checking details:**
  - Any other scrutinee type is `TP0200`, and an optional scrutinee is `TP0203`.
  - The scrutinee is evaluated once. Sub-patterns are tested left to right, and a list's length before its elements.
  - Variant patterns are resolved against their position's enum, so an unqualified variant pattern is never ambiguous. A variant the enum doesn't have is `TP0210`, and a qualified pattern naming another enum is `TP0200`.
  - A variant without a payload may be written with or without empty parentheses. Arguments to it otherwise are `TP0209`.
  - **`_`** may repeat, and so may `*_`. A list pattern with two stars is `TP0100`, `a list pattern can have only one '*'`.
  - A pattern whose position's type already has an error isn't reported again, and the `match` isn't checked for exhaustiveness or reachability.
  - Bindings follow the shadowing rules (`TP0207`), and a name bound twice within one pattern is `TP0214`.
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
- **Patterns** are the statement's (§5.1): the same scrutinee types, patterns, bindings, narrowing and checks. Bindings are scoped to their arm's expression.
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
| `== !=` | same type, int/float, or related classes | bool | structural for dataclasses, enums, lists, dicts, tuples; identity for plain classes (§4.3) and functions (§3.7); a class's `__eq__` since M28 (§4.8) |
| `is None`, `is not None` | T? | bool | M15; narrows (§3.9) |
| `< <= > >=` | numeric, or str (ordinal), or tuples of those (M12) | bool | tuples compare lexicographically |
| `< >` | a class with `__lt__`, or a type parameter bounded by a protocol with `__lt__` (M17) | bool | `a < b` is `a.__lt__(b)`, `a > b` is `b.__lt__(a)` (§3.11) |
| `< <= > >=`, `+ - * / // %`, unary `-` | a class with the matching special method (M28) | the method's | §4.8 |
| `in`, `not in` | T in list[T]; K in dict[K,V]; str in str | bool | |
| `??` | T?, T | T | |
| `and or not` | bool | bool | short-circuit |

Comparing values of unrelated types is `TP0200`.

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

---

## 6. Built-ins (v0.1)

**Functions**
- `print(*args)` writes each argument's display form, space-separated, then a newline.
- `len(x) -> int` takes a list, dict, or str (an iterator is `TP0200`, with help `convert it with 'list(…)' first`). For a `str` it counts Unicode code points, so `len("🐍")` is `1`.
- `range(stop)` and `range(start, stop)` iterate only (they're not a list).
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
- `isinstance(x, C) -> bool` (M15) is `True` when `x`'s runtime class is `C` or derives from it, as in Python, and `False` for `None`. Since M26, `isinstance(x, (A, B, …))` is `True` when `x` is any of the listed classes. It narrows `x` to the nearest class all of them derive from (§3.9), as `except (A, B) as e` binds (§3.10), and each listed class follows the rules for one (`TP0200` for a class `x` can never be).
  - `x` must be a class or an optional class, and `C` the name of a class (qualified for another module: `zoo.Dog`) that isn't generic. Anything else is `TP0200`. A tuple of classes (`isinstance(x, (A, B))`) is deferred.
  - `C` must derive from `x`'s class, or be it or one of its bases. An unrelated class is `TP0200`, since the test could never be `True`.
  - It narrows `x` (§3.9).
- `super()` (M15) is valid only as `super().m(…)` in a method (§4.3).
- `zip(xs: Iterable[A], ys: Iterable[B], …) -> Iterator[tuple[A, B, …]]` (M12) groups the values of two or more iterables, stopping at the shortest, as in Python: `zip(names, ages, cities)` is an `Iterator[tuple[str, int, str]]`. For each tuple it takes a value from each iterable in turn, left to right, and stops at the first that has none, so `zip(naturals(0), ["a", "b"])` ends. Before M26 it took exactly two lists, and before M31 it gave a new list. One iterable, which would need a one-element tuple, is `TP0209`.
- `sorted(xs: Iterable[T], …)` takes any iterable since M31, and still gives a new list.
- `sorted`, `sort`, `map`, `filter`, `enumerate` and `zip` iterate their lists (§5.2), so a function argument that changes a list panics with `list mutated during iteration`. Since M31 the lazy ones do so when they're iterated.

**Methods**
- `list[T]`: `append(x: T)`, `pop() -> T` (removes and returns the last element; panics if empty), `insert(i: int, x: T)` (Python semantics: a negative `i` counts from the end, and out-of-range indices clamp), `contains(x: T) -> bool`, `sort(key=…, reverse=False)` (M11: sorts in place, with the same rules as `sorted`)
- `dict[K, V]`: `get(k: K) -> V?` (M6), `keys() -> list[K]`, `values() -> list[V]`, `items() -> list[tuple[K, V]]` (M12) (new lists, in insertion order), `remove(k: K) -> bool` (`True` if the key was present)
- `str`: `upper() -> str`, `lower() -> str` (invariant-culture case mapping), `strip() -> str` (removes leading and trailing whitespace), `split(sep: str) -> list[str]` (an empty `sep` panics), `startswith(p: str) -> bool`, `endswith(p: str) -> bool`, `replace(old: str, new: str) -> str` (every occurrence; an empty `old` panics)
- Equality inside `contains`, `in`, `==` and `!=` is structural, as for `==`. Dict equality ignores order.

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
| dataclass | `Service(name="api", replicas=3, healthy=3)`, by runtime class, with inherited fields first |
| plain class | `<Dog object>`, by runtime class (M15) |
| class with `__str__` or `__repr__` | as §4.8 says (M28): `__str__` at top level, `__repr__` inside a container |
| enum | `Ok`, `Degraded(healthy=1, total=4)` |
| function | `<function name>`, `<function <lambda>>` (M11) |
| exception | its message, as Python's `str(e)` (M16). Inside a container, its class and message: `[KeyError("missing key \"a\"")]` |

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

---

## 7. Runtime errors (panics)
- A panic prints `panic: <message>\n  at <file>.tpn:<line>` to stderr and exits with code **101**. Output the program printed before the panic still appears. `<file>` is the source path as given on the command line (§9). `<line>` is the line of the statement that panicked, in the innermost Taipan function.
- The emitted C# carries `#line` directives so .NET stack traces map back to `.tpn` lines.
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

---

## 8. Code generation (C#)

The output is one C# compilation unit per program, compiled in-process by Roslyn with these options:
- `CheckForOverflowUnderflow = true`
- nullable enabled
- warnings not treated as errors in generated code

| Taipan | Emitted C# |
|---|---|
| program | `public static class TaipanProgram { ...constants... static void TopLevel() { ...constant initializers, then top-level stmts... } ...functions... }`, plus `public static class TaipanEntry { static int Main() => Taipan.Runtime.Panic.Run(TaipanProgram.TopLevel); }`. `Panic.Run` turns panics into the §7 output and the exit code. A generated name that clashes with a user name gets a numeric suffix. |
| identifiers | always verbatim-escaped (`@name`) to avoid C# keyword collisions |
| `def f(a: int) -> str` | `public static string @f(long @a)` |
| default values | Each default of a top-level function or method is a static helper method next to it (`connect_port()`, or `scale_by_default()` in a class), called by each call that leaves the parameter out, so every call evaluates its own (M14). A method's helpers are virtual instance methods, overridden along with the method, so a call evaluates the defaults of the method that runs (M15). A nested `def`'s default is emitted inline at each such call. Arguments are passed positionally while each is its own parameter, then by name (`arg1`, `arg2`, … for a nested `def`'s delegate), which C# evaluates in source order. The C# methods have no optional parameters. |
| `@dataclass class S` | `public class S : Taipan.Runtime.IStruct` (or `: Base`), `sealed` when no class derives from it, with a mutable `{ get; set; }` property per own field, a positional constructor over every field that passes the inherited ones to `: base(…)`, methods as instance methods (`self` is `this`), and `ToString` overridden to the display form. Not a C# record: Taipan equality is deep (lists inside dataclasses compare by contents), so `==` on dataclasses, lists and dicts calls `Taipan.Runtime.Equality.Equal` rather than C# `Equals`, and it compares runtime classes first. |
| `class C` (plain, M15) | `public class C : Taipan.Runtime.IObject` (or `: Base`), `sealed` when no class derives from it, with a `{ get; set; }` property per own field and `ToString` giving `<C object>`. C# `Equals` and `GetHashCode` aren't overridden, so `==` and dict keys use reference identity. Construction is `C.@__new__(args)`, a static method that creates the object with a protected parameterless constructor, then calls `__init__`. Each class's `__init__` is an instance method `@__init__` that first evaluates its own fields' defaults (`new` when it hides a base's), so `super().__init__(…)` is `base.@__init__(…)` and can follow the subclass's own field assignments, which a C# constructor's `: base(…)` couldn't. A class without `__init__` gets one that sets its defaults and passes its parameters on to the base's. |
| methods (M15) | `public virtual` where a method is introduced and `public override` for `@override`, with C#'s covariant return types for a narrower class result. `super().m(…)` is `base.@m(…)`. |
| protocols (M17) | Nothing: a protocol has no C# form, and a protocol a class declares is left out of its base list. A bounded generic function is monomorphized (below), so in `largest_Version`, `x > b` is `b.@__lt__(x)`, and in `largest_int` it's `x > b`. |
| `__lt__` (M17) | an ordinary method, `@__lt__`. `a < b` on classes is `a.@__lt__(b)`. `a > b` evaluates `a` and `b` into temporaries, then calls `b.@__lt__(a)`. Sorting a list of classes passes the runtime sort a comparison that calls `__lt__`. |
| special methods (M28) | Ordinary methods: `@__add__`, `@__str__`, …. Every class has virtual `@__str__` and `@__repr__`, with the default forms where it defines neither (`@__str__` calls `@__repr__`), and `ToString` calls `@__str__`. A dataclass overrides `@__repr__` (and its `==` hook) unless it defines its own. |
| `==`, ordering (M28) | Each `__eq__`, `__lt__`, `__le__`, `__gt__` and `__ge__` also overrides a runtime hook, `TryEq(object other, out bool result)` and so on, which tests the parameter's class with `is` and calls the method, returning `false` when it doesn't apply. `Taipan.Runtime.Equality.Equal` and `Ops.Compare` run §4.8's protocol over the hooks, using `Type.IsSubclassOf` for subclass priority. A hashable class overrides C# `Equals` and `GetHashCode` with `Equality.Equal` and `hash()`, so `OrderedDictionary` finds keys as §4.8 says. |
| arithmetic (M28) | `a + b` is `a.@__add__(b)` or `b.@__radd__(a)`. When subclass priority could apply (some class derived from `a`'s static class and `b`'s defines `__radd__`), the operands go into temporaries and `Ops.Prefer(a, b)` tests whether `b.@__radd__(a)` runs instead. |
| properties (M28) | C# properties, `public virtual R @p { get { … } set { … } }`, `override` for `@override`, `abstract` for `@abstractmethod`. |
| static and class methods (M28) | Each class with static or class methods, or a base with them, has a nested class object, `C.@__Class`, deriving from the base's, with a singleton `C.@__class` and the methods as virtual instance methods (`this` is `cls`). `C.m(…)` is `C.@__class.@m(…)`, and `x.m(…)` is `x.@__classobj.@m(…)`, a virtual property each class overrides. `cls(…)` calls a virtual `@__call__` that each class object overrides with its class's construction. |
| class attributes (M28) | `public static T @a { get; set; }` on the declaring class, initialized by its C# static initializer. |
| abstract classes (M28) | `public abstract class`, with `public abstract` methods. An abstract class has no `@__new__`, since nothing constructs it. |
| `Iterable[T]` / `Iterator[T]` (M31) | `IEnumerable<T>`, and the covariant interface `Taipan.Runtime.IIterator<T>`, an `IEnumerable<T>` over one stream: a second loop over it continues where the first stopped. A list, dict, str or class converts to `Iterable[T]` through a runtime wrapper that starts a fresh pass each time, and keeps the mutation check (§5.2). |
| generators (M31) | A C# iterator method returning `IEnumerable<T>`, with `yield return` and `yield break`; the generator function calls it and wraps the result in an `Iterator<T>`, so its body starts at the first value asked for. `yield from e` is a `foreach` that yields each value. |
| `map`, `filter`, `zip`, `enumerate` (M31) | Runtime iterators over `IEnumerable<T>`, which call their function or take from their sources only when asked for a value. |
| `isinstance(x, C)` / narrowed `x` | `x is C`. A narrowed read is `((C)x)`, or `x!` / `x.Value` for `x is not None` on a reference / value type. |
| `x as C` / `x as C?` | `Taipan.Runtime.Ops.Cast<C>(x)`, which panics as §7 says / `x as C` |
| exceptions (M16) | `Exception` is `Taipan.Runtime.Exception`, which derives from `System.Exception` and is emitted against like a plain class's base (`__new__`, `@__init__`, a `message` property); the other built-in exception classes derive from it. A user exception class is a plain class deriving from one of them. Runtime errors throw the matching class, and a Python exception becomes a `PythonError` where the `py` operation calls Python. Checked arithmetic keeps throwing C#'s `OverflowException`, which a `catch` turns into an `OverflowError`. `raise e` is `throw e`. A bare `raise` is `throw Taipan.Runtime.Exceptions.Rethrow(caught)`, which rethrows with `ExceptionDispatchInfo` and so keeps the original stack trace and line. |
| `try` (M16) | A C# `try` with one `catch (System.Exception)` that converts the exception to its Taipan value (or rethrows one that isn't catchable), then tests the `except` clauses in order with `is` (or Python's `isinstance` for a Python class). If none matches, `throw;` rethrows the original. `else` runs after the `try`/`catch`, behind a flag set at the end of the `try` block. `finally` is C#'s `finally`. `Panic.Run` prints an uncaught exception as §3.10 says. |
| class patterns | C# type patterns with property sub-patterns: `case Dog { @name: var n }`. The scrutinee is narrowed as above. |
| function conversion | Converting a function value to a different function type (§3.3) wraps the delegate in an adapter. The new `Function<D>` keeps the original's identity, which its `==` compares, and its display name. |
| `enum E` | `public abstract record E` with a private constructor; each variant is `public sealed record V(...) : E` nested inside |
| `T?` | `T?` |
| `list[T]` / `dict[K, V]` | `List<T>` / `OrderedDictionary<K, V>`. `for` over them goes through a runtime iterator that turns concurrent modification into the §7 panic. |
| `tuple[A, B]` | a C# value tuple `(A, B)` (M12), with elements read as `Item1`, `Item2`, …. Unpacking stores the value in a temporary first. `==` uses `Taipan.Runtime.Equality.Equal`, and ordering uses a runtime comparison that orders strings by ordinal. In generic instance names, a tuple type is written `tuple_` followed by its element types: `first_tuple_int_str`. |
| slices | `Taipan.Runtime.Ops.Slice(target, start, stop, step)`, with `null` for an omitted part (M12) |
| type aliases (M18) | Nothing: each use is its expanded type. |
| native builds (M23) | `--native` (§15.3): each method declares `int __line`, assigns it before each statement, and wraps its body in `try { … } catch (System.Exception e) when (Taipan.Runtime.Panic.Locate(e, "<file>", __line)) { throw; }`, whose filter records the location and returns false. `raise e` is `throw Taipan.Runtime.Exceptions.Raise(e)`, which clears it. |
| libraries (M22) | `--library` (§14): one public static class per module in the library's namespace, with its classes and enums nested inside, instead of `TaipanProgram`, and no `TaipanEntry`. Exported definitions are public and everything else internal. Each module has an internal initializer, called at the start of each exported member, that runs once (§14.3). |
| .NET types and members (M21) | Used directly, by their full names (`global::System.Text.StringBuilder`). Arguments and results convert as §13.3 says: `checked` casts for narrower integers, copies for arrays and enumerables, and a runtime null check on a result that's not annotated as nullable. |
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
| `match` expressions (M19) | A C# `switch` expression with the statement's patterns, each arm `pattern => value`. A raising arm is a `throw` expression. Without a `_` arm, a final `_ => throw new UnreachableException()` arm is emitted, as for the statement. |
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

- **Warnings** (`TP0300`, `TP0301` and `TP0302`) print as `<file>:<line>:<col>: warning TP<code>: <message>`. They don't stop compilation or change the exit code. They're printed with the type checker's errors, before the program runs.

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
| TP0100 | syntax error: expected X, found Y (including `'yield' needs a value`, M31) |
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
| TP0215 | `break`/`continue` outside loop, `return` outside function, a bare `raise` outside `except`, or `return`/`break`/`continue` in `finally` |
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
| TP0300 | warning: unreachable code (a statement, `match` arm or `except` clause) |
| TP0301 | warning: broad `except` clause discards the exception |
| TP0302 | warning: a library's or extension module's definition can't be exported (M22, M25) |

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
| TP0200 | `every arm of this 'match' raises`, with help `use a 'match' statement` (M19) / `this arm gives no value`, with help `end it with an expression` / `<T> cannot match a tuple pattern` / `<T> cannot match a list pattern` / `'*<x>' would hold <A> and <B>` (a starred name in a tuple) (M29) |
| TP0200 | `format spec '<spec>' isn't valid for <T>`, with help naming the part that isn't / `format specs apply to int, float and str, found <T>` (M26) |
| TP0200 | `'<A>' is a type alias, not a value`, with help `use '<N>'` when it names a class or enum (M18) |
| TP0200 | `cannot raise <T>` (with help `write '<C>(…)'` for a class name) / `'<C>' is not an exception class` / `'PythonError' cannot be constructed` (and `'DotNetError'`, M21) (M16) |
| TP0200 | `'<T>' does not satisfy '<P>': it has no method '<m>'` / `'<T>' does not satisfy '<P>': '<m>' must be 'def <m>(self, …) -> <R>'` (the protocol's signature with `Self` replaced; `-> <R>` left out when it returns nothing), at a call's callee or a declared protocol, with ` (declared by '<B>')` after `'<P>'` for an inherited declaration / `'...' can only be the body of a protocol method` / `'<m>' cannot be called directly`, with help `use '<'` (M17) |
| TP0200 | `'super()' can only be used as 'super().<method>(…)'` / `'super()' can only be used in a method` / `'<C>' has no base class` / `'__init__' cannot be called directly` / `'__init__' cannot have a return type` (M15) |
| TP0200 | `'<m>' cannot be called directly`, with help `use '<form>'` (a special method, as M17's `__lt__`) / `dict keys must be hashable, found <C>`, with help as §4.8 says / `cannot hash <T>` / `'+' on <A> and <B> is ambiguous: '<C>.__add__' would run when it fits` / `'<p>' is a property; read it from an instance` / `'<m>' needs an instance of '<C>'` / `'cls' can only be called, or used to reach static and class methods and class attributes` / `'ClassVar' can only annotate a class attribute` / `'<B>.<m>' is abstract` (M28) |
| TP0201 | `undefined name '<x>'` (with help `use 'None'` for `none`) / `undefined type '<T>'` / `module '<m>' has no definition '<x>'` / `.NET namespace '<ns>' has no type '<x>'` (M21) |
| TP0202 | `cannot assign to '<x>': it is a <let binding\|parameter\|loop variable\|constant\|function\|method>`, with help `declare it with 'var'` for a `let` binding / `cannot assign to an element of str` / `cannot assign to an element of a tuple` / `cannot use '<op>' on a slice` / `cannot delete '<x>'`, with help `assign it a new value instead` / `cannot delete a field` / `cannot delete an element of <T>` (M26) / `cannot assign to '<p>': it is a read-only property` / `cannot assign to '<a>' through '<x>'`, with help `assign '<C>.<a>'` (M28) |
| TP0203 | `<T>? can only be matched by '_' or a binding`, with help `'None' patterns aren't supported yet; bind it and test it with 'is some'` (a pattern, M29) / `expected <T>, found <T>?` / `<T>? must be narrowed before use`, with help `narrow it with 'is some', or give a default with '??'`, or one saying why a name or path isn't narrowed there (§3.9, M27) |
| TP0204 | `non-exhaustive match (missing: <a>, <b>)`, each a variant, literal or, since M29, a pattern such as `Degraded(0, _)` or `[_, *_]` (§5.1) |
| TP0205 | `condition must be bool, found <T>` (with help `convert it with 'as bool'` for `py`) |
| TP0206 | `cannot infer the type of an empty list` / `... empty dict` / `cannot infer the type of None` / `cannot infer '<T>' for '<f>'` / `cannot infer the type of lambda parameter '<x>'`, with help `add a type annotation` |
| TP0207 | `'<x>' shadows an outer binding` |
| TP0208 | `not all paths in '<f>' return a value` |
| TP0209 | `'<S>' takes <n> type arguments, found <m>` (`type argument` when n is 1) |
| TP0209 | `'<v>' has <n> fields, found <m> bindings` (variant and dataclass patterns; `field` / `binding` when the count is 1) / `'<C>' is not a dataclass, so its fields can only be bound by name` / `field '<f>' is given more than once` (class patterns, M15) |
| TP0209 | `<T> has <n> elements, found <m> names` (unpacking) / `a tuple needs at least two elements` (M12) / `<T> has <n> elements, found <m> patterns` (a tuple pattern, M29) |
| TP0209 | `'<f>' takes <n> arguments, found <m>` (`argument` when n is 1; `1 or 2 arguments` for `range`; `<a> to <b> arguments` for a function with defaults, M14; `2 or more arguments` for `zip`, M26; `function takes …` when the callee isn't a name) / `missing argument '<x>'` (M14) / `missing field '<f>' in '<S>'` / `field '<f>' is given more than once` / `argument '<k>' is given more than once` |
| TP0209 | `'<f>' takes <n> positional arguments, found <m>` / `missing keyword-only argument '<x>'` / `'*<e>' can only fill '*<args>'` / `'<f>' has no '*args' for '*<e>'` / `'<f>' has no '**kwargs' for '**<e>'` / `'**<e>' can't fill '<x>'` (M30) |
| TP0209 | `expected a lambda with <n> parameters, found <m>` (`parameter` when n is 1) / `'Callable' takes a parameter list and a result type` |
| TP0210 | `<T> has no field '<x>'` / `<T> has no method '<x>'` / `<E> has no variant '<x>'` |
| TP0211 | `'<f>' cannot use top-level variable '<x>'`, with help `pass it as a parameter, or make it a const` |
| TP0213 | `optional types cannot be nested` |
| TP0214 | `duplicate definition of '<x>'` |
| TP0215 | `'yield' outside a function` / `'yield' can't be inside a 'try' with 'except', or in 'finally'` (M31) / `'<k>' can't leave a 'match' expression`, with help `use a 'match' statement` (M29) / `'break' outside a loop` / `'continue' outside a loop` / `'return' outside a function` / `'raise' without an exception outside an except block` / `'return' cannot be used in a finally block` (and `'break'`, `'continue'`) (M16) |
| TP0216 | `'<v>' is ambiguous: it is a variant of <E1> and <E2>`, with help `qualify it, e.g. '<E1>.<v>'` |
| TP0217 | `const initializer must be a constant expression` / `class attribute value must be a constant expression` (M28) |
| TP0218 | `'<p>' is positional-only, so it can't be passed by keyword` (M30) / `'<f>' takes no keyword arguments` (a built-in) / `a function value takes no keyword arguments` / `'<f>' has no parameter '<k>'` (M14; also for `sorted`, `sort` and `enumerate`) |
| TP0219 | `instantiating '<f>' needs '<f>[<T>]', which is too deep`, at the call that starts the chain |
| TP0221 | `module '<m>' not found`, with help `looked for <path>` / `'python' is not a module`, with help `use 'from python import <module>'` / `'dotnet' is not a module`, with help `use 'from dotnet.<namespace> import <type>'` / `.NET namespace '<ns>' not found` (M21) |
| TP0222 | `only the main file can have top-level statements` / `a library can't have top-level statements` (M22) / `a Python extension can't have top-level statements` (M25) |
| TP0223 | `nested functions cannot have type parameters` |
| TP0224 | `unknown decorator '@<d>'` / `'@<d>' cannot be used here` (with help `'@dataclass' goes on a class, '@override' on a method`) / `duplicate decorator '@<d>'` / `'@<d>' cannot be used here`, with help `'@<d>' goes on a method`, for a method's decorator elsewhere / `'@<a>' cannot be combined with '@<b>'` / `'@abstractmethod' needs a class deriving from 'ABC'` / `'@<x>.setter' needs a property '<x>' just above it` (M28) |
| TP0225 | `a class can have only one base class` (protocols aside, M17) / `'<T>' cannot be a base class` / `a dataclass can only derive from a dataclass` (with help `exceptions are plain classes; give it an '__init__'` for an exception base, M16) / `a plain class can only derive from a plain class` / `generic classes cannot be derived from` / `generic classes cannot have a base class` / `'<C>' derives from itself` / `special method '<m>' is not supported` / `a dataclass cannot define '__init__'` / `field without a default follows field with a default` / `'<m>' must be 'def <m>(self, …) -> <R>'` (M28, replacing M17's `'__lt__' must take one parameter of type '<C>' (or a base class) and return bool`) / `'<m>' must take 'self' first` (or `'cls'`) / `a static method takes neither 'self' nor 'cls'` / `a property takes no parameters other than 'self'` / `a property must have a return type` / `a setter must follow its property` / `a setter must take one parameter of type <T> and return nothing` / `class attribute '<a>' needs a value` / `generic classes cannot have <static methods\|class methods\|class attributes>` / `an abstract method's body must be '...'` / `'<C>' would not be hashable, but its base '<B>' is`, with help `define '__hash__'` (M28) / `'<P>' is already declared` (or `already declared by '<B>'`) / `a protocol can only declare methods` / `a protocol method's body must be '...'` / `protocol method parameters cannot have defaults` / `protocols cannot be generic` / `a protocol can only derive from 'Protocol'` (M17) |
| TP0226 | `'__init__' does not set '<f>'` / `'<f>' has no default, and '<C>' has no '__init__'` / `'<f>' is read before it is set` / `'self' is used before '<f>' is set` (or `before 'super().__init__' is called`) / `'<f>' is inherited, so it is set by 'super().__init__'` / `'super().__init__' is called before '<f>' is set` / `'<C>.__init__' must call 'super().__init__(…)'` / `'super().__init__' must be called once, directly in '__init__'` / `'super().__init__' can only be called in '__init__'` / `'__init__' returns before '<f>' is set` (or `before 'super().__init__' is called`) (when several fields are unset, the first in declaration order is named) |
| TP0227 | `'<m>' overrides '<B>.<m>'; mark it '@override'` / `'<m>' does not override a method of <a base class\|'<B>'>` / `'__init__' cannot be marked '@override'` |
| TP0228 | `'<m>' must take the same parameters as '<B>.<m>'` / `parameter '<p>' of '<m>' must have type <T>, as in '<B>.<m>'` / `parameter '<p>' of '<m>' needs a default, as in '<B>.<m>'` / `'<m>' must return <T>, as '<B>.<m>' does` (or `return nothing`) / `'<m>' is a <static method\|class method\|method\|property> in '<B>'` / `'<p>' must have a setter, as '<B>.<p>' does` / `'<D>.<m>' must return <R>, since it can run in place of '<B>.<op>'` / `'<D>' must be constructed as '<C>' is, since '<C>.<m>' calls 'cls(…)'` (M28) |
| TP0234 | `cannot instantiate abstract class '<C>'`, with help `it does not implement '<B>.<m>'` (`'<B>.<m>' and '<B>.<n>'` for more) / `'cls(…)' may construct the abstract class '<C>'` / `'<C>' cannot add abstract method '<m>', since '<B>.<cm>' calls 'cls(…)'` (M28) |
| TP0220 | `a constraint needs at least two types` / `'<T>' is listed twice in the constraint` / `a constraint cannot be optional or generic` / `constraints are only supported on functions` / `'<T>' is not a protocol` (with help `class bounds are not supported`, for a class) / `bounds are only supported on functions` (M17) |
| TP0229 | `'<P>' is a protocol, so it can only be a type parameter bound`, with help `use a bounded type parameter, e.g. 'def f[T: <P>](x: T)'` (M17) |
| TP0231 | `'<T>.<m>' uses <N>, which Taipan can't use yet` (`<N>` the .NET type, e.g. `Dictionary<string, int>` or `Func<int, bool>`) / `'<N>' can't be imported: generic .NET types aren't supported yet` (M21) |
| TP0232 | `ambiguous call to '<T>.<m>': it could be '<m>(<P1>)' or '<m>(<P2>)'` (M21) |
| TP0233 | `a native program can't use Python`, with help `build it without '--native'` / `'<T>.<m>' can't be used in a native program: <message>` (M23) / `a Python extension can't use Python interop itself` (M25) |
| TP0230 | `type alias '<A>' refers to itself` / `type alias '<A>' refers to itself through '<B>'` (`'<B>' and '<C>'` for longer cycles), with help `use a class for a recursive type` (M18) |
| TP0300 | `unreachable match arm` / `unreachable except clause`, with help `'<B>' is already caught above` / `unreachable code` (M16) |
| TP0301 | `this clause catches every exception and discards it`, with help `catch a specific class, re-raise with 'raise', or bind it with 'as e' and use it` (M16) |
| TP0302 | `'<f>' is not exported: <reason>`, where `<reason>` is `its type parameter '<T>' has a bound` / `'<T>?' has no .NET generic form` / `it uses '<g>', which isn't a .NET generic` / `it uses '<C>', which isn't exported` / `it has its module's name` (M22) / `'<T>' can't be passed to Python` / `it's generic` (M25), with help `rename it '_<f>' to keep it private` |

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
- A Python exception during any `py` operation raises a `PythonError` (M16, §3.10), with the Python class's name, the message (Python's `str(e)`) and the exception object. `except json.JSONDecodeError` catches one by its Python class. Uncaught, it panics with `Python <ExceptionType>: <message>`.
- If Python can't be loaded, the first Python import panics with `cannot load Python: <reason>`. A program without Python imports starts Python at its first conversion to `py` (`let p: py = [1, 2]`), which panics the same way.
- All of these exit with 101 and report the Taipan line, as other panics do (§7).

### 10.6 Locating and running Python
- The runtime embeds CPython through Python.NET (pythonnet). It looks for the CPython shared library in this order:
  1. the `PYTHONNET_PYDLL` environment variable (pythonnet's own setting)
  2. the library reported by the first of `python3` or `python` found on `PATH` (`sysconfig`'s `LIBDIR` and `LDLIBRARY`)
- Python is initialized once per process and never shut down. The GIL is taken around each Python operation, so `taipan run` can run programs on different threads of one process.

---

## 11. Deferred (post-v0.1)
- protocols as types of values, protocol fields, generic protocols, protocols extending other protocols, and a check that a class used with a protocol declares it
- class bounds (`[T: Animal]`), bounds on classes' and enums' type parameters, and built-in types' ordinary methods satisfying protocols
- special methods beyond §4.8 (`__bool__`, `__len__`, `__getitem__`, `__contains__`, `__call__`, `__ne__`, `__pow__`, in-place `__iadd__` and the like, `__format__`), special methods in protocols other than `__lt__`, and `functools.total_ordering`
- multiple inheritance, mixing dataclasses and plain classes in one hierarchy, and inheritance involving generic classes
- `@dataclass` options (`frozen`, `order`), `__post_init__`, and an `__init__` written in a dataclass
- `@final`, `Self` outside protocols (so a class method's result is its declared class, not `cls`'s), static and class methods and class attributes on generic classes, abstract static and class methods, property deleters, and static and class methods as values
- `isinstance` on `py` values, and `is` between two expressions (identity)
- generic functions, built-ins, methods and constructors as values (wrap them in a lambda)
- generator `send`, `throw` and `close`, `yield` as an expression, generator return values, `Generator[Y, S, R]`, `yield` inside `try`/`except`, generator expressions and comprehensions, and `async` iterators
- or-patterns (`A | B`), guards (`case x if …`), `as` patterns, `None` and float literal patterns, mapping patterns, and `*` in tuple patterns (M29 has them in list patterns only)
- type aliases inside functions and classes, recursive aliases, and showing an alias's name in diagnostics
- defaults on lambda parameters (not planned, since loop variables are captured per iteration, §3.7), typed `**kwargs` (`Unpack[TypedDict]`), `*args` and `**kwargs` in function types, and `**e` filling named parameters
- narrowing elements (`xs[0]`) and paths rooted at a `var`, and narrowing that follows `==` comparisons with literals
- `raise … from …`, exception chaining, `BaseException`, exception groups (`except*`), and printing tracebacks
- dataclass exceptions (mixing dataclasses and plain classes), and `args` on exceptions
- nested format specs in f-strings (`{x:{width}}`), the locale-dependent `n` format type, and format specs for other types (`__format__`)
- language server features beyond §16: formatting, code actions, incremental sync, workspace-wide diagnostics, semantic token deltas and ranges, completion of .NET namespaces and Python modules' names, signature help for built-in functions, references to built-in, Python and .NET names, renaming modules (renaming their files), and editors other than VS Code
- CPython extension modules beyond §17: classes and enums as Python types, Python functions and objects as arguments, Python interop inside an extension, generic functions, free-threaded CPython, and builds for one exact Python version
- .NET interop beyond §13: generic methods and generic .NET types (other than the collections of §13.3), delegates and passing Taipan functions to .NET, `async` and `Task`, events, `ref`/`out`/`in` parameters, `params` arrays, indexers, operators, extension methods (LINQ), `object` and `decimal` results, dictionaries, deriving from .NET classes or implementing .NET interfaces, raising .NET exceptions, `isinstance`/`as`/`match` on .NET types, flags enums, namespace imports (`import dotnet.System.IO as io`), and packages with native assets
- an MSBuild project SDK (`TaipanLang.Sdk`), for building `.tpn` files with `dotnet build` as part of a .NET solution
- native builds beyond §15: another architecture (`--runtime <rid>`), Python in native programs, native libraries, and size options such as invariant globalization
- Taipan libraries beyond §14: a configurable namespace, assembly name and version (in `taipan.json`), PascalCase names, XML documentation, value `Equals` and `GetHashCode` for dataclasses, deriving from Taipan classes in other .NET languages, plain delegates for function types, and packing a library as a NuGet package

---

## 12. The `taipan` tool (M20)

### 12.1 Commands
```
taipan check <file> [<kind>]                         type-check only, printing diagnostics
taipan emit  <file> [<kind>]                         print the generated C# (§8)
taipan build <file> [<kind>] -o <dir>                write a runnable program to <dir>, or with <kind>:
                                                     --library (M22, §14), --native (M23, §15),
                                                     or --python-extension (M25, §17)
taipan run   <file> [-- args]                        compile and run
taipan lsp [--stdio]                                 run the language server on stdin and stdout (M24, §16)
taipan --version                                     print "taipan <version>", e.g. "taipan 0.2.0"
taipan --help                                        print the usage text
```
- `--version` and `--help` print to stdout and exit `0`. Each takes no other arguments.
- No arguments, an unknown command or option, or wrong arguments to a command print a one-line error (if there is one) and the usage text to stderr, and exit `2` (§9).
- `build -o <dir>` writes `<name>.dll` with `Taipan.Runtime.dll`, `Python.Runtime.dll` and a `runtimeconfig.json`, so `dotnet <dir>/<name>.dll` runs it on the .NET 10 runtime.

### 12.2 Packages
Taipan is distributed as two NuGet packages, with the same version:

| Package | Contents |
|---|---|
| `TaipanLang` | the `taipan` command, as a .NET tool: the compiler, the runtime and its dependencies (Roslyn, pythonnet) |
| `TaipanLang.Runtime` | the `Taipan.Runtime` library that compiled programs reference, depending on `pythonnet` |

- `TaipanLang` installs with `dotnet tool install -g TaipanLang`, which puts `taipan` on the path. It runs on the .NET 10 runtime, and compiles programs against that runtime's assemblies. It needs no SDK.
- The NuGet ID `taipan` belongs to another project, hence `TaipanLang`. Assembly and namespace names don't change: the runtime is still `Taipan.Runtime.dll`, namespace `Taipan.Runtime`.
- `TaipanLang.Runtime` isn't needed to run programs, since the tool carries the runtime. It's published for Taipan libraries referenced from other .NET code (M22).
- **Version:** one version for both packages and `taipan --version`, set in one place in the build. M20's is `0.2.0`, the first packaged release after v0.1.
- `dotnet pack -c Release -o <dir>` at the repository root builds both packages.
- An installed tool behaves exactly as a source build, Python interop included: CPython is found as §10.6 says, at run time.
- Publishing to nuget.org is done by hand and isn't part of the build.

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
- A type may be a class, a static class, a struct, an interface or an enum. It must be public, non-generic and not nested. Nested types are reached through the type containing them: `Environment.SpecialFolder.Desktop`.
- Imported names share the file's global scope, like classes (§4.7): a clash is `TP0214`. They live in the type namespace, as classes do, so `let sb: StringBuilder = StringBuilder()` names the type twice: once as a type and once as its constructor.
- `import dotnet`, and `from dotnet import X`, are `TP0221`, with help `use 'from dotnet.<namespace> import <type>'`. A namespace no referenced assembly defines is `TP0221`. A type the namespace lacks is `TP0201`. A generic type is `TP0231`.
- Only types are imported. A namespace can't be imported or used as a value.

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
| `object` | any value | an `int`, `float`, `bool`, `str` or .NET value (not a Taipan list, dict, tuple, class, function, `py` or `None`, whose .NET forms are internal) | `TP0231` |
| other public, non-generic classes, structs, interfaces and enums | the same type, opaque | | |

- `T` in a row stands for any type in the table, so `string[]` is `list[str]` and `List<int?>` is `list[int?]`. `List<T>` is passed as is only when `T` maps without conversion (`long`, `double`, `bool`, `string` or an opaque type); any other `List<T>` is copied, both ways.
- **Anything else is `TP0231`**, reported where a member using it is called, read or assigned: other generic types (`Dictionary<K, V>`, `Task<T>`, `Span<T>`), delegates (`Func<…>`, `Action<…>`), `decimal`, `Int128`, `BigInteger`, `nint`, pointers, `ref`, `out` or `in` parameters, and generic methods (named by their type parameter: `'Array.Empty' uses T`). Members that use such types can be present, as long as they aren't used.
- **Nullability.** A reference type annotated as nullable (`string?`) is `T?`, and one annotated as not null is `T`. A result of a not-null type that is null anyway, since annotations aren't checked by .NET, raises `TypeError` (`'<T>.<m>' returned null`) at the boundary, rather than surfacing later as a null in Taipan. Elements of a not-null element type (`string[]`) are checked the same way when the list is copied. In an assembly without nullable annotations, reference-type results are `T?` and reference-type parameters take `T`.
- **Opaque types** are written by their imported name in annotations and type arguments: `list[FileInfo]`, `Uri?`. A value of a .NET type that wasn't imported (such as a method's result) can still be used. Its type is displayed by its C# name without the namespace (`FileInfo`, `Environment.SpecialFolder`).
- `==` and `!=` on two values of the same .NET type use .NET's `Equals`. Ordering, arithmetic, `in`, iteration, indexing, `match`, `isinstance` and dict keys don't apply (`TP0200`). `print`, `str()` and f-string holes use .NET's `ToString()`.
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

### 13.5 Overloads
.NET methods and constructors are often overloaded. A call picks one overload, by this rule:
1. **Candidates** are the public overloads with that name, static or instance as the call requires, skipping any that use a type `TP0231` covers.
2. A candidate is **applicable** if the arguments match its parameters by position and keyword (§4.2), and each argument converts to its parameter (§13.3).
3. **Each argument's fit** is, best first:
   - *exact*: its Taipan type is the parameter's mapped type with no conversion (`int` to `long`, `float` to `double`, `str` to `string`, a value to its own .NET type, a list to `List<T>` or an interface of it, `None` or a `T` to a nullable `T`)
   - *conversion*: `int` to a narrower integer, `str` to `char`, a .NET value to a base class or interface, a list copied to an array or with its elements converted
   - *float conversion*: `int` to `double` or `float`, and `float` to `float`
   - *object*: any value to `object`
4. A candidate is **better** than another if each of its arguments fits at least as well and one fits better, or if they fit the same and it leaves out fewer parameters with defaults.
5. **The overload chosen** is the applicable candidate no other is better than. If there are several such, the call is `TP0232`, naming two of them.

So `sb.Append(5)` calls `Append(long)`, `sb.Append("x")` calls `Append(string)` (not `Append(object)`), `Math.Max(1, 2)` calls `Max(long, long)`, and `TimeSpan.FromDays(2)` calls `FromDays(int)`, not `FromDays(double)` or `FromDays(int, int = 0, …)`. These are the overloads C# picks, too.

- If no candidate has the right number and names of arguments and there's only one candidate, its errors are reported as for a Taipan function (`TP0209`, `TP0218`). If exactly one candidate has them, each argument is checked against its parameter (`TP0200` at the argument). Otherwise, with no applicable candidate, it's `TP0200`, `no overload of 'StringBuilder.Append' takes (list[int])`.
- If the only candidates with that name use types `TP0231` covers, the call is `TP0231`, naming a type that keeps the first one out.

### 13.6 Exceptions
- **Catching by .NET class:** `except C`, where `C` is an imported .NET class deriving from `System.Exception`, matches a .NET exception of that class or a class deriving from it, as for Taipan classes. `except C as e` binds `e` with type `C`, so its .NET members can be used (`e.Message`, `e.FileName`). Listing several .NET classes binds their nearest common .NET base. A clause can't list both Taipan and .NET classes with `as` (`TP0200`): write separate clauses.
- **Catching as `Exception`:** a .NET exception reaching `except Exception`, a bare `except:` or any other Taipan class's clause is a `DotNetError` (§3.10), with `type` the .NET class's full name and `message` the .NET exception's `Message`. So `except Exception` still catches everything.
- .NET's `OverflowException` is Taipan's `OverflowError`, as for checked arithmetic (§8), whichever code throws it.
- Unreachable clauses (`TP0300`) are checked for .NET classes too.
- **Uncaught**, a .NET exception panics with `.NET <full class name>: <message>`, as a Python exception does (§10.5), and exits with 101.
- Raising .NET exceptions is deferred.

### 13.7 Deferred
§11 lists what's deferred: generic methods and most generic types, delegates and passing Taipan functions to .NET, `async`, events, `ref`/`out`/`in`, `params` arrays, indexers, operators, extension methods, dictionaries, deriving from .NET types, raising .NET exceptions, and type tests on .NET types.

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
- **The assembly** is `<name>.dll`, where `<name>` is the main file's name without `.tpn`.
- **The namespace** is `<name>` in PascalCase: each `_`-separated part capitalized and the underscores dropped, so `geometry` is `Geometry` and `health_checks` is `HealthChecks`.
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
| protocols, type aliases | nothing, as in a program (§8) |

- **Types in signatures** are §8's: `int` is `long`, `float` is `double`, `bool`, `str`, `T?` (`long?` or `string?`), `list[T]` is `List<T>`, `dict[K, V]` is `OrderedDictionary<K, V>`, `tuple[A, B]` is a value tuple, a function type is `Taipan.Runtime.Function<D>` (constructed from a delegate: `new Function<Func<long, long>>("twice", x => x * 2)`), `py` is pythonnet's `PyObject`, `Iterable[T]` is `IEnumerable<T>`, which a C# caller may pass any sequence as, and `Iterator[T]` is `Taipan.Runtime.IIterator<T>`, an `IEnumerable<T>` it can loop over once (M31), and .NET types are themselves. Lists and dicts are shared, not copied, both ways.
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
§11 lists what's deferred: a configurable namespace, assembly name and version, PascalCase names, XML documentation from docstrings, `Equals` and `GetHashCode` on dataclasses, deriving from Taipan classes in other .NET languages, delegates for function types, and publishing a library as a NuGet package.

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
- **The platform** is the operating system and architecture `taipan` runs on, by its portable runtime identifier, such as `linux-x64`, even where a distribution's own build of .NET reports its own (`ubuntu.24.04-x64`). NativeAOT can't build for another operating system; choosing another architecture is deferred.
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

### 15.3 Panic locations
A native executable has no debug information to map its stack to `.tpn` lines, so for `--native` the compiler records lines itself (§8):
- Each emitted method (functions, methods, nested `def`s, lambdas, default helpers and the top level) keeps the `.tpn` line of the statement it's running in a local variable.
- An exception filter around each method's body records that line, and the method's file, on an exception the first time one passes through, so the innermost Taipan frame is the one recorded. The filter never catches the exception.
- `raise e` clears what `e` recorded, so it reports its own line, and a bare `raise` keeps it (§3.10).
- A panic prints the recorded location, as `taipan run` prints the one from the stack trace.

This adds a local store per statement and a filter per method; it's emitted only for `--native`.

### 15.4 Deferred
§11 lists what's deferred: building for another architecture (`--runtime <rid>`), Python in native programs, native libraries (`--library --native`), and size options such as invariant globalization.

---

## 16. Language server (M24, M32)
`taipan lsp` is a Language Server Protocol (LSP 3.17) server for editors. It reports the compiler's diagnostics as you type, shows a name's type on hover, and goes to a name's definition, across modules too. Since M32 it also completes names, shows a call's signature, finds a name's references, renames across modules, and gives semantic tokens (§16.4–§16.7), and the repository has a VS Code extension that starts it (§16.8).

### 16.1 Protocol
- **Transport:** JSON-RPC 2.0 over stdin and stdout, each message framed by a `Content-Length` header, as the LSP specifies. `taipan lsp` takes no arguments but `--stdio`, which clients such as VS Code's pass and which names the only transport. It writes nothing else to stdout.
- **Lifecycle:** `initialize`, `initialized`, `shutdown` and `exit`. `exit` ends the server with code `0` after `shutdown`, and `1` without it, as the LSP specifies.
- **Capabilities** the server declares: full document sync with open and close notifications (`textDocumentSync: 1`), `hoverProvider` and `definitionProvider`; since M32, `completionProvider` (trigger character `.`), `signatureHelpProvider` (trigger characters `(` and `,`), `referencesProvider`, `renameProvider` (with `prepareProvider`), and `semanticTokensProvider` (`full` only, with §16.7's legend). Positions are in UTF-16 code units, the LSP's default; Taipan's own columns count code points (§9), and the server converts.
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
  | function | `def scale(p: Point, by: float = …) -> Point`, with type parameters (`def first[T](xs: list[T]) -> T?`) and parameter kinds as written (§4.2): `def log(msg: str, /, *args: int, sep: str = …, **kwargs: str)` (M32) |
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
  | after `from m import`, or after a comma there | the names `m` exports (§4.7), with an enum's variants; nothing for `python` and `dotnet` modules |
  | after `.`, on a value of a class | its fields, properties and methods, inherited ones included, and its `ClassVar` attributes, but not static or class methods, or special methods (§4.8) |
  | after `.`, on a class's name | its static and class methods and `ClassVar` attributes |
  | after `.`, on an enum's name | its variants |
  | after `.`, on a module | the names it exports, as after `from m import` |
  | after `.`, on a `list`, `dict` or `str` | §6's methods for it |
  | after `.`, on a .NET value or type | its public instance members, or a type's public static members (§13.4) |
  | after `.`, on anything else (a `py` value, a tuple, a number) | nothing |
  | in a type (after `:` in a binding or parameter, after `->`, inside a type's `[…]`, after `type X =`) | the types in scope: built-in types (§3), classes, enums, protocols, type aliases, type parameters, imported .NET types, and modules (for `m.Point`) |
  | anywhere else a name may go | the names in scope at the cursor, then keywords: every keyword that starts a statement at a statement's start, and `None`, `True`, `False`, `not`, `lambda` and `match` inside an expression |

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
  | module, a Python module (§10) | `Module` (9) |
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
  | a module | `cannot rename the module '<x>'; rename its file instead` |
  | a special method (§4.8), `__init__` included | `cannot rename the special method '<x>'` |
  | a new name that isn't an identifier (§1.3), or is `_` | `'<new>' is not a valid name` |
  | a new name that's a keyword (`type` isn't) | `'<new>' is a keyword` |
  | a new name that's a special method's (`__x__`) | `cannot rename to the special name '<new>'` |
  | a file in the module root that doesn't parse, since its references can't be found | `cannot rename while <path> has syntax errors` |
  | a rename that would add an error: one more error with some code in some file of the module root, when it's checked again with the changes | `renaming '<x>' to '<new>' would cause an error: <path>:<line>:<col>: <code>: <message>`, naming the first such error |

  The last row catches clashes (`TP0214`), shadowing (`TP0207`), and a class that stops satisfying a protocol (`TP0225`), among others, so a rename never turns a program that checked into one that doesn't. Paths in these messages are relative to the document's directory.

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
- **Where.** The extension lives in `editors/vscode`, in plain JavaScript with no build step. Its one runtime dependency is `vscode-languageclient`, Microsoft's LSP client. Its development dependencies are `@vscode/vsce`, which packages it, and `vscode-textmate` and `vscode-oniguruma`, which its grammar test runs on, the engine VS Code itself uses.
- **What it contributes:**
  - the language `taipan`, for `.tpn` files;
  - a language configuration: `#` comments, bracket pairs, auto-closing brackets and quotes, and indenting after a line ending in `:`;
  - the TextMate grammar `source.taipan` (below);
  - the setting `taipan.path`, the `taipan` command to start, `"taipan"` by default (found on `PATH`).
- **It starts** `<taipan.path> lsp --stdio` when a `.tpn` file is opened, as the client for every Taipan document, and stops it when VS Code closes.
- **It packages** with `vsce package` into `taipan-<version>.vsix`, with the extension's own files and its runtime dependency only.
- **The grammar** colors code without the server, so a file is colored as soon as it opens. It gives these scopes, each ending in `.taipan`:

  | Text | Scope |
  |---|---|
  | `# …` | `comment.line.number-sign` |
  | `"…"`, `'…'` | `string.quoted.double`, `string.quoted.single`, with escapes `constant.character.escape` |
  | `f"…"` | `string.interpolated`, with `{{` and `}}` as escapes; each hole `meta.interpolation`, with its braces `punctuation.section.interpolation.begin` and `.end`, its conversion (`!r`) and format spec (`:>8`) `storage.type.format`, and the expression inside colored as code |
  | integers, floats | `constant.numeric.integer`, `constant.numeric.float` |
  | `True`, `False`, `None` | `constant.language` |
  | `if elif else while for match return pass break continue try except finally raise yield`, and a `for` header's `in` | `keyword.control.flow` |
  | `import from as` | `keyword.control.import` |
  | `and or not is in some` | `keyword.operator.logical` |
  | `def`, `lambda` | `storage.type.function` |
  | `class`, `enum` | `storage.type.class` |
  | `let var const`, and `type` starting an alias | `storage.modifier.declaration` |
  | `del` | `keyword.other.del` |
  | the name after `def` | `entity.name.function` |
  | the name after `class` or `enum` | `entity.name.type` |
  | `@name` | `entity.name.function.decorator` |
  | `self`, `cls` | `variable.language.special` |
  | §6's built-in functions; `int`, `float`, `str` and `list` only before `(`, since they're types otherwise | `support.function.builtin` |
  | built-in types (`int float str bool list dict tuple Iterator Iterable Callable`) | `support.type.builtin` |
  | `+ - * / // % ??` | `keyword.operator.arithmetic` |
  | `== != < <= > >=` | `keyword.operator.comparison` |
  | `= += -= *= /=` | `keyword.operator.assignment` |
  | `->` | `punctuation.separator.annotation.result` |

- **The golden token list.** `editors/vscode/test/sample.tpn` exercises every row above, and `sample.tokens` lists the tokens the grammar gives it, one per line: `<line>:<column> <text> <scopes>`, with columns from 1, the text as a JSON string, and the token's scopes after `source.taipan`, space-separated (`-` when there are none). Tokens of only whitespace are left out.

### 16.9 Deferred
§11 lists what's deferred: formatting, code actions, incremental document sync, workspace-wide diagnostics, semantic token deltas and ranges, completing .NET namespaces and Python modules' names, signature help for built-ins, references to built-in, Python and .NET names, renaming modules, and editors other than VS Code.

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
- **Restrictions:** an extension module runs natively, so §15.2 applies (`TP0233`): it can't use Python interop (§10) itself, or .NET members that need run-time code generation. Like a library's main file (§14.3), it contains declarations only, so a top-level statement is `TP0222`.
- `--python-extension` can't be combined with `--library` or `--native` (a usage error). `taipan check <file> --python-extension` and `taipan emit <file> --python-extension` check and emit the module as for a build, with its diagnostics.

### 17.2 What's exported
The module file's own definitions whose names don't start with `_` (§14.1), as module attributes:
- **Functions** whose parameter and result types are all *convertible* (§10.3), other than `py`: `int`, `float`, `bool`, `str`, and lists, dicts, tuples and optionals of those, or no result. Each is a Python built-in function (`geometry.area`).
- **Constants** of convertible types, as attributes holding their values (`geometry.PI`), set when the module is imported.
- **Exception classes** (§3.10) defined in the file, as Python exception classes named `<name>.<Class>` (`geometry.NegativeRadius`). Each derives from the Python class of its Taipan base: the module's own class for an exported exception class, or the built-in of the same name (§17.4), so `except ValueError` catches a Taipan class deriving from `ValueError`. A base that isn't exported (`_Base`) is skipped for its nearest exported or built-in ancestor.
- **Not exported (`TP0302`):** a function or constant that uses a type that isn't convertible, such as a class, an enum or a function type, is warning `TP0302`, with `'<T>' can't be passed to Python` as the reason, as is a generic function (`it's generic`). Other classes and enums aren't exported, and aren't warned about.

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

### 17.5 Deferred
§11 lists what's deferred: classes and enums as Python types, passing Python functions or objects (`py`) to Taipan functions, Python interop inside an extension module, generic functions, free-threaded CPython, and builds for one exact Python version.
