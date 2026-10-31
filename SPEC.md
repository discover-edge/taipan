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
type_params   = "[" NAME { "," NAME } "]" ;                       (* M8, §3.6 *)

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
| `dict[K, V]` | hash map that keeps insertion order; `K` ∈ {`int`, `str`, `bool`}, otherwise `TP0200` | `OrderedDictionary<K, V>` |
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
  - **Then arguments:** the remaining parameters are inferred from the argument types, in order.
  - Arguments are then checked against the substituted parameter types, with the usual widening, so `let b: Box[float] = Box(1)` is valid.
  - When two arguments infer `int` and `float` for the same parameter, it's `float`. Any other disagreement is `TP0200`.
  - A `none` argument infers nothing, and an argument of type `U?` for a `T?` parameter infers `T = U`.
  - A parameter left undetermined is `TP0206`. For example, `Nothing` from `enum Maybe[T]` has no expected type to infer from.
- Generic enum variants and generic struct constructors infer their type arguments the same way: `Box(5)` is `Box[int]`, and `Just("x")` is `Maybe[str]`.
- **Optionals don't nest, even through substitution:** `T?` with `T = int?` is `int?`, as in Python's `Optional[Optional[int]]`. Only a literal `T??` in source is `TP0213`.
- **Constraints** such as `[N: (int, float)]` or `[K: (int, str, bool)]` (PEP 695 constraint tuples) are planned for M8.1. Until then, a type parameter with a constraint is `TP0100`.
- **Polymorphic recursion:** a generic function that, through its own calls, needs ever-larger instantiations (for example, `f[T]` calling `f` with a `list[T]`) is `TP0219`. It's reported once the instantiation chain is 32 deep.

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
- **Functions aren't values.** A function name can only be called. Any other use is `TP0200`. Calling something that isn't a function is also `TP0200`.
- **Keyword arguments** in a call to anything other than a struct or variant constructor are `TP0218`.
- **Return values:**
  - `return` with a value in a function without a return type, or `return` without one in a function with a return type, is `TP0200`.
  - Using a call to a function without a return type as a value is `TP0200`.
- **Return-path analysis (`TP0208`).** A statement *terminates* if it is a `return`, or an `if` with an `else` whose every branch ends in a terminating statement. (In M7, a `match` whose arms all terminate also counts.) A block terminates if any statement in it does. A function with a return type whose body doesn't terminate is `TP0208`. Loops never terminate for this purpose, including `while True:`.
- **Constants:** a `const` initializer may contain only literals, unary `-`, and the operators `+ - * / // %`. Anything else, including a reference to another constant, is `TP0217`. Evaluation happens at run time, so `const X = 1 // 0` panics at startup.
- **Integer literals:** a literal outside the 64-bit signed range is `TP0200`. The exception is `-9223372036854775808`, where `-` is applied directly to the literal.
- **Expression statements:** any expression may be used as a statement, and its value is discarded.
- **Error types:** once an expression has an error, its type is treated as compatible with everything. This means one mistake is reported once, not again at every use.

### 4.6 Structs, lists and dicts: checking rules
- **Struct names** live in the type namespace and also in the global value scope, as the struct's constructor. So a struct can't share a name with a function, constant or built-in (`TP0214`), and a local can't take its name (`TP0207`). A struct named `int`, `float`, `bool`, `str`, `list`, `dict` or `py` is also `TP0214`.
- **Members:** field and method names within one struct must be distinct (`TP0214`). Field types may name any struct, including the struct itself, for example through `list[Node]`.
- **Construction:**
  - The positional form takes exactly one argument per field, in declaration order (`TP0209`).
  - The keyword form must name every field exactly once. A missing or repeated field is `TP0209`, and an unknown name is `TP0210`.
  - Arguments are evaluated in source order.
- **Methods** are hoisted with their struct. Inside a method, `self` is an immutable parameter (`TP0202` to reassign), and other methods are reached as `self.m()`. A bare `m()` refers to a global function. `self` outside a method is `TP0201`.
- **Member access:**
  - An unknown field or method is `TP0210`.
  - A method used without calling it is `TP0200`, and so is calling a field.
  - Assigning to a method is `TP0202`.
- **Expected types.** An expression is checked against an expected type when its context supplies one: an annotation, a parameter (including constructor fields and built-in method parameters), a return type, an assignment target (variable, field or element), or an enclosing list or dict literal that has an expected type. `[]` and `{}` need one (`TP0206`). With an expected type, each element is checked against the expected element type, so `let xs: list[float] = [1, 2]` is valid.
- **Literals without an expected type:** all elements (or all keys, or all values) must have the same type, except that `int` and `float` mix to `float`. A mismatch is `TP0200` at the first element that differs.
- **Assignment targets:**
  - `xs[i] = v` and `d[k] = v` are allowed (a `d[k]` assignment inserts or updates).
  - `s[i] = v` on a `str` is `TP0202`.
  - Compound forms (`xs[f()] += 1`, `make().count += 1`) evaluate the target's sub-expressions once.
- **Indexing** applies to `list[T]` (`int` index, gives `T`), `dict[K, V]` (`K` key, gives `V`), and `str` (`int` index, gives `str`). Anything else is `TP0200`.
- **Built-in methods** follow §6, with argument counts checked by `TP0209`. `d.get` arrives with optionals in M6.

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
- `str[i] -> str`, one character, with the same rules as list. Characters are Unicode code points, as for `len`.
- Slicing is post-v0.1.

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

**Methods**
- `list[T]`: `append(x: T)`, `pop() -> T` (removes and returns the last element; panics if empty), `insert(i: int, x: T)` (Python semantics: a negative `i` counts from the end, and out-of-range indices clamp), `contains(x: T) -> bool`
- `dict[K, V]`: `get(k: K) -> V?` (M6), `keys() -> list[K]`, `values() -> list[V]` (new lists, in insertion order), `remove(k: K) -> bool` (`True` if the key was present)
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
| dict | `{"a": 1}` |
| struct | `Service(name="api", replicas=3, healthy=3)` |
| enum | `Ok`, `Degraded(healthy=1, total=4)` |

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
| generics | Monomorphized. Each distinct instantiation the program uses becomes its own C# method or type, named after its type arguments: `first_int`, `Box_str`, `Box_list_int`, `Maybe_int_opt` (for `Maybe[int?]`). A clash with another name gets a numeric suffix. Generic definitions that are never instantiated are type-checked but not emitted. |
| `const` | a `static` field, assigned at the start of `TopLevel` in source order |
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
| TP0200 | `'<f>' is a function; call it` / `'<x>' is not a function` |
| TP0200 | `integer literal is out of range` |
| TP0200 | `'range' can only be used in a for loop` / `cannot iterate over <T>` |
| TP0200 | `'<T>' is inferred as both <A> and <B>` (type-argument inference) |
| TP0200 | `cannot match on <T>` / `'<E>' is a type, not a value` / `'<v>' is not a function` (a variant without a payload) |
| TP0200 | `'is some' needs an optional, found <T>` / `'is none' needs an optional, found <T>` / `'??' needs an optional on the left, found <T>` |
| TP0200 | `'<m>' is a method; call it` / `'<f>' is a field, not a method` / `cannot index <T>` / `dict keys must be int, str, or bool, found <T>` |
| TP0201 | `undefined name '<x>'` / `undefined type '<T>'` |
| TP0202 | `cannot assign to '<x>': it is a <let binding\|parameter\|loop variable\|constant\|function\|method>`, with help `declare it with 'var'` for a `let` binding / `cannot assign to an element of str` |
| TP0203 | `expected <T>, found <T>?` / `<T>? must be narrowed before use`, with help `narrow it with 'is some', or give a default with '??'` |
| TP0204 | `non-exhaustive match (missing: <a>, <b>)` |
| TP0205 | `condition must be bool, found <T>` |
| TP0206 | `cannot infer the type of an empty list` / `... empty dict` / `cannot infer the type of none` / `cannot infer '<T>' for '<f>'`, with help `add a type annotation` |
| TP0207 | `'<x>' shadows an outer binding` |
| TP0208 | `not all paths in '<f>' return a value` |
| TP0209 | `'<S>' takes <n> type arguments, found <m>` (`type argument` when n is 1) |
| TP0209 | `'<v>' has <n> fields, found <m> bindings` (variant patterns; `field` / `binding` when the count is 1) |
| TP0209 | `'<f>' takes <n> arguments, found <m>` (`argument` when n is 1; `1 or 2 arguments` for `range`) / `missing field '<f>' in '<S>'` / `field '<f>' is given more than once` |
| TP0210 | `<T> has no field '<x>'` / `<T> has no method '<x>'` / `<E> has no variant '<x>'` |
| TP0211 | `'<f>' cannot use top-level variable '<x>'`, with help `pass it as a parameter, or make it a const` |
| TP0213 | `optional types cannot be nested` |
| TP0214 | `duplicate definition of '<x>'` |
| TP0215 | `'break' outside a loop` / `'continue' outside a loop` / `'return' outside a function` |
| TP0216 | `'<v>' is ambiguous: it is a variant of <E1> and <E2>`, with help `qualify it, e.g. '<E1>.<v>'` |
| TP0217 | `const initializer must be a constant expression` |
| TP0218 | `keyword arguments are only allowed in constructors` |
| TP0219 | `instantiating '<f>' needs '<f>[<T>]', which is too deep`, at the call that starts the chain |
| TP0300 | `unreachable match arm` |

Types are written as in source: `int`, `str?`, `list[int]`. The type of `none` is written `none`, and a missing value is written `nothing`.

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
