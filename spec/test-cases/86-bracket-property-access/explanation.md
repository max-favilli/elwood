# Bracket property access

Demonstrates `obj["propertyName"]` syntax for accessing properties with names that aren't valid identifiers.

This is essential for working with XML attributes parsed by `fromXml()`, which creates `@`-prefixed property names like `@id` and `@lang`. Since `@` isn't a valid identifier character, `b.@id` won't work — but `b["@id"]` does.

Also useful for:
- Properties with spaces, hyphens, or other special characters
- Dynamic property access with computed keys: `obj[variableName]`