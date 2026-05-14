# 106 — `| first` followed by `let` binding

Verifies that `| first` (and other optional-arg pipe operators) correctly
terminate before a `let` keyword, rather than consuming `let` as a predicate.
