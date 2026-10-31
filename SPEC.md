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
  `def struct enum let var const if elif else while for in match return pass break continue and or not is some none True False as from import lambda`
- **Integer literals:** decimal, with `_` separators allowed (`1_000`). They're 64-bit signed, and overflow at compile time is `TP0200`. The range check belongs to the type checker, not the lexer, so `-9223372036854775808` is valid.
- **Float literals:** `1.5`, `1e9`, `2.5e-3`. A leading digit is required, so `.5` is invalid. Formally: `digits "." digits [exp] | digits exp`, where `exp = ("e" | "E") ["+" | "-"] digits`. `1.` lexes as the int `1` followed by `.`.
- **Number literal rules:** `_` may appear only between two digits. A multi-digit integer part can't start with `0`. A number immediately followed by a letter, digit-less exponent, or `_` (`12abc`, `1e`, `1_`, `007`) is `TP0007`.
- **String literals:** `"..."` or `'...'`. Escapes are `\n \t \\ \" \' \{ \}`. Any other backslash sequence is `TP0005`. Strings are single-line only, and an unterminated string is `TP0003`.
- **F-strings:** `f"...{expr}..."` or `f'...'`, with a lowercase `f` immediately before the quote. A hole holds any expression except an f-string. `{{` and `}}` are literal braces, and literal text uses the string escapes. The lexer emits the f-string as a single token holding the raw text plus hole spans, and the parser re-lexes the holes.
  - A hole runs from `{` to its matching `}`. Inside a hole the lexer tracks nested brackets and skips over string literals of either quote, so `f"{d["k"]}"` is valid.
  - `TP0006`: a lone `}` in literal text, an empty or whitespace-only hole, or an f-string inside a hole.
  - An f-string, or a hole, still open at end of line is `TP0003`.
- **Operators and punctuation:**
  `+ - * / // % == != < <= > >= = += -= *= /= -> ?? ? . , : ( ) [ ] { }`
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
top_item      = import_stmt | func_def | struct_def | enum_def | const_def | stmt ;

import_stmt   = "import" NAME [ "as" NAME ] NEWLINE                                  (* M9, §4.7 *)
              | "from" NAME "import" import_name { "," import_name } NEWLINE ;
import_name   = NAME [ "as" NAME ] ;

const_def     = "const" NAME [ ":" type ] "=" expr NEWLINE ;

func_def      = "def" NAME [ type_params ] "(" [ params ] ")" [ "->" type ] ":" block ;
params        = param { "," param } [ "," ] ;
param         = NAME ":" type ;
type_params   = "[" type_param { "," type_param } "]" ;             (* M8, §3.6 *)
type_param    = NAME [ ":" "(" type { "," type } [ "," ] ")" ] ;   (* constraints: M8.1; fewer than two types is TP0220 *)

struct_def    = "struct" NAME [ type_params ] ":" NEWLINE INDENT struct_member { struct_member } DEDENT ;
struct_member = NAME ":" type NEWLINE | method_def ;
method_def    = "def" NAME "(" "self" [ "," params ] ")" [ "->" type ] ":" block ;   (* M5 *)

enum_def      = "enum" NAME [ type_params ] ":" NEWLINE INDENT variant { variant } DEDENT ;
variant       = NAME [ "(" named_fields ")" ] NEWLINE ;
named_fields  = NAME ":" type { "," NAME ":" type } [ "," ] ;

block         = NEWLINE INDENT stmt { stmt } DEDENT ;

stmt          = simple_stmt NEWLINE | compound_stmt ;
simple_stmt   = let_stmt | var_stmt | assign_stmt | return_stmt
              | "pass" | "break" | "continue" | expr ;
let_stmt      = "let" ( NAME [ ":" type ] | names ) "=" expr_list ;
var_stmt      = "var" ( NAME [ ":" type ] | names ) "=" expr_list ;
names         = NAME "," NAME { "," NAME } ;                         (* unpacking: M12, §3.8 *)
assign_stmt   = target ( "=" | "+=" | "-=" | "*=" | "/=" ) expr
              | target "," target { "," target } "=" expr_list ;     (* M12 *)
target        = NAME | postfix "." NAME | postfix "[" expr "]" ;
return_stmt   = "return" [ expr_list ] ;
expr_list     = expr { "," expr } ;                                  (* two or more make a tuple: M12 *)

compound_stmt = if_stmt | while_stmt | for_stmt | match_stmt | func_def ;     (* in a block: nested function, M11 *)
if_stmt       = "if" condition ":" block { "elif" condition ":" block } [ "else" ":" block ] ;
condition     = expr "is" "some" NAME | expr "is" "none" | expr ;
while_stmt    = "while" condition ":" block ;
for_stmt      = "for" ( NAME | names ) "in" expr ":" block ;
match_stmt    = "match" expr ":" NEWLINE INDENT arm { arm } DEDENT ;
arm           = pattern ":" ( simple_stmt NEWLINE | block ) ;
pattern       = "_" | [ "-" ] INT | STRING | "True" | "False"
              | qual_name [ "(" [ NAME { "," NAME } ] ")" ] ;
qual_name     = NAME [ "." NAME [ "." NAME ] ] ;                    (* module.Enum.Variant: M9 *)

type          = base_type { "?" } ;                               (* more than one "?" is TP0213, §3.1 *)
base_type     = "int" | "float" | "bool" | "str" | "py"           (* py: M10 *)
              | "list" "[" type "]"
              | "dict" "[" type "," type "]"
              | "tuple" "[" type { "," type } "]"                    (* M12; fewer than two types is TP0209 *)
              | "Callable" "[" "[" [ type { "," type } ] "]" "," ( type | "none" ) "]"   (* M11, §3.7 *)
              | NAME [ "." NAME ] [ "[" type { "," type } "]" ] ;  (* module.Type: M9 *)

(* Expressions, lowest to highest precedence *)
expr          = lambda_expr | coalesce ;
lambda_expr   = "lambda" [ lambda_param { "," lambda_param } ] ":" expr ;   (* M11, §3.7 *)
lambda_param  = NAME | "_" ;
coalesce      = or_expr [ "??" coalesce ] ;                        (* right-assoc *)
or_expr       = and_expr { "or" and_expr } ;
and_expr      = not_expr { "and" not_expr } ;
not_expr      = "not" not_expr | comparison ;
comparison    = additive [ comp_op additive ] ;                   (* no chaining *)
comp_op       = "==" | "!=" | "<" | "<=" | ">" | ">=" | "in" | "not" "in" ;
additive      = term { ( "+" | "-" ) term } ;
term          = unary { ( "*" | "/" | "//" | "%" ) unary } ;
unary         = "-" unary | cast ;
cast          = postfix [ "as" type ] ;                            (* M10 *)
postfix       = atom { call | "." NAME | "[" subscript "]" } ;
subscript     = expr | [ expr ] ":" [ expr ] [ ":" [ expr ] ] ;      (* a slice: M12, §5.4 *)
call          = "(" [ arg { "," arg } [ "," ] ] ")" ;
arg           = expr | NAME "=" expr ;
atom          = INT | FLOAT | STRING | FSTRING | "True" | "False" | "none"
              | NAME | "self" | "(" expr ")"
              | "(" expr "," [ expr { "," expr } [ "," ] ] ")"         (* a tuple: M12; one element is TP0209 *)
              | "[" [ expr { "," expr } [ "," ] ] "]"
              | "{" [ expr ":" expr { "," expr ":" expr } [ "," ] ] "}" ;
```

**Grammar notes**
- `a < b < c` is `TP0212`. Write `a < b and b < c`.
- An `arm` may take a simple statement on the same line (`Ok: print("ok")`) or an indented block.
- Keyword arguments are allowed only in struct and enum-variant constructors (§4.3), in calls to `py` values (§10.2), and for the keyword-only parameters `key` and `reverse` of `sorted` and `list.sort` (M11, §6). Within one call, arguments are either all positional or all keyword. Mixing them is `TP0100`. The exceptions are `py` calls and `sorted`/`sort`, where keyword arguments may follow positional ones, as in Python. A positional argument after a keyword argument is always `TP0100`.
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
  - Specific messages: `cannot assign to this expression` (an `assign_stmt` target that isn't a `target`), and `cannot mix positional and keyword arguments`.
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
| `dict[K, V]` | hash map that keeps insertion order; `K` ∈ {`int`, `str`, `bool`}, or a tuple of valid keys (M12), otherwise `TP0200` | `OrderedDictionary<K, V>` |
| `tuple[A, B, …]` | fixed-size, immutable group of values (M12, §3.8) | `ValueTuple<A, B, …>` |
| `T?` | optional `T` | `T?` (C# nullable) |
| `py` | dynamic Python object (M10, §10) | `Python.Runtime.PyObject` |
| `Callable[[A, B], R]` | function value (M11, §3.7) | `Taipan.Runtime.Function<D>` (§8) |

- A function without `-> type` returns nothing (C# `void`). Using its result as a value is `TP0200`.
- `T??` is `TP0213`. Optionals don't nest.

### 3.2 Inference
- **Local inference only.** A `let` or `var` without an annotation takes the type of its initializer.
- **Function parameters must be annotated.** The return type is declared, or the function returns nothing.
- `[]` and `{}` without an expected type are `TP0206`. The expected type can come from an annotation, a parameter, a return type, or an assignment target.
- List literals need all elements to have the same type, except that `int` and `float` mix to `float`.

### 3.3 Assignability
`S` is assignable to `T` when one of these holds:
- `S == T`
- `S` is `int` and `T` is `float` (implicit widening)
- `T` is `U?` and `S` is assignable to `U`
- `S` is the type of `none` and `T` is any optional

There are no other implicit conversions. Explicit conversions use the built-ins `int(x)`, `float(x)`, and `str(x)` (§6).

### 3.4 Optionals and narrowing
- A value of type `T?` can't be used where `T` is expected (`TP0203`). Member access and indexing on `T?` are also `TP0203`.
- These forms narrow it:
  - `if x is some v:` binds `v: T` in that branch only.
  - `if x is none:` (no binding). Narrowing of `x` itself after an early return is a post-v0.1 feature.
  - `x ?? default` has type `T` when `default: T`.
- `x == none` is allowed and returns `bool`, but it doesn't narrow.
- **Using an optional:**
  - A `T?` is `TP0203` wherever a `T` is expected. That includes operands of operators other than `==`/`!=`, the target of member access, indexing or a method call, and the iterable of a `for`.
  - It's `TP0200` where some unrelated type is expected.
  - `==` and `!=` may compare a `T?` with a `T`, a `T?` or `none`. The result is `True` only when both are `none`, or both hold equal values.
- **`is some` / `is none`:**
  - The tested expression is evaluated once and must be optional. Otherwise it's `TP0200`.
  - The binding `v` is a `let` and follows the usual shadowing rules (`TP0207`).
  - In `while x is some v:`, `x` is re-tested on each iteration and `v` is bound in the body.
  - In an `elif` clause, the binding is visible only in that clause's branch.
- **`??`:**
  - The left operand must be optional (`TP0200` otherwise) and is evaluated first. The right operand is evaluated only when the left is `none`.
  - With `x: T?`, the default is checked against `T` as its expected type, and the result is `T`. If the default is itself a `T?`, the result is `T?`, so `a ?? b ?? 0` works.
- **`none` and inference:**
  - `none` has its own type, written `none`, which is assignable to every optional.
  - `let x = none` without an annotation is `TP0206`.
  - In list and dict literals without an expected type, `none` and `T` mix to `T?`, and `T` and `T?` mix to `T?`. So `[1, none]` is `list[int?]`.
- **Nesting (`TP0213`):** `T??` is reported at the type. Optionals never nest. `d.get(k)` on a dict whose values are already optional returns that same optional type: on a `dict[str, int?]` it gives `int?`, and a missing key and a stored `none` both give `none` (as in Python, and as with generics, §3.6).

### 3.5 Conditions
`if`, `elif`, and `while` conditions and the operands of `and`, `or`, and `not` must be `bool`. There's no truthiness, so `if xs:` is `TP0205`.

### 3.6 Generics (M8)
- **Declarations:** functions, structs and enums may take type parameters, in PEP 695 style: `def first[T](xs: list[T]) -> T?:`, `struct Box[T]:`, `enum Maybe[T]:`.
  - Type parameter names live in the type namespace of their definition, including a generic struct's methods. Methods take no type parameters of their own.
  - A repeated type parameter is `TP0214`. One that shadows an existing type name is `TP0207`.
- **Instantiation in types:** a generic struct or enum must be given exactly as many type arguments as it has parameters (`Box[int]`, `Maybe[list[str]]`). A count mismatch, including a bare `Box` or arguments on a non-generic type, is `TP0209`.
- **Type parameters are unconstrained.** Bodies are checked once, with each type parameter treated as an opaque type `T`:
  - A `T` may be assigned, passed, returned, stored in lists and fields, compared with `==`/`!=` against another `T`, tested with `in` on a `list[T]`, and displayed (`print`, `str`, f-strings).
  - Anything else on a `T` is `TP0200` or `TP0210`, as for any type lacking the operation: arithmetic, ordering, member access, method calls, indexing, iteration, `match`, or use as a dict key.
  - So a body that checks is valid for every type argument. Instantiation never reports new errors (apart from `TP0219`).
- **Type arguments are always inferred.** There's no explicit call-site syntax.
  - **Expected type first:** if the call has an expected type (§4.6), the return type is unified with it.
  - **Then arguments:** the remaining parameters are inferred from the argument types, in order. Lambda arguments come last (§3.7).
  - An argument whose type doesn't fit what the expected type inferred overrides it. So `let b: Box[float] = Box(1)` keeps `T = float`, but `let x: int? = first(xs)` with `xs: list[int?]` infers `T = int?`.
  - Arguments are then checked against the substituted parameter types, with the usual widening.
  - When two arguments infer `int` and `float` for the same parameter, it's `float`. Any other disagreement is `TP0200`.
  - A `none` argument infers nothing, and an argument of type `U?` for a `T?` parameter infers `T = U`.
  - A parameter left undetermined is `TP0206`. For example, `Nothing` from `enum Maybe[T]` has no expected type to infer from.
- Generic enum variants and generic struct constructors infer their type arguments the same way: `Box(5)` is `Box[int]`, and `Just("x")` is `Maybe[str]`.
- **Optionals don't nest, even through substitution:** `T?` with `T = int?` is `int?`, as in Python's `Optional[Optional[int]]`. Only a literal `T??` in source is `TP0213`.
- **Constraints (M8.1):** a function's type parameter may list the types it can stand for, as a PEP 695 constraint tuple: `def total[N: (int, float)](xs: list[N]) -> N:`.
  - **Valid tuples:** at least two distinct types, none of them optional and none mentioning a type parameter. Otherwise it's `TP0220`.
  - **Functions only:** constraints on a struct's or enum's type parameters are also `TP0220` in v0.1.
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
- **Function types** are written as in Python's `typing`. `Callable[[int, str], bool]` takes an `int` and a `str` and returns a `bool`, and `Callable[[], int]` takes nothing. A result of `none` means the function returns nothing: `Callable[[str], none]`.
  - `Callable` is a built-in type name, like `list`. A bare `Callable` is `TP0209`.
  - A function type is assignable only to an identical one. Parameter and result types must match exactly, with no widening. A lambda is checked against its expected type, though, so `lambda x: x` fits `Callable[[int], float]`.
  - Function types may appear wherever a type can: as `T?`, in lists, as dict values, as fields, and as type arguments. They can't be dict keys, and they aren't convertible to Python (§10.3).
- **Function values** are:
  - the name of a top-level function that isn't generic, used without calling it (`apply(double, xs)`)
  - a nested `def`
  - a `lambda`
  - Generic functions, built-in functions, methods, struct constructors and enum variants aren't values (`TP0200`, §9.1). Wrap them in a lambda instead: `lambda s: s.total()`.
- **Calls:** any expression of function type can be called: `f(x)`, `handlers[0](req)`, or `s.on_done()` for a field. Arguments are positional only (`TP0218`), and are checked against the parameter types. A wrong count is `TP0209`. Calling a `T?` is `TP0203`.
- **Lambdas:** `lambda x, y: x + y`. The body is a single expression, whose value is the result.
  - Parameters take no annotations. Their types come from the expected type (§4.6), which must be a function type with as many parameters as the lambda has (`TP0209` otherwise).
  - The body is checked against the expected result type. When the result is `none`, the body is checked as an expression statement, and its value is discarded.
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

---

## 4. Declarations

### 4.1 Bindings
- `let` is immutable. Reassigning it is `TP0202`.
- `var` is mutable.
- `const` is top-level only, and its initializer must be a literal or an arithmetic or string expression of literals. Constants are visible everywhere.
- **Scoping** is per block, as in C#. A name is visible from its declaration to the end of its block.
- **Shadowing** a name from an enclosing block is `TP0207`. Redeclaring a name in the same block is `TP0214`.
- **Top level:**
  - `def`, `struct`, `enum`, and `const` are hoisted and visible everywhere.
  - Top-level `let` and `var` are script locals. They're visible only to later top-level statements. Referencing one from inside a function is `TP0211`.

### 4.2 Functions
- `def` declares a function. Recursion and mutual recursion are allowed, since functions are hoisted. A `def` inside a block is a nested function, which isn't hoisted (§3.7).
- Every path in a function with a declared return type must `return` a value (`TP0208`).
- Arguments are positional only. A wrong count is `TP0209`.
- Overloading isn't supported. A duplicate name is `TP0214`.

### 4.3 Structs
- A struct is a named record with typed fields and, from M5, methods.
- **Construction:** `Service("api", 3, 3)` or `Service(name="api", replicas=3, healthy=3)`. With keyword form, every field must be given exactly once.
- Fields are mutable through any binding: `s.healthy = 2` is valid even when `s` is a `let`. `let` fixes the binding, not the object.
- Structs are reference types with structural equality (`==` compares fields).
- **Methods** take an explicit `self` as the first parameter, typed implicitly as the enclosing struct. They're called as `s.method(args)`.
- An unknown field or method is `TP0210`.

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
  - An enum's name lives in the type namespace and the global value scope, like a struct's.
  - Each variant name also occupies the global value scope. A variant can't share its name with a function, constant, struct, enum or built-in (`TP0214`).
  - Two enums may share a variant name, but then only the qualified form is valid. The unqualified form is `TP0216`, even when an expected type would disambiguate.
  - A local can't take a variant's name (`TP0207`).
- **Construction details:**
  - A variant with a payload follows the struct constructor rules of §4.6 (`TP0209`, `TP0210`). Without arguments, it's `TP0209`.
  - A variant without a payload is a value. Calling it, as in `Ok()`, is `TP0200`.
  - An enum name used as a value is `TP0200`. So is member access on an enum value: payloads are reached only through `match`.

### 4.5 Names and checking rules
- **Namespaces:** types (`int`, `float`, `bool`, `str`, user structs and enums) and values (functions, constants, variables, parameters, built-in functions) are separate. An unknown type name is `TP0201`.
- **Global scope:** functions, constants and built-in functions share one global scope. Defining a name twice there, including a `def` or `const` with a built-in function's name, is `TP0214`.
- **Shadowing (`TP0207`)** covers every name visible at the declaration, including globals and built-ins: `let len = 3` is `TP0207`. A function body can't see top-level `let`/`var` bindings (§4.1), so declaring the same name inside a function is allowed.
- **Parameters** are immutable, like `let`. They're declared in the function's outermost scope, so `let x` in a body with a parameter `x` is `TP0207`. Two parameters with the same name are `TP0214`.
- **Assignment** to anything other than a `var` (a `let`, a parameter, a loop variable, a constant or a function) is `TP0202`. Compound assignment `x op= e` is checked as `x = x op e`, so `var n = 1` then `n /= 2` is `TP0200`.
- **Use before declaration:** a local or top-level binding referenced before its `let`/`var` is `TP0201`.
- **Functions as values** follow §3.7 (M11). Calling something that isn't a function is `TP0200`.
- **Keyword arguments** in a call to anything other than a struct or variant constructor, a `py` value, or `sorted`/`sort` (§6) are `TP0218`. So is a keyword that `sorted` or `sort` doesn't take.
- **Return values:**
  - `return` with a value in a function without a return type, or `return` without one in a function with a return type, is `TP0200`.
  - Using a call to a function without a return type as a value is `TP0200`.
- **Return-path analysis (`TP0208`).** A statement *terminates* if it is a `return`, or an `if` with an `else` whose every branch ends in a terminating statement. (In M7, a `match` whose arms all terminate also counts.) A block terminates if any statement in it does. A function with a return type whose body doesn't terminate is `TP0208`. Loops never terminate for this purpose, including `while True:`.
- **Constants:** a `const` initializer may contain only literals, unary `-`, and the operators `+ - * / // %`. Anything else, including a reference to another constant, is `TP0217`. Evaluation happens at run time, so `const X = 1 // 0` panics at startup.
- **Integer literals:** a literal outside the 64-bit signed range is `TP0200`. The exception is `-9223372036854775808`, where `-` is applied directly to the literal.
- **Expression statements:** any expression may be used as a statement, and its value is discarded.
- **Error types:** once an expression has an error, its type is treated as compatible with everything. This means one mistake is reported once, not again at every use.

### 4.6 Structs, lists and dicts: checking rules
- **Struct names** live in the type namespace and also in the global value scope, as the struct's constructor. So a struct can't share a name with a function, constant or built-in (`TP0214`), and a local can't take its name (`TP0207`). A struct named `int`, `float`, `bool`, `str`, `list`, `dict`, `py`, `Callable` or `tuple` is also `TP0214`.
- **Members:** field and method names within one struct must be distinct (`TP0214`). Field types may name any struct, including the struct itself, for example through `list[Node]`.
- **Construction:**
  - The positional form takes exactly one argument per field, in declaration order (`TP0209`).
  - The keyword form must name every field exactly once. A missing or repeated field is `TP0209`, and an unknown name is `TP0210`.
  - Arguments are evaluated in source order.
- **Methods** are hoisted with their struct. Inside a method, `self` is an immutable parameter (`TP0202` to reassign), and other methods are reached as `self.m()`. A bare `m()` refers to a global function. `self` outside a method is `TP0201`.
- **Member access:**
  - An unknown field or method is `TP0210`.
  - A method used without calling it is `TP0200`. So is calling a field, unless the field has a function type (§3.7).
  - Assigning to a method is `TP0202`.
- **Expected types.** An expression is checked against an expected type when its context supplies one: an annotation, a parameter (including constructor fields and built-in method parameters), a return type, an assignment target (variable, field or element), or an enclosing list or dict literal that has an expected type. `[]` and `{}` need one (`TP0206`). With an expected type, each element is checked against the expected element type, so `let xs: list[float] = [1, 2]` is valid.
- **Literals without an expected type:** all elements (or all keys, or all values) must have the same type, except that `int` and `float` mix to `float`. A mismatch is `TP0200` at the first element that differs.
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
- **Module files contain declarations only:** `import`, `def`, `struct`, `enum` and `const`. A top-level statement (including `let` and `var`) in a module other than the main file is `TP0222`. Only the main file has a top level that runs.
- **Exports:** a module exports every name it defines, meaning its functions, structs, enums, their variants, and constants. There's no privacy, no re-export (a name a module itself imported can't be imported from it), and no `from m import *` (`TP0100`).
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
- The scrutinee can be an enum, `int`, `str`, or `bool`.
- **Patterns:**
  - Variant patterns bind payload fields positionally: `Degraded(h, t)`. With a payload, the binding count must equal the field count. A variant with a payload may also be matched as `Degraded` without parentheses, binding nothing.
  - Literal patterns apply to `int`, `str`, and `bool`.
  - `_` matches anything.
- **Exhaustiveness:**
  - For an enum, every variant must be covered, or there must be a `_` arm. Otherwise it's `TP0204`, and the message lists the missing variants.
  - For `bool`, the arms must cover `True` and `False`, or include `_`.
  - For `int` and `str`, a `_` arm is required.
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
  - **Duplicates (`TP0300`):** a duplicate arm is the same variant, literal or `True`/`False` as an earlier arm, regardless of its bindings. Unreachable arms are still checked, but not emitted.
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
| `== !=` | same type, or int/float | bool | structural for structs, enums, lists, dicts, tuples; identity for functions (§3.7) |
| `< <= > >=` | numeric, or str (ordinal), or tuples of those (M12) | bool | tuples compare lexicographically |
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
  - Each part is an `int`, or an `int?` where `none` means omitted.
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
- `int(x: float) -> int` truncates toward zero. An `int` argument is returned unchanged. `int(x: str) -> int?` parses, returning `none` on failure. It accepts an optional sign and decimal digits with `_` only between digits (as in §1.3), with surrounding whitespace allowed. A value outside the int range gives `none`.
- `float(x: int) -> float`. A `float` argument is returned unchanged. `float(x: str) -> float?` accepts the same forms as `int(str)`, plus an optional fraction and exponent (`1.5`, `2e-3`, `.5`, `5.`), and `inf`, `infinity` and `nan` in any case. Otherwise it returns `none`.
- `range` can only be used as the iterable of a `for` loop. Any other use is `TP0200`.
- `sorted(xs: list[T], key=…, reverse=False) -> list[T]` (M11) returns a new list in ascending order.
  - `key: Callable[[T], K]` gives each element's sort key. It's called once per element, in list order, before any reordering.
  - The elements, or the keys when `key` is given, must be `int`, `float` or `str`, or tuples of those (M12), and are ordered as by `<`. Otherwise it's `TP0200`. A NaN sorts before every other float.
  - `reverse: bool` sorts in descending order.
  - The sort is stable: equal elements keep their original order, with `reverse=True` too, as in Python.
  - `key` and `reverse` are optional and keyword-only.
- `map(f: Callable[[T], U], xs: list[T]) -> list[U]` and `filter(f: Callable[[T], bool], xs: list[T]) -> list[T]` (M11) call `f` once per element, in order, and return new lists. Python's versions return iterators, which Taipan doesn't have.
- `enumerate(xs)` and `enumerate(xs, start)` (M12) give a new list of `(index, element)` tuples: `list[tuple[int, T]]` for a `list[T]`, or `list[tuple[int, str]]` for a `str`'s characters. `start` is an `int` that defaults to 0. It may be given by position, or as the keyword argument `start=`, as in Python.
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
| `none` | `none` |
| `str` | as-is at top level. Inside a list, dict or struct, written as a literal: in `"` quotes, with `\\`, `\"`, `\n` and `\t` escaped |
| list | `[1, 2, 3]` |
| tuple | `(1, "a")` (M12) |
| dict | `{"a": 1}` |
| struct | `Service(name="api", replicas=3, healthy=3)` |
| enum | `Ok`, `Degraded(healthy=1, total=4)` |
| function | `<function name>`, `<function <lambda>>` (M11) |

---

## 7. Runtime errors (panics)
- A panic prints `panic: <message>\n  at <file>.tpn:<line>` to stderr and exits with code **101**. Output the program printed before the panic still appears. `<file>` is the source path as given on the command line (§9). `<line>` is the line of the statement that panicked, in the innermost Taipan function.
- The emitted C# carries `#line` directives so .NET stack traces map back to `.tpn` lines.
- **Panic sources and messages:**
  - int overflow: `integer overflow`. This includes `-x` and `x // -1` for the most negative int.
  - division by zero (`/`, `//`, `%`): `division by zero`
  - `int(x)` of a float that is NaN, infinite, or outside the int range: `cannot convert <x> to int`, with `<x>` in display form
  - index out of range: `index <i> out of range for length <n>`
  - missing dict key: `missing key <k>`, with `<k>` as written inside a container (strings quoted)
  - `pop()` on an empty list: `pop from empty list`
  - mutation during iteration: `list mutated during iteration` / `dict mutated during iteration`. Any change to the list or dict being iterated counts, including assigning to an existing element.
  - `split("")` / `replace("", …)`: `empty separator`
  - a slice step of 0: `slice step cannot be zero` (M12)
  - Python interop (§10.5): `cannot convert Python <type> to <T>`, `Python <ExceptionType>: <message>`, `cannot load Python: <reason>`
  - failed non-optional `as` (M10)

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
| `struct S` | `public sealed class S : Taipan.Runtime.IStruct`, with a mutable `{ get; set; }` property per field, a positional constructor, methods as instance methods (`self` is `this`), and `ToString` overridden to the display form. Not a C# record: Taipan equality is deep (lists inside structs compare by contents), so `==` on structs, lists and dicts calls `Taipan.Runtime.Equality.Equal` rather than C# `Equals`. |
| `enum E` | `public abstract record E` with a private constructor; each variant is `public sealed record V(...) : E` nested inside |
| `T?` | `T?` |
| `list[T]` / `dict[K, V]` | `List<T>` / `OrderedDictionary<K, V>`. `for` over them goes through a runtime iterator that turns concurrent modification into the §7 panic. |
| `tuple[A, B]` | a C# value tuple `(A, B)` (M12), with elements read as `Item1`, `Item2`, …. Unpacking stores the value in a temporary first. `==` uses `Taipan.Runtime.Equality.Equal`, and ordering uses a runtime comparison that orders strings by ordinal. In generic instance names, a tuple type is written `tuple_` followed by its element types: `first_tuple_int_str`. |
| slices | `Taipan.Runtime.Ops.Slice(target, start, stop, step)`, with `null` for an omitted part (M12) |
| generics | Monomorphized. Each distinct instantiation the program uses becomes its own C# method or type, named after its type arguments: `first_int`, `Box_str`, `Box_list_int`, `Maybe_int_opt` (for `Maybe[int?]`). A clash with another name gets a numeric suffix. Generic definitions that are never instantiated are type-checked but not emitted. |
| function types | `Taipan.Runtime.Function<D>` (M11), with `D` a `Func<…>` or `Action<…>`. It wraps the delegate and the display name, and calls go through the delegate. A top-level function used as a value is one cached object per function, and each evaluation of a lambda or nested `def` creates a new object, so `==` is reference equality. In generic instance names, a function type is written `fn_` followed by its parameter and result types: `apply_fn_int_str`, `Box_fn_none`. |
| lambdas, nested `def` | C# lambdas, which capture locals by reference. A `for x in range(…)` loop copies its variable into a fresh local each iteration, so captures see that iteration's value. |
| `const` | a `static` field, assigned at the start of `TopLevel` in source order |
| modules | All files go into one C# compilation unit, with no per-module classes. A name defined in two modules gets a numeric suffix in the C# output (`area`, `area2`). |
| locals | a typed C# local. A Taipan local whose name C# scoping would reject (the same name declared in a nested block and again later in the outer block) is renamed with a numeric suffix. |
| `for x in range(a, b)` | a C# `for` loop. `a` and `b` are evaluated once, before the loop. |
| `x is some v` | `x is {} v`. C# scopes pattern variables more widely than Taipan, so a binding whose name is used anywhere else in the method gets a numeric suffix. |
| `x is none` / `none` | `x is null` / `null` |
| `x ?? d` | `x ?? d` |
| `match` | `switch` statement with type, positional and constant patterns; each arm's body in its own braces. When there's no `_` arm, an emitted `default:` throws `UnreachableException` (exhaustiveness is guaranteed by the Taipan checker). A Taipan `break` inside an arm becomes `goto` to a label after the loop, since a C# `break` would leave the `switch`. Variants implement `Taipan.Runtime.IStruct`, and `==` on enums uses `Taipan.Runtime.Equality.Equal`, as for structs. |
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

- **Warnings** (currently only `TP0300`) print as `<file>:<line>:<col>: warning TP<code>: <message>`. They don't stop compilation or change the exit code. They're printed with the type checker's errors, before the program runs.

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
| TP0215 | `break`/`continue` outside loop, or `return` outside function |
| TP0216 | ambiguous enum variant; qualify it |
| TP0217 | `const` initializer is not a constant expression |
| TP0218 | keyword arguments outside a constructor |
| TP0219 | generic instantiation too deep (polymorphic recursion) |
| TP0220 | invalid type parameter constraint |
| TP0221 | module not found |
| TP0222 | top-level statement in an imported module |
| TP0223 | nested function with type parameters |
| TP0300 | warning: unreachable match arm |

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
| TP0200 | `'is some' needs an optional, found <T>` / `'is none' needs an optional, found <T>` / `'??' needs an optional on the left, found <T>` |
| TP0200 | `'<m>' is a method; call it` / `'<f>' is a field, not a method` / `cannot index <T>` / `dict keys must be int, str, or bool, found <T>` |
| TP0201 | `undefined name '<x>'` / `undefined type '<T>'` / `module '<m>' has no definition '<x>'` |
| TP0202 | `cannot assign to '<x>': it is a <let binding\|parameter\|loop variable\|constant\|function\|method>`, with help `declare it with 'var'` for a `let` binding / `cannot assign to an element of str` / `cannot assign to an element of a tuple` |
| TP0203 | `expected <T>, found <T>?` / `<T>? must be narrowed before use`, with help `narrow it with 'is some', or give a default with '??'` |
| TP0204 | `non-exhaustive match (missing: <a>, <b>)` |
| TP0205 | `condition must be bool, found <T>` (with help `convert it with 'as bool'` for `py`) |
| TP0206 | `cannot infer the type of an empty list` / `... empty dict` / `cannot infer the type of none` / `cannot infer '<T>' for '<f>'` / `cannot infer the type of lambda parameter '<x>'`, with help `add a type annotation` |
| TP0207 | `'<x>' shadows an outer binding` |
| TP0208 | `not all paths in '<f>' return a value` |
| TP0209 | `'<S>' takes <n> type arguments, found <m>` (`type argument` when n is 1) |
| TP0209 | `'<v>' has <n> fields, found <m> bindings` (variant patterns; `field` / `binding` when the count is 1) |
| TP0209 | `<T> has <n> elements, found <m> names` (unpacking) / `a tuple needs at least two elements` (M12) |
| TP0209 | `'<f>' takes <n> arguments, found <m>` (`argument` when n is 1; `1 or 2 arguments` for `range`; `function takes …` when the callee isn't a name) / `missing field '<f>' in '<S>'` / `field '<f>' is given more than once` / `argument '<k>' is given more than once` |
| TP0209 | `expected a lambda with <n> parameters, found <m>` (`parameter` when n is 1) / `'Callable' takes a parameter list and a result type` |
| TP0210 | `<T> has no field '<x>'` / `<T> has no method '<x>'` / `<E> has no variant '<x>'` |
| TP0211 | `'<f>' cannot use top-level variable '<x>'`, with help `pass it as a parameter, or make it a const` |
| TP0213 | `optional types cannot be nested` |
| TP0214 | `duplicate definition of '<x>'` |
| TP0215 | `'break' outside a loop` / `'continue' outside a loop` / `'return' outside a function` |
| TP0216 | `'<v>' is ambiguous: it is a variant of <E1> and <E2>`, with help `qualify it, e.g. '<E1>.<v>'` |
| TP0217 | `const initializer must be a constant expression` |
| TP0218 | `keyword arguments are only allowed in constructors` / `'<f>' has no keyword argument '<k>'` (`sorted`, `sort`) |
| TP0219 | `instantiating '<f>' needs '<f>[<T>]', which is too deep`, at the call that starts the chain |
| TP0221 | `module '<m>' not found`, with help `looked for <path>` / `'python' is not a module`, with help `use 'from python import <module>'` |
| TP0222 | `only the main file can have top-level statements` |
| TP0223 | `nested functions cannot have type parameters` |
| TP0220 | `a constraint needs at least two types` / `'<T>' is listed twice in the constraint` / `a constraint cannot be optional or generic` / `constraints are only supported on functions` |
| TP0300 | `unreachable match arm` |

Types are written as in source: `int`, `str?`, `list[int]`. The type of `none` is written `none`, and a missing value is written `nothing`.

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
  - `print`, `str()` and f-string holes. A `None`, `bool`, `int`, `float`, `str`, `list`, `tuple` or `dict` displays by Taipan's rules (§6), so `None` is `none` and strings inside containers are quoted. A `tuple` is written `(1, 2)`. Any other object displays as Python's `str(x)`.
- **The result of every `py` operation is `py`, including comparisons.** A `py` used as a condition, or with `and`, `or` or `not`, is `TP0205`, with help `convert it with 'as bool'`.
- `py` may annotate variables, parameters, return types, fields and collection elements (`list[py]`). It can't be a dict key (§3.1). A list, dict or optional containing `py` can't be compared with `==` (`TP0200`), since Python objects have no Taipan equality.

### 10.3 Taipan → Python
- These Taipan types are *convertible*: `int`, `float`, `bool`, `str`, `none`, `list[T]` and `dict[K, V]` of convertible types, tuples of convertible types (M12, converted to a Python `tuple`), `T?` of a convertible `T`, and `py` itself.
- A convertible value is converted when it's passed into a `py` operation, and when it's assigned to something of type `py` (so `let x: py = [1, 2]` is valid).
- Lists and dicts are copied, so changes on either side aren't seen by the other.
- **Mixed literals:** in a `py` operation's arguments and operands, a list literal has element type `py` and a dict literal has value type `py`, so their elements may mix types: `json.dumps({"xs": [1, 2], "ok": True, "none": none})`.
- A struct or enum value, or any other non-convertible type, passed to Python is `TP0200` (`cannot pass <T> to Python`).

### 10.4 Python → Taipan: `as`
- A `py` value reaches a static type only through `as` (cast, §2). Any other use where a static type is expected is `TP0200`. `as` applied to a non-`py` value is also `TP0200`.
- `x as T` converts or panics. `x as T?` converts or gives `none`.
- **Targets and what each accepts:**
  - `int`: a Python `int` in the 64-bit range. A `bool` is not accepted, even though Python's `bool` is an `int`.
  - `float`: a Python `float`, or an `int` (not `bool`), converted.
  - `bool`: a Python `bool`.
  - `str`: a Python `str`.
  - `list[T]`: a Python `list` or `tuple` whose every element converts to `T`.
  - `dict[K, V]`: a Python `dict` whose every key converts to `K` and every value to `V`.
  - `tuple[A, B, …]` (M12): a Python `tuple` or `list` with exactly that many elements, each converting to its type.
  - `T?` inside another target (for example `list[int?]`): Python `None` becomes `none`, and anything else converts as `T`. As the outer target, `x as int?` gives `none` both for `None` and on a mismatch.
  - `py`: always succeeds, so `x as py` is allowed but pointless.
  - Any other target (a struct, an enum) is `TP0200`.
- Conversion is all-or-nothing: one failing element fails the whole conversion.

### 10.5 Errors at run time
- A failed `as T` panics with `cannot convert Python <type> to <T>`, where `<type>` is the Python type's name (`str`, `NoneType`).
- A Python exception during any `py` operation panics with `Python <ExceptionType>: <message>`. Exception handling is post-v0.1.
- If Python can't be loaded, the first Python import panics with `cannot load Python: <reason>`.
- All of these exit with 101 and report the Taipan line, as other panics do (§7).

### 10.6 Locating and running Python
- The runtime embeds CPython through Python.NET (pythonnet). It looks for the CPython shared library in this order:
  1. the `PYTHONNET_PYDLL` environment variable (pythonnet's own setting)
  2. the library reported by the first of `python3` or `python` found on `PATH` (`sysconfig`'s `LIBDIR` and `LDLIBRARY`)
- Python is initialized once per process and never shut down. The GIL is taken around each Python operation, so `taipan run` can run programs on different threads of one process.

---

## 11. Deferred (post-v0.1)
- protocols (structural interfaces)
- generic functions, built-ins, methods and constructors as values (wrap them in a lambda)
- iterators and generators (`map` and `filter` return lists)
- tuple patterns in `match`
- nested unpacking (`let (a, b), c = …`) and unpacking lists
- slice assignment and `del`
- `zip` of more than two lists
- `match` as an expression
- flow-sensitive narrowing after early return
- exceptions and `try`
- format specs in f-strings
- an LSP server
- NativeAOT publish
- emitting CPython extension modules (interop level 3)
