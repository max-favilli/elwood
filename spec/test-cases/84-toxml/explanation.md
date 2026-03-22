# toXml — convert JSON to XML

Demonstrates `.toXml()` which serializes a JSON object into an XML string.

Conventions:
- **Single top-level key** (`catalog`) becomes the root element
- **Arrays** (`book` array) become repeated elements with the same name
- **Object properties** become child elements
- **`declaration: false`** omits the `<?xml ...?>` header

Use `toXml({ rootElement: "name" })` to force a specific root element name when the object has multiple top-level keys.