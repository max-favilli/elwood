# fromXml — real XML file input

Demonstrates using an actual `.xml` file as test input. The test runner reads the file as a raw string and passes it as `$`.

The script parses an orders XML document, filters for orders with quantity > 1, and extracts the id and customer fields. Combines XML parsing with Elwood's pipe operators for a realistic data processing pipeline.