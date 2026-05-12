# 98 — Multi-argument memo functions

Verifies that `memo (x, y) => expr` works with multiple parameters.

The cache key is built from all arguments, so `lookup(1, 20)` and `lookup(1, 10)` are
cached independently. This enables hoisting complex lookups that depend on multiple
loop variables to the top of a script.
