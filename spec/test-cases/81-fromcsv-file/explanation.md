# fromCsv — real CSV file input

Demonstrates using an actual `.csv` file as test input instead of JSON-wrapped CSV strings.

When the input file is `input.csv` (instead of `input.json`), the test runner reads the file as a raw string and passes it as `$`. The script can then call `.fromCsv()` directly on `$` without needing a JSON wrapper.

This is the most natural way to test CSV parsing — the input file is a real CSV file that you can open in Excel or any text editor.