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
  `def struct enum let var const if elif else while for in match return pass break continue and or not is some none True False as from import`
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
top_item      = func_def | struct_def | enum_def | const_def | stmt ;

const_def     = "const" NAME [ ":" type ] "=" expr NEWLINE ;

func_def      = "def" NAME [ type_params ] "(" [ params ] ")" [ "->" type ] ":" block ;
params        = param { "," param } [ "," ] ;
param         = NAME ":" type ;
type_params   = "[" NAME { "," NAME } "]" ;                       (* M8 *)

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
let_stmt      = "let" NAME [ ":" type ] "=" expr ;
var_stmt      = "var" NAME [ ":" type ] "=" expr ;
assign_stmt   = target ( "=" | "+=" | "-=" | "*=" | "/=" ) expr ;
target        = NAME | postfix "." NAME | postfix "[" expr "]" ;
return_stmt   = "return" [ expr ] ;

compound_stmt = if_stmt | while_stmt | for_stmt | match_stmt ;
if_stmt       = "if" condition ":" block { "elif" condition ":" block } [ "else" ":" block ] ;
condition     = expr "is" "some" NAME | expr "is" "none" | expr ;
while_stmt    = "while" condition ":" block ;
for_stmt      = "for" NAME "in" expr ":" block ;
match_stmt    = "match" expr ":" NEWLINE INDENT arm { arm } DEDENT ;
arm           = pattern ":" ( simple_stmt NEWLINE | block ) ;
pattern       = "_" | [ "-" ] INT | STRING | "True" | "False"
              | qual_name [ "(" [ NAME { "," NAME } ] ")" ] ;
qual_name     = NAME [ "." NAME ] ;

type          = base_type { "?" } ;                               (* more than one "?" is TP0213, §3.1 *)
base_type     = "int" | "float" | "bool" | "str" | "py"           (* py: M10 *)
              | "list" "[" type "]"
              | "dict" "[" type "," type "]"
              | NAME [ "[" type { "," type } "]" ] ;

(* Expressions, lowest to highest precedence *)
expr          = coalesce ;
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
postfix       = atom { call | "." NAME | "[" expr "]" } ;
call          = "(" [ arg { "," arg } [ "," ] ] ")" ;
arg           = expr | NAME "=" expr ;
atom          = INT | FLOAT | STRING | FSTRING | "True" | "False" | "none"
              | NAME | "self" | "(" expr ")"
              | "[" [ expr { "," expr } [ "," ] ] "]"
              | "{" [ expr ":" expr { "," expr ":" expr } [ "," ] ] "}" ;
```

**Grammar notes**
- `a < b < c` is `TP0212`. Write `a < b and b < c`.
- An `arm` may take a simple statement on the same line (`Ok: print("ok")`) or an indented block.
- Keyword arguments are allowed only in struct and enum-variant constructors (§4.3). Within one call, arguments are either all positional or all keyword. Mixing them is `TP0100`.
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
| `dict[K, V]` | hash map; `K` ∈ {`int`, `str`, `bool`} | `Dictionary<K, V>` |
| `T?` | optional `T` | `T?` (C# nullable) |
| `py` | dynamic Python object (M10) | `PyObject` |

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

### 3.5 Conditions
`if`, `elif`, and `while` conditions and the operands of `and`, `or`, and `not` must be `bool`. There's no truthiness, so `if xs:` is `TP0205`.

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
- `def` declares a function. Recursion and mutual recursion are allowed, since functions are hoisted.
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

### 5.2 Loops
- `for x in e` works when `e` is:
  - `list[T]`, giving `x: T`
  - `dict[K, V]`, giving `x: K` (keys, insertion order)
  - `str`, giving `x: str` (one character each)
  - `range(...)`, giving `x: int`
- The loop variable is a `let` scoped to the body.
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
| `== !=` | same type, or int/float | bool | structural for structs, enums, lists, dicts |
| `< <= > >=` | numeric, or str (ordinal) | bool | |
| `in`, `not in` | T in list[T]; K in dict[K,V]; str in str | bool | |
| `??` | T?, T | T | |
| `and or not` | bool | bool | short-circuit |

Comparing values of unrelated types is `TP0200`.

### 5.4 Indexing
- `list[i]` with an int index: out of range panics. Negative indices count from the end, as in Python.
- `dict[k]`: a missing key panics. Use `d.get(k) -> V?` instead.
- `str[i] -> str`, one character, with the same rules as list.
- Slicing is post-v0.1.

---

## 6. Built-ins (v0.1)

**Functions**
- `print(*args)` writes each argument's display form, space-separated, then a newline.
- `len(x) -> int` takes a list, dict, or str.
- `range(stop)` and `range(start, stop)` iterate only (they're not a list).
- `str(x) -> str` gives the display form.
- `int(x: float) -> int` truncates toward zero. `int(x: str) -> int?` parses, returning `none` on failure.
- `float(x: int) -> float`. `float(x: str) -> float?`.

**Methods**
- `list[T]`: `append(x: T)`, `pop() -> T` (panics if empty), `insert(i: int, x: T)`, `contains(x: T) -> bool`
- `dict[K, V]`: `get(k: K) -> V?`, `keys() -> list[K]`, `values() -> list[V]`, `remove(k: K) -> bool`
- `str`: `upper()`, `lower()`, `strip()`, `split(sep: str) -> list[str]`, `startswith(p: str) -> bool`, `endswith(p: str) -> bool`, `replace(old: str, new: str) -> str`

**Display form** (used by `print`, `str()`, and f-strings)

| Type | Format |
|---|---|
| `int` | decimal |
| `float` | shortest round-trip form, with `.0` appended when integral; `inf`, `-inf`, `nan` |
| `bool` | `True` / `False` |
| `none` | `none` |
| `str` | as-is at top level; quoted with `"` inside containers |
| list | `[1, 2, 3]` |
| dict | `{"a": 1}` |
| struct | `Service(name="api", replicas=3, healthy=3)` |
| enum | `Ok`, `Degraded(healthy=1, total=4)` |

---

## 7. Runtime errors (panics)
- A panic prints `panic: <message>\n  at <file>.tpn:<line>` to stderr and exits with code **101**.
- The emitted C# carries `#line` directives so .NET stack traces map back to `.tpn` lines.
- **Panic sources:**
  - int overflow
  - division by zero
  - index out of range
  - missing dict key
  - `pop()` on an empty list
  - mutation during iteration
  - failed non-optional `as` (M10)

---

## 8. Code generation (C#)

The output is one C# compilation unit per program, compiled in-process by Roslyn with these options:
- `CheckForOverflowUnderflow = true`
- nullable enabled
- warnings not treated as errors in generated code

| Taipan | Emitted C# |
|---|---|
| program | `public static class TaipanProgram { static void Main() { ...top-level stmts... } ...functions... }` |
| identifiers | always verbatim-escaped (`@name`) to avoid C# keyword collisions |
| `def f(a: int) -> str` | `public static string @f(long @a)` |
| `struct S` | `public sealed record class S(...)` with mutable `{ get; set; }` properties; `ToString` overridden to the display form |
| `enum E` | `public abstract record E` with a private constructor; each variant is `public sealed record V(...) : E` nested inside |
| `T?` | `T?` |
| `x is some v` | `x is {} v` |
| `match` | `switch` statement with type and constant patterns; emitted `default:` throws `UnreachableException` (exhaustiveness is guaranteed by the Taipan checker) |
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
| TP0300 | warning: unreachable match arm |

New codes go into this table before they're implemented.

---

## 10. Python interop (M10) — design sketch, revisit before implementing

- **Import:** `from python import requests` binds `requests: py`.
- **Dynamic operations on `py`:** attribute access, calls (positional and keyword), indexing, and arithmetic all return `py`, with no static checking.
- **Taipan → Python:** implicit at call boundaries for `int`, `float`, `bool`, `str`, `list`, `dict`, and `none`.
- **Python → Taipan:** only through `as`:
  - `x as T?` is a checked conversion that returns `none` on mismatch.
  - `x as T` panics on mismatch.
  - Valid targets are `int`, `float`, `bool`, `str`, `list[T]`, and `dict[K, V]`. Conversion is recursive.
- A `py` value can't flow into a static type any other way (`TP0200`).
- **Implementation:**
  - Python.NET (pythonnet) embeds CPython.
  - The runtime initializes Python lazily on first import.
  - The GIL is acquired per boundary call.
- Python exceptions become panics, unless caught. Exception handling is post-v0.1.

---

## 11. Deferred (post-v0.1)
- protocols (structural interfaces)
- lambdas and closures
- tuples
- slicing
- `match` as an expression
- flow-sensitive narrowing after early return
- exceptions and `try`
- format specs in f-strings
- an LSP server
- NativeAOT publish
- emitting CPython extension modules (interop level 3)
