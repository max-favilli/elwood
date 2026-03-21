# 29 — SelectMany (Flatten)

## Script
```
let allTeams = $.departments[*] | selectMany d => d.teams
let allMembers = allTeams | selectMany t => t.members

return {
  teamLeads: allTeams | select t => t.lead,
  allMembers: allMembers,
  totalPeople: allMembers | count
}
```

## Traditional JSONPath equivalent
```
$.departments[*].selectmany($.teams)              → all teams flattened
$.departments[*].selectmany($.teams).selectmany($.members)   → all members flattened
```

## Explanation
`selectMany` is like `select`, but it **flattens** the results. If the projection returns an array, its elements are merged into the output instead of being nested.

### select vs selectMany
```
// select — preserves nesting:
$.departments[*] | select d => d.teams
→ [ [team1, team2], [team3] ]           ← array of arrays

// selectMany — flattens:
$.departments[*] | selectMany d => d.teams
→ [ team1, team2, team3 ]               ← flat array
```

### Step by step
1. `$.departments[*]` — 2 departments
2. `| selectMany d => d.teams` — for each department, get its teams array and flatten:
   - Engineering has 2 teams → contributes 2
   - Marketing has 1 team → contributes 1
   - Result: 3 teams in a flat array
3. `| selectMany t => t.members` — for each team, get its members and flatten:
   - Alice's team: ["Bob", "Charlie"] → 2
   - Diana's team: ["Eve"] → 1
   - Frank's team: ["Grace", "Heidi", "Ivan"] → 3
   - Result: 6 members in a flat array

### Use cases
- Flattening nested arrays (orders → line items, departments → employees)
- Collecting all items from sub-collections into one list
- Any time `select` gives you an array-of-arrays but you want a flat array

### LINQ equivalent
```csharp
departments.SelectMany(d => d.Teams)           // C# LINQ
departments |> Seq.collect (fun d -> d.Teams)   // F#
```
