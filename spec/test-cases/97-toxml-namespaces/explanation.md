# 97 — toXml with XML namespaces

Verifies that `toXml()` correctly emits namespace declarations and prefixed attributes.

- `["@xmlns:xsi"]` becomes the `xmlns:xsi="..."` namespace declaration attribute
- `["@xsi:noNamespaceSchemaLocation"]` becomes the prefixed attribute `xsi:noNamespaceSchemaLocation="..."`
- Regular `@`-prefixed properties become plain attributes (`CompanyName="..."`)
- Array values (`Order`) become repeated child elements

Computed property keys (`[expr]`) are used because `:` in property names requires bracket notation.
