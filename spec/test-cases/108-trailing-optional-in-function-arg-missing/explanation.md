# 108 — Trailing `?` on missing property in function argument

`obj.b?` returns null when `b` doesn't exist, even inside a function call.
