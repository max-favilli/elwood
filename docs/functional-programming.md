# Why Functional Programming?

Elwood is a **functional, expression-oriented, domain-specific language** for JSON transformation. This is a deliberate design choice — functional programming is the natural fit for data transformation.

## What "functional" means in Elwood

### Everything is an expression

There are no statements in Elwood — no `for` loops, no `while`, no variable assignment. Every piece of code produces a value:

```
// This is an expression that produces a value
$.users | where(.age > 25) | select(.name)

// Even scripts with let bindings are expressions
let adults = $.users | where(.age >= 18)
let count = adults | count
return { names: adults | select(.name), total: count }
```

### Pure transformations — no side effects

An Elwood expression takes input and produces output. It doesn't modify the input, write to a database, send a network request, or change any external state. Same input + same expression = same output, every time.

This makes Elwood expressions:
- **Predictable** — easy to understand, test, and debug
- **Composable** — combine simple expressions into complex pipelines
- **Parallelizable** — safe to run concurrently without locks or synchronization

### First-class functions (lambdas)

Functions are values that you pass to operators:

```
// Named lambda
$.users | where(u => u.active and u.age > 25)

// Implicit context (shorthand)
$.users | where(.active) | select(.name)

// Two-argument lambda for reduce
$.prices | reduce((total, price) => total + price) from 0
```

### Higher-order functions

Most pipe operators are higher-order functions — they take a function as an argument:

```
where(predicate)     — keep items where predicate returns true
select(transform)    — apply transform to each item
orderBy(keySelector) — sort by the value keySelector returns
reduce(accumulator)  — fold all items using accumulator function
```

This is the same concept as `.filter()`, `.map()`, `.sort()`, and `.reduce()` in JavaScript, or LINQ's `Where()`, `Select()`, `OrderBy()`, and `Aggregate()` in C#.

### Immutability — no mutation

There is no way to modify a value in Elwood. `let` creates a binding — it gives a name to a value. It does not create a variable you can change later:

```
let x = 10
// There is no way to do: x = 20 (this is not valid Elwood)
// x is always 10 within this scope
```

When you "add a property" to an object, you create a new object:

```
let original = { name: "Alice", age: 30 }
let updated = { ...original, email: "alice@example.com" }
// original is unchanged — updated is a new object with all three properties
```

### Pipeline composition (pipes)

Data flows left-to-right through a chain of transformations:

```
$.orders
| where(o => o.status == "shipped")        // filter
| select(o => { id: o.id, total: o.total }) // project
| orderBy(.total) desc                      // sort
| take(10)                                  // limit
```

Each `|` passes the output of one step as the input to the next. This is function composition — `f(g(h(x)))` written as `x | h | g | f` — but readable.

### Pattern matching

Instead of `if/else` chains, Elwood supports pattern matching:

```
$.status | match
  "active"  => "green",
  "pending" => "yellow",
  "retired" => "red",
  _         => "gray"
```

### No loops — operators instead

There are no `for` or `while` loops. Instead, pipe operators express iteration declaratively:

| Instead of... | Use... |
|---|---|
| `for` loop that filters | `where(predicate)` |
| `for` loop that transforms | `select(transform)` |
| `for` loop that accumulates | `reduce(accumulator)` |
| `for` loop that flattens | `selectMany(selector)` |
| `for` loop that groups | `groupBy(key)` |
| `for` loop that finds | `first(predicate)` |
| `for` loop that checks | `any(predicate)` / `all(predicate)` |

For iterative computation where each step depends on the previous result, Elwood provides `iterate`:

```
// iterate(seed, function) → lazy sequence
// Each value is produced by applying the function to the previous value

// Double until we exceed 1000
iterate(1, x => x * 2) | takeWhile(x => x < 1000)
// → [1, 2, 4, 8, 16, 32, 64, 128, 256, 512]

// Generate 5 items with computed properties
iterate({ n: 1, total: 0 }, s => { n: s.n + 1, total: s.total + s.n })
| take(5)
// → [{ n: 1, total: 0 }, { n: 2, total: 1 }, { n: 3, total: 3 }, { n: 4, total: 6 }, { n: 5, total: 10 }]
```

The sequence is lazy — `iterate` produces values on demand, and `takeWhile` or `take` stops when done. No infinite loop risk, no mutation, no counter variable.

This is not a limitation — it's a design choice. Every transformation you could write with a loop can be expressed with these operators, and the result is shorter, more readable, and less error-prone.

---

## Pure functions and pragmatic exceptions

### What is a pure function?

A **pure function** always returns the same output for the same input, and has no side effects:

```
// Pure — same input always gives the same output
$.price * 1.1
"hello".toUpper()
$.items | where(.active) | count
```

Pure functions are:
- **Testable** — you can write a test with a fixed input and expected output
- **Cacheable** — if you've seen this input before, reuse the result (this is what `memo` does)
- **Parallelizable** — safe to run concurrently without locks

### Why purity matters for data transformation

In a transformation pipeline, purity means you can reason about each step independently:

```
$.orders
| where(.total > 100)        // pure — depends only on .total
| select(.customer.name)     // pure — depends only on .customer.name
| distinct                   // pure — depends only on the input array
```

You can test each step in isolation, reorder steps (where the result is logically equivalent), and trust that no step affects another. This is what makes pipe chains reliable.

### Elwood's pragmatic exceptions

Elwood is **functional by default, pragmatically impure when the domain requires it.** These functions are impure — they return different values each time they're called:

| Function | Why it's impure | Why it's necessary |
|---|---|---|
| `now()` | Returns the current datetime | Transformations often need timestamps |
| `utcNow()` | Returns the current UTC datetime | Same |
| `newGuid()` | Returns a new random GUID | Generating unique IDs is a common need |

These functions are clearly identified and isolated:
- They cannot be tested with conformance test cases (since the output is non-deterministic)
- They are tested with property-based tests that verify format and type, not exact values
- They don't affect the purity of the rest of the language — a pipeline without these functions is fully deterministic

This is the same approach taken by F#, Clojure, Elixir, Scala, and DataWeave — languages that are functional in design but pragmatic in practice. Haskell is the only major functional language that enforces strict purity (through monads), but that level of rigor is unnecessary for a data transformation DSL.

---

## Why functional programming fits data transformation

Data transformation is inherently functional: **input → transform → output**. There is no mutable state to manage, no external systems to interact with, no order-dependent side effects. The entire job is to take data in one shape and produce data in another shape.

Imperative approaches (loops, mutation, variables) introduce complexity that doesn't help:

```javascript
// Imperative (JavaScript)
const result = [];
for (let i = 0; i < users.length; i++) {
  if (users[i].age > 25 && users[i].active) {
    result.push({
      name: users[i].name,
      email: users[i].email.toLowerCase()
    });
  }
}
result.sort((a, b) => a.name.localeCompare(b.name));
```

```
// Functional (Elwood)
$.users
| where(u => u.age > 25 and u.active)
| select(u => { name: u.name, email: u.email.toLower() })
| orderBy(.name)
```

The Elwood version has:
- No temporary variables
- No index management
- No mutation (`.push()`)
- No imperative control flow
- Clear left-to-right data flow

Both produce the same result. The functional version is shorter, easier to read, and harder to get wrong.

---

## Comparison with other functional transformation languages

Elwood is not the first functional transformation language, but it draws from the best ideas:

| Language | Domain | Functional style |
|---|---|---|
| **jq** | JSON transformation (CLI) | Purely functional, pipes, filters |
| **DataWeave** | MuleSoft data integration | Functional, pattern matching, type-aware |
| **LINQ** | .NET collections | Functional operators over sequences |
| **XQuery/XSLT** | XML transformation | Functional (XQuery), declarative (XSLT) |
| **Elwood** | JSON transformation (cross-platform) | Functional, pipes, lambdas, pattern matching |

Elwood's syntax is designed to be familiar to developers who know LINQ (C#) or array methods (JavaScript). If you've used `.filter()`, `.map()`, `.reduce()`, you already know the concepts — Elwood just gives them a cleaner syntax for JSON transformation.
