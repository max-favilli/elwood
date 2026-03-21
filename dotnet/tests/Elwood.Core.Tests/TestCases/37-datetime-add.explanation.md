# 37 — DateTime Add

## Expression
```
{
  original: $.order.createdAt,
  plusOneDay: $.order.createdAt.add("1.00:00:00"),
  plusProcessing: $.order.createdAt.add($.order.processingTime),
  plus30min: $.order.createdAt.add("00:30:00")
}
```

## Traditional JSONPath equivalent
```
$.toobject({
  original: $.order.createdAt,
  plusOneDay: $.order.createdAt.add(1.00:00:00),
  plusProcessing: $.order.createdAt.add($.order.processingTime),
  plus30min: $.order.createdAt.add(00:30:00)
})
```

## Explanation
- `.add(timespan)` — adds a TimeSpan to a datetime string and returns the result
- The timespan format is `days.hours:minutes:seconds`:
  - `"1.00:00:00"` — 1 day
  - `"00:30:00"` — 30 minutes
  - `"1.06:30:00"` — 1 day, 6 hours, 30 minutes

### TimeSpan format
```
days.hours:minutes:seconds
```

| TimeSpan | Meaning |
|---|---|
| `"00:30:00"` | 30 minutes |
| `"02:00:00"` | 2 hours |
| `"1.00:00:00"` | 1 day |
| `"7.00:00:00"` | 7 days |
| `"1.06:30:00"` | 1 day, 6 hours, 30 minutes |

### Dynamic timespans
The timespan can come from the input data:
```
$.order.createdAt.add($.order.processingTime)
```
Here `processingTime` is `"1.06:30:00"` from the JSON — so the due date is computed dynamically.

### Numeric fallback
`.add()` also works on numbers as simple addition:
```
$.price.add($.tax)    → numeric addition
```
