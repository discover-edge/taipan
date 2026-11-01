# Taipan Language Specification — v0.1 (draft)

Taipan is a statically typed, compiled language with Python's surface syntax. The compiler is written in C# (.NET 10). It transpiles to C# and compiles the result in-process with Roslyn.

**Design goals, in priority order:**
1. Reads like Python. A Python developer should understand any Taipan program on sight.
2. Sound static typing. No `null` surprises, exhaustive `match`, no runtime type errors outside the Python boundary.
3. Annotate function boundaries only. Locals are inferred.
4. Compiled. The output is a .NET assembly, with NativeAOT as a later option.
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
- There is no backslash line continuation.
- At EOF, the lexer emits `NEWLINE` if needed, then a `DEDENT` for each remaining stack level, then `EOF`.

### 1.3 Tokens
- **Comments:** `#` to end of line.
- **Identifiers:** `[A-Za-z_][A-Za-z0-9_]*`. A lone `_` is the wildcard pattern only.
- **Keywords:**
  `def class enum let var const if elif else while for in match return pass break continue and or not is some None True False as from import lambda try except finally raise`
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
| TP0006 | `single '}' is not allowed in an f-string` / `empty f-string hole` / `f-strings cannot be nested` |
| TP0007 | `invalid number literal` |

---

## 2. Grammar (EBNF)

Terminal names in CAPS come from the lexer.

```ebnf
file          = { NEWLINE | top_item } EOF ;
top_item      = import_stmt | func_def | class_def | enum_def | const_def | type_alias | stmt ;

type_alias    = "type" NAME [ type_params ] "=" type NEWLINE ;           (* M18, §3.12; "type" is a soft keyword *)

import_stmt   = "import" NAME [ "as" NAME ] NEWLINE                                  (* M9, §4.7 *)
              | "from" NAME "import" import_name { "," import_name } NEWLINE ;
import_name   = NAME [ "as" NAME ] ;

const_def     = "const" NAME [ ":" type ] "=" expr NEWLINE ;

func_def      = { decorator } "def" NAME [ type_params ] "(" [ params ] ")" [ "->" type ] ":" block ;
decorator     = "@" NAME NEWLINE ;                                   (* M15: @dataclass, @override; §4.3 *)
params        = param { "," param } [ "," ] ;
param         = NAME ":" type [ "=" expr ] ;                         (* default: M14, §4.2 *)
type_params   = "[" type_param { "," type_param } "]" ;             (* M8, §3.6 *)
type_param    = NAME [ ":" ( "(" type { "," type } [ "," ] ")"     (* constraints: M8.1; fewer than two types is TP0220 *)
                           | type ) ] ;                            (* a bound: M17, §3.11; anything but a protocol is TP0220 *)

class_def     = { decorator } "class" NAME [ type_params ] [ "(" base { "," base } ")" ] ":"   (* M15, §4.3 *)
                NEWLINE INDENT class_member { class_member } DEDENT ;
base          = NAME [ "." NAME ] [ "[" type { "," type } "]" ] ;   (* more than one class base is TP0225; protocols may be added: M17 *)
class_member  = NAME ":" type [ "=" expr ] NEWLINE | method_def | "pass" NEWLINE ;
method_def    = { decorator } "def" NAME "(" "self" [ "," params ] ")" [ "->" type ] ":" ( block | "..." NEWLINE ) ;   (* M5; "...": M17 *)

enum_def      = "enum" NAME [ type_params ] ":" NEWLINE INDENT variant { variant } DEDENT ;
variant       = NAME [ "(" named_fields ")" ] NEWLINE ;
named_fields  = NAME ":" type { "," NAME ":" type } [ "," ] ;

block         = NEWLINE INDENT stmt { stmt } DEDENT ;

stmt          = simple_stmt NEWLINE | compound_stmt ;
simple_stmt   = let_stmt | var_stmt | assign_stmt | return_stmt | raise_stmt
              | "pass" | "break" | "continue" | expr ;
let_stmt      = "let" ( NAME [ ":" type ] | names ) "=" expr_list ;
var_stmt      = "var" ( NAME [ ":" type ] | names ) "=" expr_list ;
names         = NAME "," NAME { "," NAME } ;                         (* unpacking: M12, §3.8 *)
assign_stmt   = target ( "=" | "+=" | "-=" | "*=" | "/=" ) expr
              | target "," target { "," target } "=" expr_list ;     (* M12 *)
target        = NAME | postfix "." NAME | postfix "[" expr "]" ;
return_stmt   = "return" [ expr_list ] ;
expr_list     = expr { "," expr } ;                                  (* two or more make a tuple: M12 *)
raise_stmt    = "raise" [ expr ] ;                                   (* M16, §3.10 *)

compound_stmt = if_stmt | while_stmt | for_stmt | match_stmt | try_stmt | func_def ;   (* in a block: nested function, M11 *)
try_stmt      = "try" ":" block { except_clause } [ "else" ":" block ] [ "finally" ":" block ] ;   (* M16, §3.10 *)
except_clause = "except" [ except_types [ "as" NAME ] ] ":" block ;
except_types  = qual_name | "(" qual_name { "," qual_name } [ "," ] ")" ;
if_stmt       = "if" condition ":" block { "elif" condition ":" block } [ "else" ":" block ] ;
condition     = expr "is" "some" NAME | expr ;
while_stmt    = "while" condition ":" block ;
for_stmt      = "for" ( NAME | names ) "in" expr ":" block ;
match_stmt    = "match" expr ":" NEWLINE INDENT arm { arm } DEDENT ;
arm           = pattern ":" ( simple_stmt NEWLINE | block ) ;
pattern       = "_" | [ "-" ] INT | STRING | "True" | "False"
              | qual_name [ "(" [ pat_args ] ")" ] ;
pat_args      = pat_arg { "," pat_arg } [ "," ] ;
pat_arg       = NAME | NAME "=" NAME ;                               (* keyword form: class patterns, M15, §5.1 *)
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
arg           = expr | NAME "=" expr ;
atom          = INT | FLOAT | STRING | FSTRING | "True" | "False" | "None"
              | NAME | "self" | "(" expr ")" | "..."                 (* "...": M17, a protocol method's body *)
              | "(" expr "," [ expr { "," expr } [ "," ] ] ")"         (* a tuple: M12; one element is TP0209 *)
              | "[" [ expr { "," expr } [ "," ] ] "]"
              | "{" [ expr ":" expr { "," expr ":" expr } [ "," ] ] "}" ;
```

**Grammar notes**
- `a < b < c` is `TP0212`. Write `a < b and b < c`.
- An `arm` may take a simple statement on the same line (`Ok: print("ok")`) or an indented block.
- Keyword arguments are allowed in calls to functions and methods (M14, §4.2), class and enum-variant constructors (§4.3), `py` values (§10.2), and the keyword parameters of built-ins (`key` and `reverse` of `sorted` and `list.sort`, `start` of `enumerate`; §6). Keyword arguments may follow positional ones, as in Python. A positional argument after a keyword argument is `TP0100`.
- In a parameter list, a parameter without a default after one with a default is `TP0100`, as in Python.
- **Decorators (M15)** are only `@dataclass` on a `class` and `@override` on a method (§4.3). A decorator takes no arguments: `@dataclass(frozen=True)` is `TP0100`. Any other decorator name, a decorator in any other place, or a repeated one is `TP0224`.
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
  - Specific messages: `cannot assign to this expression` (an `assign_stmt` target that isn't a `target`), `positional argument follows keyword argument`, `parameter without a default follows parameter with a default` (M14), `a bare 'except' must be the last except clause` (M16, at the bare `except`, after which parsing continues normally), and `a type alias must be at the top level` (M18, at `type`). A `try` with no `except` or `finally`, or an `else` with no `except`, is `expected 'except' or 'finally', found …`.
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
| `dict[K, V]` | hash map that keeps insertion order; `K` ∈ {`int`, `str`, `bool`}, a tuple of valid keys (M12), or a plain class, compared by identity (M15), otherwise `TP0200` | `OrderedDictionary<K, V>` |
| `tuple[A, B, …]` | fixed-size, immutable group of values (M12, §3.8) | `ValueTuple<A, B, …>` |
| `T?` | optional `T` | `T?` (C# nullable) |
| `py` | dynamic Python object (M10, §10) | `Python.Runtime.PyObject` |
| `C` | instance of a class `C` (M15, §4.3) | a C# class (§8) |
| `Exception`, `ValueError`, … | built-in exception classes (M16, §3.10) | classes in `Taipan.Runtime` (§8) |
| `Callable[[A, B], R]` | function value (M11, §3.7) | `Taipan.Runtime.Function<D>` (§8) |
| `Comparable`, user protocols | structural interface, only as a type parameter bound (M17, §3.11) | none: bounded generics are monomorphized |

- A function without `-> type` returns nothing (C# `void`). Using its result as a value is `TP0200`.
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
  - The value must be a tuple (`TP0200` otherwise), with exactly as many elements as there are names (`TP0209`). Lists can't be unpacked, since their length isn't known when compiling. This differs from Python.
  - `_` ignores an element and may repeat. Unpacking declarations take no annotation: each binding gets its element's type.
  - In an assignment, the right side is evaluated first. Then, left to right, each target's own sub-expressions are evaluated and the value is stored, so `a, b = b, a` swaps. Every target follows the rules for `=` (`TP0202`). A compound form such as `a, b += 1` is `TP0100`.
- **Equality and ordering:**
  - `==` and `!=` compare tuples element by element, structurally, as for lists. Two tuple types are comparable when they have the same length and each pair of element types is comparable.
  - `<`, `<=`, `>` and `>=` compare lexicographically, as in Python, when every element type is ordered: `int`, `float`, `str`, or a tuple of those. So `sorted(xs, key=lambda s: (s.healthy, s.name))` works.
- **Dict keys:** a tuple of valid keys is itself a valid key: `dict[tuple[int, int], str]`.
- **`match`:** a tuple can't be matched on (`TP0200`). Tuple patterns are post-M12.
- **Display:** `(1, "a")`, with elements written as inside a list.
- **Generics:** tuple types may be type arguments, and inference matches them element by element.

### 3.9 Narrowing a name (M15)
- Some conditions narrow a name's type in the code they guard, as mypy does:
  - `isinstance(x, C)` narrows `x` to the class `C` (§4.3). On an `x` of type `A?`, it also rules out `None`, since `isinstance(None, C)` is `False`.
  - `x is not None` narrows `x: T?` to `T`.
- **Which names:** only a name that can't change: a `let`, a parameter (including `self`), a loop variable, or a `match` or `is some` binding. A `var` isn't narrowed, nor is a field (`self.x is not None`) or any other expression, since it could change in between. Using one as if it were narrowed reports the usual error (`TP0203`, `TP0210`), with help `copy it to a 'let' first`.
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
  - In `while c:`, the body sees what holds when `c` is true.
  - In `a and b`, `b` sees what holds when `a` is true. In `a or b`, `b` sees what holds when `a` is false. So `x is None or x > 0` is valid.
  - Lambdas and nested functions written in a narrowed region see the narrowed type too, since the name can't change.
  - `match` class patterns narrow the scrutinee inside their arm (§5.1).
- A narrowed name has the narrowed type for every use: member access, calls, arguments and assignments. It's still the same binding, so it follows the shadowing rules as before.
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
  | `RuntimeError` | `Exception` | changing a list or dict while iterating over it |
  | `PythonError` | `Exception` | a Python exception from a `py` operation (§10.5) |

- `Exception` has one field, `message: str`, and an `__init__(self, message: str = "")`. Every built-in class except `PythonError` takes the same arguments: `raise ValueError("bad port")`. Python has no `message` field; it keeps the arguments in `args`, and `str(e)` gives the message. In Taipan, `str(e)` gives the message too (§6).
- `PythonError` has two more fields: `type: str`, the Python exception's class name, and `value: py`, the Python exception object. Its `message` is Python's `str(e)`. Only the runtime creates one: constructing it is `TP0200`, and deriving from it is `TP0225`.
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
- Importing Python modules happens at startup (§10.1), before any `try` runs, so a failure to load Python or to import a module is always a panic.
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
- **`__lt__` on a class** is the one special method besides `__init__` that a class may define (§4.3). It takes exactly one parameter, whose type is the class or one of its bases, and returns `bool` (`TP0225` otherwise). It overrides like any method (§4.3).
- `a < b` on a class value `a` calls `a.__lt__(b)`, and `b` must fit its parameter. `a > b` evaluates `a` and then `b`, and calls `b.__lt__(a)`, as Python does when `a` has no `__gt__`.
- `<=` and `>=` aren't defined by `__lt__`: on a class or a bounded type parameter they're `TP0200`, with help `'__lt__' defines only '<' and '>'`. Python raises `TypeError` for them at run time; `__le__`, `__ge__` and `functools.total_ordering` are deferred.
- `sorted` and `list.sort` accept a list, or a key, of any `Comparable` type (§6), and call only `__lt__`, as Python's sort does.
- Calling `__lt__` directly (`a.__lt__(b)`) is `TP0200`, with help `use '<'`, except as `super().__lt__(…)` in an override.

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
- **Fields** are declared in the class body, with a type and an optional default: `legs: int = 4`. Assigning a field that isn't declared, through `self` or otherwise, is `TP0210`. Python would create it.
  - A field's default is evaluated each time an instance is made: in a plain class, just before the class's own `__init__` body runs, and in a dataclass, as the constructor's parameter default. This differs from Python, where a class-level `items: list[int] = []` is one list shared by every instance.
  - Defaults follow the rules for parameter defaults (§4.2): checked against the field's type, and unable to use `self` or the `__init__` parameters (`TP0201`).
  - Fields are mutable through any binding: `s.healthy = 2` is valid even when `s` is a `let`. `let` fixes the binding, not the object.
- **Methods** take an explicit `self` as the first parameter, typed implicitly as the enclosing class. They're called as `s.method(args)`, with arguments matched as for functions (§4.2), and are dispatched on the object's runtime class, so a `Dog` in a `list[Animal]` runs `Dog.speak`. Inside a method, other methods are reached as `self.m()`, and a bare `m()` refers to a global function.
- **Special methods:** a plain class may define `__init__`, and any class may define `__lt__` (M17, §3.11). Any other method named `__x__` (`__str__`, `__eq__`, `__hash__`, …) is `TP0225`. They're deferred, and Taipan reports them rather than silently ignore them.
- **Members** are looked up on the static type. An unknown field or method is `TP0210`. A `Dog`-only method on an `Animal` needs narrowing first (§3.9).

**Construction**
- A class's name, called, constructs an instance: `Dog("rex", "lab")`, `Point(1.0, y=2.0)`. Arguments are matched as for functions (§4.2).
- **Plain class:** the arguments are matched to `__init__`'s parameters, after `self`.
  - A plain class without `__init__` uses its base class's `__init__`, as in Python. With no base class, it takes no arguments.
  - `__init__` has no return type (`TP0200`), and may use a bare `return`.
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
  - a return type that's the same as the base's, or, when both are classes or optional classes, one assignable to the base's (`Dog` for `Animal`). If the base returns nothing, the override must too.

**Narrowing and conversion**
- `isinstance(x, C)` tests the runtime class (§6), and narrows `x` (§3.9).
- `x as C` converts `x` to the class `C`, which must derive from `x`'s class or be a base of it (`TP0200` otherwise). If `x` doesn't hold a `C`, it panics with `cannot convert <runtime class> to <C>` (§7). `x as C?` gives `None` instead of panicking.
- `match` takes class patterns (§5.1).

**Equality, display and keys**
- **Plain class:** `==` and `!=` compare identity, as in Python without `__eq__`. Each construction makes a new object, equal only to itself. The display form is `<Dog object>`, by runtime class.
- **Dataclass:** `==` compares the runtime classes, then every field, inherited ones included, structurally. Values of different classes are never equal, as in Python's `dataclass`. The display form is the runtime class's name and every field: `Dog(name="rex", breed="lab")`.
- `==` between two class types is valid when one is assignable to the other. Unrelated classes are `TP0200`.
- `dict[C, V]` is valid for a plain class `C`, with keys compared by identity (§3.1).
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
- **Class names** live in the type namespace and also in the global value scope, as the class's constructor. So a class can't share a name with a function, constant or built-in (`TP0214`), and a local can't take its name (`TP0207`). A class named `int`, `float`, `bool`, `str`, `list`, `dict`, `py`, `Callable` or `tuple` is also `TP0214`, as is one named `Protocol`, `Self` or `Comparable` (M17).
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
  - A slice isn't a target (`TP0100`). Slice assignment is post-M12.
  - Compound forms (`xs[f()] += 1`, `make().count += 1`) evaluate the target's sub-expressions once.
- **Indexing** applies to `list[T]` (`int` index, gives `T`), `dict[K, V]` (`K` key, gives `V`), `str` (`int` index, gives `str`), and tuples (§3.8). Anything else is `TP0200`.
- **Built-in methods** follow §6, with argument counts checked by `TP0209`. `d.get` arrives with optionals in M6.

### 4.7 Modules (M9)
- **A program is a main file plus the modules it imports, directly or indirectly.**
  - `import geometry` loads `geometry.tpn` from the main file's directory. Every module of a program lives in that one directory, so there are no packages or dotted module names in v0.1.
  - A missing file is `TP0221`, reported at the import.
  - The module name `python` is reserved for Python interop (§10).
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

---

## 5. Statements and expressions

### 5.1 `match`
- The scrutinee can be an enum, `int`, `str`, `bool`, or a class (M15).
- **Patterns:**
  - Variant patterns bind payload fields positionally: `Degraded(h, t)`. With a payload, the binding count must equal the field count. A variant with a payload may also be matched as `Degraded` without parentheses, binding nothing.
  - Literal patterns apply to `int`, `str`, and `bool`.
  - `_` matches anything.
- **Class patterns (M15)** match when the scrutinee's runtime class is the named class or derives from it, as with `isinstance`:
  - `Dog()` binds nothing. Unlike a variant, a class pattern always has parentheses, as in Python: a bare `Dog` is `TP0200`.
  - `Dog(name=n, breed=b)` binds fields by name, in any order. Any field of `Dog`, inherited ones included, may be bound.
  - For a dataclass, `Point(x, y)` binds fields by position, in constructor order, as Python's `__match_args__` does. Positional bindings may be followed by keyword ones. Positional bindings on a plain class are `TP0209`, and more than there are fields is `TP0209`.
  - A field named twice is `TP0209`, and an unknown one is `TP0210`.
  - The class must derive from the scrutinee's class, or be it (`TP0200` otherwise). In its arm, a scrutinee that's a name that can't change (§3.9) is narrowed to that class.
  - Python's sub-patterns inside a class pattern (`Dog(name="rex")`) aren't supported: each argument is a binding.
- **Exhaustiveness:**
  - For an enum, every variant must be covered, or there must be a `_` arm. Otherwise it's `TP0204`, and the message lists the missing variants.
  - For `bool`, the arms must cover `True` and `False`, or include `_`.
  - For `int` and `str`, a `_` arm is required.
  - For a class, there must be a `_` arm, or a pattern of the scrutinee's own class. Subclasses are open-ended, so listing them never covers the base.
- An arm after `_`, or a duplicate arm, is warning `TP0300` (unreachable).
- `match` is a statement in v0.1. A match expression is post-v0.1.
- **Checking details:**
  - Any other scrutinee type is `TP0200`, and an optional scrutinee is `TP0203`.
  - The scrutinee is evaluated once.
  - A literal pattern of the wrong type, or a variant pattern on a non-enum scrutinee, is `TP0200`.
  - Variant patterns are resolved against the scrutinee's enum, so an unqualified variant pattern is never ambiguous. A variant the enum doesn't have is `TP0210`, and a qualified pattern naming another enum is `TP0200`.
  - A binding count that differs from the payload's field count is `TP0209`. A variant without a payload may be written with or without empty parentheses.
  - **`_` as a binding** (`Degraded(_, t)`) ignores that field and may repeat.
  - Other bindings are `let`s scoped to their arm. They follow the shadowing rules (`TP0207`), and a name repeated within one pattern is `TP0214`.
  - **Duplicates (`TP0300`):** a duplicate arm is the same variant, literal or `True`/`False` as an earlier arm, regardless of its bindings. A class pattern is unreachable after a pattern of the same class or one of its bases, since bindings never fail to match. Unreachable arms are still checked, but not emitted.
  - `TP0204` points at the `match` keyword. It lists missing variants in declaration order (`missing: Down, Ok`), `True`/`False` for bool, and `_` for int and str.
- **Return paths:** a `match` terminates (§4.5) if every arm's body terminates. Exhaustiveness is guaranteed once it type-checks.
- **Control flow:** `break` and `continue` in an arm apply to the enclosing loop, as anywhere else.

### 5.2 Loops
- `for x in e` works when `e` is:
  - `list[T]`, giving `x: T`
  - `dict[K, V]`, giving `x: K` (keys, insertion order)
  - `str`, giving `x: str` (one character each)
  - `range(...)`, giving `x: int`
- The loop variable is a `let` scoped to the body.
- `for a, b in e` unpacks each element, which must be a tuple with that many elements (§3.8). Each name is a loop variable.
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
| `== !=` | same type, int/float, or related classes | bool | structural for dataclasses, enums, lists, dicts, tuples; identity for plain classes (§4.3) and functions (§3.7) |
| `is None`, `is not None` | T? | bool | M15; narrows (§3.9) |
| `< <= > >=` | numeric, or str (ordinal), or tuples of those (M12) | bool | tuples compare lexicographically |
| `< >` | a class with `__lt__`, or a type parameter bounded by a protocol with `__lt__` (M17) | bool | `a < b` is `a.__lt__(b)`, `a > b` is `b.__lt__(a)` (§3.11) |
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

---

## 6. Built-ins (v0.1)

**Functions**
- `print(*args)` writes each argument's display form, space-separated, then a newline.
- `len(x) -> int` takes a list, dict, or str. For a `str` it counts Unicode code points, so `len("🐍")` is `1`.
- `range(stop)` and `range(start, stop)` iterate only (they're not a list).
- `str(x) -> str` gives the display form.
- `int(x: float) -> int` truncates toward zero. An `int` argument is returned unchanged. `int(x: str) -> int?` parses, returning `None` on failure. It accepts an optional sign and decimal digits with `_` only between digits (as in §1.3), with surrounding whitespace allowed. A value outside the int range gives `None`.
- `float(x: int) -> float`. A `float` argument is returned unchanged. `float(x: str) -> float?` accepts the same forms as `int(str)`, plus an optional fraction and exponent (`1.5`, `2e-3`, `.5`, `5.`), and `inf`, `infinity` and `nan` in any case. Otherwise it returns `None`.
- `range` can only be used as the iterable of a `for` loop. Any other use is `TP0200`.
- `sorted(xs: list[T], key=…, reverse=False) -> list[T]` (M11) returns a new list in ascending order.
  - `key: Callable[[T], K]` gives each element's sort key. It's called once per element, in list order, before any reordering.
  - The elements, or the keys when `key` is given, must be `int`, `float` or `str`, tuples of those (M12), or another `Comparable` type: a class with `__lt__`, or a type parameter bounded by a protocol with `__lt__` (M17, §3.11). They're ordered as by `<`. Otherwise it's `TP0200`. A NaN sorts before every other float.
  - `reverse: bool` sorts in descending order.
  - The sort is stable: equal elements keep their original order, with `reverse=True` too, as in Python.
  - `key` and `reverse` are optional and keyword-only.
- `map(f: Callable[[T], U], xs: list[T]) -> list[U]` and `filter(f: Callable[[T], bool], xs: list[T]) -> list[T]` (M11) call `f` once per element, in order, and return new lists. Python's versions return iterators, which Taipan doesn't have.
- `enumerate(xs)` and `enumerate(xs, start)` (M12) give a new list of `(index, element)` tuples: `list[tuple[int, T]]` for a `list[T]`, or `list[tuple[int, str]]` for a `str`'s characters. `start` is an `int` that defaults to 0. It may be given by position, or as the keyword argument `start=`, as in Python.
- `isinstance(x, C) -> bool` (M15) is `True` when `x`'s runtime class is `C` or derives from it, as in Python, and `False` for `None`.
  - `x` must be a class or an optional class, and `C` the name of a class (qualified for another module: `zoo.Dog`) that isn't generic. Anything else is `TP0200`. A tuple of classes (`isinstance(x, (A, B))`) is deferred.
  - `C` must derive from `x`'s class, or be it or one of its bases. An unrelated class is `TP0200`, since the test could never be `True`.
  - It narrows `x` (§3.9).
- `super()` (M15) is valid only as `super().m(…)` in a method (§4.3).
- `zip(xs: list[A], ys: list[B]) -> list[tuple[A, B]]` (M12) pairs the elements up, stopping at the shorter list, as in Python. It takes exactly two lists.
- `sorted`, `sort`, `map`, `filter`, `enumerate` and `zip` iterate their lists (§5.2), so a function argument that changes a list panics with `list mutated during iteration`.

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
| enum | `Ok`, `Degraded(healthy=1, total=4)` |
| function | `<function name>`, `<function <lambda>>` (M11) |
| exception | its message, as Python's `str(e)` (M16). Inside a container, its class and message: `[KeyError("missing key \"a\"")]` |

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

- **Warnings** (`TP0300` and `TP0301`) print as `<file>:<line>:<col>: warning TP<code>: <message>`. They don't stop compilation or change the exit code. They're printed with the type checker's errors, before the program runs.

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
| TP0100 | syntax error: expected X, found Y |
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
| TP0222 | top-level statement in an imported module |
| TP0223 | nested function with type parameters |
| TP0224 | invalid decorator |
| TP0225 | invalid class definition |
| TP0226 | object used before it is fully initialized |
| TP0227 | missing or unneeded `@override` |
| TP0228 | incompatible override |
| TP0229 | protocol used as a type |
| TP0230 | recursive type alias |
| TP0300 | warning: unreachable code (a statement, `match` arm or `except` clause) |
| TP0301 | warning: broad `except` clause discards the exception |

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
| TP0200 | `expected <T>, found lambda` / `cannot sort <T>`, with help `pass a key, e.g. 'key=lambda x: ...'` / `cannot sort by <K>` (M11) |
| TP0200 | `tuple index must be an integer literal` / `index <i> out of range for <T>` (a tuple) / `cannot unpack <T>` / `cannot slice <T>` (M12) |
| TP0200 | `integer literal is out of range` |
| TP0200 | `'range' can only be used in a for loop` / `cannot iterate over <T>` |
| TP0200 | `'<T>' is inferred as both <A> and <B>` / `'<T>' must be <A> or <B>, found <C>` (type-argument inference and constraints; `A, B or C` for longer tuples) |
| TP0200 | `'<m>' is a module, not a value` |
| TP0200 | `'as' converts py values; found <T>` / `cannot convert to <T>` / `cannot pass <T> to Python` (§10) |
| TP0200 | `cannot match on <T>` / `'<E>' is a type, not a value` / `'<v>' is not a function` (a variant without a payload) |
| TP0200 | `'is some' needs an optional, found <T>` / `'is None' needs an optional, found <T>` / `'is not None' needs an optional, found <T>` / `'??' needs an optional on the left, found <T>` |
| TP0200 | `'<m>' is a method; call it` / `'<f>' is a field, not a method` / `cannot index <T>` / `dict keys must be int, str, bool, tuples of those, or plain class instances, found <T>` (for a dataclass, with help `a dataclass compares by value, so it cannot be a key`) |
| TP0200 | `isinstance needs a class, found <T>` / `'<C>' is not a class` / `'<C>' is generic` / `<T> can never be a <C>` (`isinstance`, `as` and class patterns on unrelated classes) / `'<C>' is a class; write '<C>()'` (a class pattern without parentheses) (M15) |
| TP0200 | `'<A>' is a type alias, not a value`, with help `use '<N>'` when it names a class or enum (M18) |
| TP0200 | `cannot raise <T>` (with help `write '<C>(…)'` for a class name) / `'<C>' is not an exception class` / `'PythonError' cannot be constructed` (M16) |
| TP0200 | `'<T>' does not satisfy '<P>': it has no method '<m>'` / `'<T>' does not satisfy '<P>': '<m>' must be 'def <m>(self, …) -> <R>'` (the protocol's signature with `Self` replaced; `-> <R>` left out when it returns nothing), at a call's callee or a declared protocol, with ` (declared by '<B>')` after `'<P>'` for an inherited declaration / `'...' can only be the body of a protocol method` / `'<m>' cannot be called directly`, with help `use '<'` (M17) |
| TP0200 | `'super()' can only be used as 'super().<method>(…)'` / `'super()' can only be used in a method` / `'<C>' has no base class` / `'__init__' cannot be called directly` / `'__init__' cannot have a return type` (M15) |
| TP0201 | `undefined name '<x>'` (with help `use 'None'` for `none`) / `undefined type '<T>'` / `module '<m>' has no definition '<x>'` |
| TP0202 | `cannot assign to '<x>': it is a <let binding\|parameter\|loop variable\|constant\|function\|method>`, with help `declare it with 'var'` for a `let` binding / `cannot assign to an element of str` / `cannot assign to an element of a tuple` |
| TP0203 | `expected <T>, found <T>?` / `<T>? must be narrowed before use`, with help `narrow it with 'is some', or give a default with '??'` |
| TP0204 | `non-exhaustive match (missing: <a>, <b>)` |
| TP0205 | `condition must be bool, found <T>` (with help `convert it with 'as bool'` for `py`) |
| TP0206 | `cannot infer the type of an empty list` / `... empty dict` / `cannot infer the type of None` / `cannot infer '<T>' for '<f>'` / `cannot infer the type of lambda parameter '<x>'`, with help `add a type annotation` |
| TP0207 | `'<x>' shadows an outer binding` |
| TP0208 | `not all paths in '<f>' return a value` |
| TP0209 | `'<S>' takes <n> type arguments, found <m>` (`type argument` when n is 1) |
| TP0209 | `'<v>' has <n> fields, found <m> bindings` (variant and dataclass patterns; `field` / `binding` when the count is 1) / `'<C>' is not a dataclass, so its fields can only be bound by name` / `field '<f>' is given more than once` (class patterns, M15) |
| TP0209 | `<T> has <n> elements, found <m> names` (unpacking) / `a tuple needs at least two elements` (M12) |
| TP0209 | `'<f>' takes <n> arguments, found <m>` (`argument` when n is 1; `1 or 2 arguments` for `range`; `<a> to <b> arguments` for a function with defaults, M14; `function takes …` when the callee isn't a name) / `missing argument '<x>'` (M14) / `missing field '<f>' in '<S>'` / `field '<f>' is given more than once` / `argument '<k>' is given more than once` |
| TP0209 | `expected a lambda with <n> parameters, found <m>` (`parameter` when n is 1) / `'Callable' takes a parameter list and a result type` |
| TP0210 | `<T> has no field '<x>'` / `<T> has no method '<x>'` / `<E> has no variant '<x>'` |
| TP0211 | `'<f>' cannot use top-level variable '<x>'`, with help `pass it as a parameter, or make it a const` |
| TP0213 | `optional types cannot be nested` |
| TP0214 | `duplicate definition of '<x>'` |
| TP0215 | `'break' outside a loop` / `'continue' outside a loop` / `'return' outside a function` / `'raise' without an exception outside an except block` / `'return' cannot be used in a finally block` (and `'break'`, `'continue'`) (M16) |
| TP0216 | `'<v>' is ambiguous: it is a variant of <E1> and <E2>`, with help `qualify it, e.g. '<E1>.<v>'` |
| TP0217 | `const initializer must be a constant expression` |
| TP0218 | `'<f>' takes no keyword arguments` (a built-in) / `a function value takes no keyword arguments` / `'<f>' has no parameter '<k>'` (M14; also for `sorted`, `sort` and `enumerate`) |
| TP0219 | `instantiating '<f>' needs '<f>[<T>]', which is too deep`, at the call that starts the chain |
| TP0221 | `module '<m>' not found`, with help `looked for <path>` / `'python' is not a module`, with help `use 'from python import <module>'` |
| TP0222 | `only the main file can have top-level statements` |
| TP0223 | `nested functions cannot have type parameters` |
| TP0224 | `unknown decorator '@<d>'` / `'@<d>' cannot be used here` (with help `'@dataclass' goes on a class, '@override' on a method`) / `duplicate decorator '@<d>'` |
| TP0225 | `a class can have only one base class` (protocols aside, M17) / `'<T>' cannot be a base class` / `a dataclass can only derive from a dataclass` (with help `exceptions are plain classes; give it an '__init__'` for an exception base, M16) / `a plain class can only derive from a plain class` / `generic classes cannot be derived from` / `generic classes cannot have a base class` / `'<C>' derives from itself` / `special method '<m>' is not supported` / `a dataclass cannot define '__init__'` / `field without a default follows field with a default` / `'__lt__' must take one parameter of type '<C>' (or a base class) and return bool` / `'<P>' is already declared` (or `already declared by '<B>'`) / `a protocol can only declare methods` / `a protocol method's body must be '...'` / `protocol method parameters cannot have defaults` / `protocols cannot be generic` / `a protocol can only derive from 'Protocol'` (M17) |
| TP0226 | `'__init__' does not set '<f>'` / `'<f>' has no default, and '<C>' has no '__init__'` / `'<f>' is read before it is set` / `'self' is used before '<f>' is set` (or `before 'super().__init__' is called`) / `'<f>' is inherited, so it is set by 'super().__init__'` / `'super().__init__' is called before '<f>' is set` / `'<C>.__init__' must call 'super().__init__(…)'` / `'super().__init__' must be called once, directly in '__init__'` / `'super().__init__' can only be called in '__init__'` / `'__init__' returns before '<f>' is set` (or `before 'super().__init__' is called`) (when several fields are unset, the first in declaration order is named) |
| TP0227 | `'<m>' overrides '<B>.<m>'; mark it '@override'` / `'<m>' does not override a method of <a base class\|'<B>'>` / `'__init__' cannot be marked '@override'` |
| TP0228 | `'<m>' must take the same parameters as '<B>.<m>'` / `parameter '<p>' of '<m>' must have type <T>, as in '<B>.<m>'` / `parameter '<p>' of '<m>' needs a default, as in '<B>.<m>'` / `'<m>' must return <T>, as '<B>.<m>' does` (or `return nothing`) |
| TP0220 | `a constraint needs at least two types` / `'<T>' is listed twice in the constraint` / `a constraint cannot be optional or generic` / `constraints are only supported on functions` / `'<T>' is not a protocol` (with help `class bounds are not supported`, for a class) / `bounds are only supported on functions` (M17) |
| TP0229 | `'<P>' is a protocol, so it can only be a type parameter bound`, with help `use a bounded type parameter, e.g. 'def f[T: <P>](x: T)'` (M17) |
| TP0230 | `type alias '<A>' refers to itself` / `type alias '<A>' refers to itself through '<B>'` (`'<B>' and '<C>'` for longer cycles), with help `use a class for a recursive type` (M18) |
| TP0300 | `unreachable match arm` / `unreachable except clause`, with help `'<B>' is already caught above` / `unreachable code` (M16) |
| TP0301 | `this clause catches every exception and discards it`, with help `catch a specific class, re-raise with 'raise', or bind it with 'as e' and use it` (M16) |

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
- If Python can't be loaded, the first Python import panics with `cannot load Python: <reason>`.
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
- special methods other than `__init__` and `__lt__`, such as `__str__`, `__eq__`, `__hash__`, `__le__` and `__add__`, and `functools.total_ordering`
- multiple inheritance, mixing dataclasses and plain classes in one hierarchy, and inheritance involving generic classes
- `@dataclass` options (`frozen`, `order`), `__post_init__`, and an `__init__` written in a dataclass
- class attributes, `@staticmethod`, `@classmethod`, `@property`, abstract classes, `@final`, and `Self` outside protocols
- `isinstance` with a tuple of classes or on `py` values, and `is` between two expressions (identity)
- sub-patterns inside class patterns
- `-> None` as an explicit "returns nothing" annotation
- generic functions, built-ins, methods and constructors as values (wrap them in a lambda)
- iterators and generators (`map` and `filter` return lists)
- tuple patterns in `match`
- nested unpacking (`let (a, b), c = …`) and unpacking lists
- slice assignment and `del`
- `zip` of more than two lists
- `match` as an expression
- type aliases inside functions and classes, recursive aliases, and showing an alias's name in diagnostics
- keyword-only (`*`) and positional-only (`/`) parameters, `*args` and `**kwargs`, and defaults on lambda parameters
- flow-sensitive narrowing after early return, and narrowing `var`s and fields
- `raise … from …`, exception chaining, `BaseException`, exception groups (`except*`), and printing tracebacks
- dataclass exceptions (mixing dataclasses and plain classes), and `args` on exceptions
- format specs in f-strings
- an LSP server
- NativeAOT publish
- emitting CPython extension modules (interop level 3)
