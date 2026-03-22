# parseJson — deserialize embedded JSON strings

Demonstrates `.parseJson()` which deserializes a JSON string into a navigable value.

This is essential when dealing with properties that contain serialized JSON — common in APIs, CSV exports, log files, and message queues where nested structures get stringified.

- `$.raw.parseJson()` — parses `{"name":"Alice","scores":[90,85,92]}` into an object you can navigate with `.name`, `.scores[0]`, etc.
- If the string is not valid JSON, `.parseJson()` returns `null` — so `$.plain.parseJson().isNull()` is `true` for `"hello"`.