# 91 — $root binding vs $ path resolution

Verifies that when a `$root` binding is passed alongside a different input value:
- `$` (rooted paths like `$.name`) resolves to the **input** (the slice)
- `$root` (identifier) resolves to the **binding** (the full document)
- Other bindings like `$source` are also accessible

This is the Eagle use case: the input is a sliced portion of the IDM, while `$root` gives
the script access to the full unsliced document.
