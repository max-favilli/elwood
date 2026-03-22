# fromCsv — skipRows and no headers

Demonstrates two `fromCsv` options working together:

- **`skipRows: 1`** — skips the first row (a title/metadata row that isn't part of the CSV data)
- **`headers: false`** — treats all remaining rows as data (no header row), generating alphabetic column names: A, B, C, etc.

This pattern is common when parsing CSV exports that have a title or report name in the first row(s) before the actual data begins. The auto-generated column names (A, B, C, ... Z, AA, AB) match Excel's column naming convention.