# fromXml — parse XML to JSON

Demonstrates `.fromXml()` which parses an XML string into a navigable JSON structure.

Conventions:
- **Repeated elements** (two `<book>` elements) automatically become an array
- **Simple leaf elements** (`<id>`, `<title>`, `<price>`) become string values
- **Nested structure** is preserved: `catalog.book` navigates the hierarchy
- **Namespaces** are stripped by default
- **Attributes** become `@attr` properties (configurable via `attributePrefix` option)

The script parses a catalog XML, then uses a pipe to extract and transform each book's data.