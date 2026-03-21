# 56 — GraphQL Query (String Building)

## Expression
```
"query { products(first: " + $.pageSize.toString() + ") { edges { node { id } } } }"
```

## Traditional JSONPath equivalent
```
$.graphqlquery('query { products(first: 10) { edges { node { id } } } }')
```

## Explanation
Traditional JSONPath's `.graphqlquery()` was a pass-through function — it just returned its string argument as-is. It existed to embed GraphQL query strings in expressions.

In Elwood, you build dynamic strings with **string concatenation** (`+`) or **interpolation** (backticks):

### String concatenation
```
"query { products(first: " + $.pageSize.toString() + ") { ... } }"
```

### String interpolation (for strings without curly braces)
```
`Hello {$.name}, your order is {$.orderId}`
```

Note: backtick interpolation uses `{expr}` syntax, which conflicts with GraphQL's curly braces. For GraphQL queries, string concatenation with `+` is the cleaner approach.

### Traditional approach
```
$.graphqlquery('{ products(first: 10) { edges { node { id } } } }')
```
The function was just a pass-through — it returned the string argument unchanged. No dynamic injection was possible inside the function call.

### When to use
- **String concatenation** (`+`) — when the template contains `{` `}` characters (GraphQL, JSON templates)
- **Interpolation** (`` ` ``) — for simple templates without curly braces (URLs, messages, filenames)
