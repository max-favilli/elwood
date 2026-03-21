# 55 — String Search: contains, startsWith, endsWith

## Expression
```
{
  xlsx: $.files[*] | where f => f.name.endsWith(".xlsx") | select f => f.name,
  reports: $.files[*] | where f => f.name.startsWith("report") | select f => f.name,
  finance: $.files[*] | where f => f.path.contains("finance") | select f => f.name
}
```

## Traditional JSONPath equivalents
```
$.files[*].where($.name.endsWith('.xlsx')).select($.name)
$.files[*].where($.name.startsWith('report')).select($.name)
$.files[*].where($.path.contains('finance')).select($.name)
```

## Explanation
Three string search methods, all **case-insensitive** by default:

| Method | Checks | Example |
|---|---|---|
| `.contains(str)` | String contains substring | `"hello world".contains("world")` → `true` |
| `.startsWith(str)` | String starts with prefix | `"report-Q1".startsWith("report")` → `true` |
| `.endsWith(str)` | String ends with suffix | `"file.xlsx".endsWith(".xlsx")` → `true` |

### Use in filtering
These methods return booleans, so they work naturally in `where` predicates:
```
$.items[*] | where i => i.name.contains("test")
$.items[*] | where i => !i.name.startsWith("draft")
```

### Case sensitivity
All three are **case-insensitive** in Elwood (matching traditional behavior):
```
"Hello".contains("hello")      → true
"Report.xlsx".endsWith(".XLSX") → true
```
