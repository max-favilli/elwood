# toCsv — alwaysQuote

Demonstrates the `alwaysQuote` option which wraps every field in double quotes, regardless of whether the value contains special characters.

This is useful when generating CSV files for systems that expect RFC 4180 strict quoting, or when downstream consumers treat unquoted and quoted fields differently.