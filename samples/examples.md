# Elwood Examples

All examples use `samples/sample-data.json` as input.

Run any example with:
```bash
dotnet run --project src/Elwood.Cli -- eval "<expression>" --input samples/sample-data.json
```
Or in the REPL:
```bash
dotnet run --project src/Elwood.Cli
elwood> :load samples/sample-data.json
elwood> <expression>
```

---

## 1. Basic Navigation

```
$.company
```
→ `"Acme Corp"`

```
$.systems[0].name
```
→ `"SAP ERP"`

```
$.systems[*] | count
```
→ `8`

---

## 2. Filtering

**Active systems only:**
```
$.systems[*] | where s => s.status == "active"
```
→ 6 items (excludes "Legacy CMS" and "Old Warehouse")

**Active cloud systems:**
```
$.systems[*] | where s => s.status == "active" && s.type == "cloud"
```
→ 5 items

---

## 3. Projection

**Extract just names:**
```
$.systems[*] | where s => s.status == "active" | select s => s.name
```
→ `["SAP ERP", "Shopify", "Salesforce", "PLM System", "Azure Data Lake", "Elwood Middleware"]`

**Build new objects:**
```
$.systems[*] | select s => { id: s.name.toLower().replace(" ", "-"), label: s.name, active: s.status == "active" }
```
→
```json
[
  { "id": "sap-erp", "label": "SAP ERP", "active": true },
  { "id": "shopify", "label": "Shopify", "active": true },
  ...
]
```

---

## 4. Sorting

**Systems sorted by name:**
```
$.systems[*] | orderBy s => s.name asc | select s => s.name
```
→ `["Azure Data Lake", "PLM System", "Elwood Middleware", "Legacy CMS", ...]`

**Systems sorted by domain then name:**
```
$.systems[*] | orderBy s => s.domain asc, s => s.name asc | select s => { domain: s.domain, name: s.name }
```

---

## 5. Grouping

**Systems by domain:**
```
$.systems[*] | groupBy s => s.domain | select g => { domain: g.key, count: g.items | count, systems: g.items | select s => s.name }
```
→
```json
[
  { "domain": "ERP", "count": 2, "systems": ["SAP ERP", "Old Warehouse"] },
  { "domain": "E-Commerce", "count": 2, "systems": ["Shopify", "Legacy CMS"] },
  { "domain": "CRM", "count": 1, "systems": ["Salesforce"] },
  ...
]
```

---

## 6. Pattern Matching

**Color-code by domain:**
```
$.systems[*] | select s => { name: s.name, color: s.domain | match "ERP" => "#4ECDC4", "E-Commerce" => "#FF6B6B", "CRM" => "#45B7D1", _ => "#95A5A6" }
```
→
```json
[
  { "name": "SAP ERP", "color": "#4ECDC4" },
  { "name": "Shopify", "color": "#FF6B6B" },
  { "name": "Salesforce", "color": "#45B7D1" },
  { "name": "Legacy CMS", "color": "#FF6B6B" },
  ...
]
```

---

## 7. Conditionals

**Label systems as active/retired:**
```
$.systems[*] | select s => { name: s.name, label: if s.status == "active" then s.name else `{s.name} (RETIRED)` }
```
→
```json
[
  { "name": "SAP ERP", "label": "SAP ERP" },
  ...
  { "name": "Legacy CMS", "label": "Legacy CMS (RETIRED)" },
  ...
]
```

---

## 8. String Methods

```
$.systems[*] | select s => s.name.toLower().replace(" ", "_")
```
→ `["sap_erp", "shopify", "salesforce", "legacy_cms", ...]`

---

## 9. Aggregation

**Distinct domains:**
```
$.systems[*] | select s => s.domain | distinct
```
→ `["ERP", "E-Commerce", "CRM", "PLM", "Data", "Integration"]`

**Unique middleware types:**
```
$.integrations[*] | select i => i.middleware | distinct
```
→ `["Elwood Middleware", "Direct"]`

---

## 10. Arithmetic

**Order totals:**
```
$.orders[*] | select o => { id: o.id, total: o.items[*] | select i => i.price * i.qty | sum }
```

---

## 11. Take / Skip / Batch

**First 3 systems:**
```
$.systems[*] | take 3 | select s => s.name
```
→ `["SAP ERP", "Shopify", "Salesforce"]`

**Batch in groups of 3:**
```
$.systems[*] | select s => s.name | batch 3
```
→ `[["SAP ERP", "Shopify", "Salesforce"], ["Legacy CMS", "PLM System", "Azure Data Lake"], ["Old Warehouse", "Elwood Middleware"]]`

---

## 12. Scripts (let bindings + return)

Save as `samples/landscape.elwood`:

```
let activeSystems = $.systems[*] | where s => s.status == "active"

let clusters = activeSystems
  | groupBy s => s.domain
  | select g => {
      domain: g.key,
      systemCount: g.items | count,
      systems: g.items | select s => s.name
    }

let middlewareIntegrations = $.integrations[*]
  | where i => i.middleware == "Elwood Middleware"

return {
  company: $.company,
  totalActive: activeSystems | count,
  clusters: clusters,
  middlewareIntegrationCount: middlewareIntegrations | count,
  frequencies: middlewareIntegrations | select i => i.frequency | distinct
}
```

Run with:
```bash
dotnet run --project src/Elwood.Cli -- run samples/landscape.elwood --input samples/sample-data.json
```

Expected output:
```json
{
  "company": "Acme Corp",
  "totalActive": 6,
  "clusters": [
    { "domain": "ERP", "systemCount": 1, "systems": ["SAP ERP"] },
    { "domain": "E-Commerce", "systemCount": 1, "systems": ["Shopify"] },
    { "domain": "CRM", "systemCount": 1, "systems": ["Salesforce"] },
    { "domain": "PLM", "systemCount": 1, "systems": ["PLM System"] },
    { "domain": "Data", "systemCount": 1, "systems": ["Azure Data Lake"] },
    { "domain": "Integration", "systemCount": 1, "systems": ["Elwood Middleware"] }
  ],
  "middlewareIntegrationCount": 5,
  "frequencies": ["hourly", "daily", "on-demand", "real-time", "nightly"]
}
```

---

## 13. Error Reporting

**Typo in property name:**
```
$.systems[*] | select s => s.staus
```
→ `Error at line 1, col 32: Property 'staus' not found on Object. Did you mean 'status'? Available: name, domain, status, owner, type`

**Unknown pipe operator:**
```
$.systems[*] | flatten
```
→ `Error: Unknown pipe operator 'flatten'`
