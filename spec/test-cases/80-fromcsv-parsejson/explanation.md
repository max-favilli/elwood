# fromCsv — parseJson option

Demonstrates the `parseJson: true` option on `fromCsv()`, which automatically detects and deserializes JSON values embedded in CSV cells.

The CSV contains a `metadata` column where each cell holds a JSON object (properly quoted per CSV rules). With `parseJson: true`, these cells are parsed into navigable objects instead of remaining as raw strings.

This is useful when:
- API responses are exported to CSV with nested data serialized as JSON strings
- Log files embed structured data in CSV columns
- Data pipelines pass complex objects through CSV transport

Without `parseJson`, `r.metadata` would be the raw string `{"role":"admin","level":3}`. With it, you can navigate directly: `r.metadata.role`.