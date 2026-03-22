# fromText — real text file input

Demonstrates using an actual `.txt` file as test input. The test runner reads the file as a raw string and passes it as `$`.

This example parses a log file, filters for ERROR lines, and extracts the message after the log level prefix. A realistic pattern for processing log exports, configuration files, or any line-oriented text data.