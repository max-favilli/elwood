# 52 — Clone, Keep, Remove

## Expression
```
{
  cloned: $[0].clone(),
  kept: $[*] | skip 1 | take 2 | select o => o.keep("id", "email"),
  removed: $[*] | skip 1 | take 2 | select o => o.remove("profile", "email")
}
```

## Traditional JSONPath equivalents
```
$[0].clone()
$[1:3].keep(id,email)
$[1:3].remove(profile,email)
```

## Explanation

### .clone()
Deep copies an object. In Elwood, values are generally immutable (new objects are created by transformations), but `.clone()` is available for explicit deep copying when needed.

### .keep(properties...)
Keeps **only** the named properties, removes everything else:
```
o.keep("id", "email")
// { id, email, username, profile } → { id, email }
```

### .remove(properties...)
Removes the named properties, keeps everything else:
```
o.remove("profile", "email")
// { id, email, username, profile } → { id, username }
```

### Slice syntax: $[1:3] vs skip/take
traditional JSONPath supports JSONPath slice `$[1:3]` (indices 1 inclusive to 3 exclusive). In Elwood, use pipe operators:
```
$[*] | skip 1 | take 2     // equivalent to $[1:3]
```

### keep vs remove vs spread
| Need | Method |
|---|---|
| Keep only specific properties | `.keep("id", "email")` |
| Remove specific properties | `.remove("profile", "email")` |
| Add/override properties | `{ ...o, newProp: value }` |
| Full reshape | `\| select o => { id: o.id, ... }` |

### Traditional JSONPath comparison
| traditional JSONPath | Elwood |
|---|---|
| `$[0].clone()` | `$[0].clone()` |
| `$[1:3].keep(id,email)` | `$[*] \| skip 1 \| take 2 \| select o => o.keep("id", "email")` |
| `$[1:3].remove(profile,email)` | `$[*] \| skip 1 \| take 2 \| select o => o.remove("profile", "email")` |

Note: Traditional JSONPath's `.keep()` and `.remove()` use unquoted property names. Elwood requires quoted strings to avoid ambiguity with variables.
