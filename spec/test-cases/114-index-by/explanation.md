# 114 — IndexBy (lookup by key)

`| indexBy keySelector` turns a collection into an object keyed by the selector, so that a
row can be found by its key without searching the collection.

```
let historyByName = $.history[*] | indexBy h => h.fileName
let customerById = $.customers[*] | indexBy $.id
let historyGroups = $.history[*] | groupBy h => h.fileName | indexBy g => g.key

return {
  actions: $.files[*] | select f => {
    name: f.name,
    action: if historyByName[f.name] == null then "new"
            else if historyByName[f.name].size != f.size then "updated"
            else "unchanged"
  },
  firstWins: historyByName["a.jpg"].seen,
  missing: historyByName["nope.jpg"],
  orders: $.orders[*] | select o => { ref: o.ref, customer: customerById[o.customerId]?.name },
  allMatches: historyGroups["a.jpg"].items | select h => h.seen,
  index: $.customers[*] | indexBy c => c.name
}
```

## Why

The usual way to find a row by key is `list | first x => x.key == value`. Inside a `select`
or `where` over another collection that search runs once per row, and each run walks the
list: rows × list size. With an index the list is walked once to build it, and each row
costs one lookup: rows + list size.

```
let find = memo name => $.history[*] | first h => h.fileName == name     // scans per call
let historyByName = $.history[*] | indexBy h => h.fileName               // built once
```

## Rules

- **Read it with `index[key]`.** A key with no entry gives `null`.
- **The first row with a key wins**, so `index[k]` is the row `first x => x.key == k` finds.
  `historyByName["a.jpg"]` is the entry seen first, not the later one.
- **Keys are text.** A number or boolean key is read by its text, so `customerById[o.customerId]`
  works with numeric ids. It also means the number `7` and the string `"7"` are the same key,
  which `==` would keep apart.
- **A row whose key is `null` has no entry**, and looking up `null` finds nothing. This is
  the one place it differs from `first x => x.key == null`, which would match such a row.
- **For every match rather than the first**, group and then index the groups:
  `list | groupBy x => x.key | indexBy g => g.key`, then `index[k].items`.

An index is an ordinary object, so it can also be returned as output, as `index` is here.
