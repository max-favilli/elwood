# iPaaS Transformation Landscape — How Elwood Compares

## The Problem

Every integration platform needs to transform data. JSON comes in one shape, it needs to go out in another. The question is: **how do you express that transformation?**

The industry has converged on two approaches — and both have the same fundamental weakness.

---

## Approach 1: Visual Mapping + Function Library

Drag lines between source and target fields. Insert canned functions (uppercase, date format, math) into the mapping lines. When that's not enough, escape to a general-purpose scripting language.

### Boomi

**How it works:** The Map component lets you visually connect source fields to destination fields. For anything beyond 1:1 copying, you chain **Map Functions** — built-in operations like string uppercase, date formatting, math, or database lookups. For complex logic, you build **User-Defined Functions** from chains of standard functions.

**Escape hatch:** When the function library isn't enough, Boomi offers **Groovy** (2.4.x, full Java access) or **JavaScript** (ES 5.1 — not modern JS). A separate Data Process shape gives raw access to document bytes.

**Limitations:**
- No real DSL — purely visual mapping + function library + escape-hatch scripting
- No textual representation of a mapping you can meaningfully version-control or diff
- JavaScript support stuck at ECMAScript 5.1
- Complex nested function chains are hard to read and debug in the visual UI
- Custom scripting can't reference other Boomi components

### Informatica IICS

**How it works:** The Mapping Designer provides a visual pipeline of **Transformations**. The workhorse is the **Expression Transformation** which uses Informatica's proprietary SQL-like language: `IIF(SALARY > 50000, SALARY * 1.1, SALARY * 1.2)` or `CONCAT(FIRST_NAME, ' ', LAST_NAME)`. 100+ built-in functions for string, date, math, conditional logic, and lookups.

**Escape hatch:** None within mappings — you must work within the transformation language. No general-purpose scripting inside a mapping.

**Limitations:**
- The expression language is proprietary — looks like SQL but isn't SQL, creating a learning curve and non-transferable skills
- No scripting escape hatch when you hit the ceiling
- Visual designer gets unwieldy for complex multi-step transformations
- Historically rooted in relational/tabular data; hierarchical JSON/XML transformations are more recent and less mature

### SAP Cloud Integration (SAP CPI)

**How it works:** Three distinct options, all embedded as steps in an integration flow:

1. **Graphical Message Mapping** — visual drag-and-drop for XML-to-XML only. Connect source fields to target fields, insert standard functions or custom Java-like UDFs. Uses SAP's proprietary "queue and context" concept for handling cardinality differences.
2. **XSLT Mapping** — full XSLT stylesheets. Most powerful for XML structural changes but steep learning curve and verbose.
3. **Groovy/JavaScript Scripting** — full programmatic control with access to the exchange object (message body, headers, properties).

**Limitations:**
- Graphical mapping only supports XML-to-XML — for JSON, you must use Groovy or convert to XML first
- The queue-and-context concept is notoriously difficult to understand
- No single expression language across all three modes — each is its own world
- XSLT is verbose for simple transformations

---

## Approach 2: Restricted Subset of a General-Purpose Language

Take a real programming language (JavaScript, Ruby), restrict it to expressions only, and use it as an inline mapping language. You get familiar syntax but not the full language power.

### SnapLogic

**How it works:** The Mapper Snap lets you write expressions for each output field using a **JavaScript-subset expression language**. It supports expressions only — no `if` blocks, no `function()` declarations, no imperative control flow. You're limited to arrow functions (`(x, y) => x + y`) and ternary operators. JSONPath syntax is used for source paths.

**Escape hatch:** Expression Libraries (reusable function collections). No full scripting within a Mapper.

**Limitations:**
- Expressions-only JavaScript creates a ceiling when you need imperative logic
- JSONPath and the expression language syntax are not fully compatible
- Mappings are embedded in pipelines, not reusable standalone artifacts
- Transformation rules inside a Mapper are not easily exported or version-controlled as text

### Workato

**How it works:** Within each recipe step's input fields, toggle **Formula Mode** to write expressions using **Ruby syntax** — specifically, method chaining on data references: `datapill.upcase.strip` or `datapill.to_f * 1.1`. Ternary operators for conditionals, safe navigation (`&.`) for nil handling.

**The catch:** This is **not full Ruby**. Workato maintains an **allowlist** of approved Ruby methods. If a method isn't on the list, it doesn't work. You must contact your Customer Success Manager to request new methods.

**Escape hatch:** Custom Code Connector for full Ruby or Python scripts as a separate step.

**Limitations:**
- Allowlisted Ruby — you get Ruby syntax without Ruby power, and you're blocked when you hit an unlisted method
- No standalone mapping artifact — transformations are scattered across recipe steps
- "Working with and formatting data can sometimes be quite fiddly and cumbersome" (user reviews)
- No visual schema-to-schema mapping view

---

## Approach 3: Purpose-Built Transformation DSL

Design a language specifically for data transformation. Not a visual mapper, not a restricted subset of an existing language, but a dedicated DSL that is textual, version-controllable, and powerful enough for complex structural transformations.

### MuleSoft DataWeave

**How it works:** DataWeave is MuleSoft's dedicated functional transformation language. It is the only purpose-built transformation DSL among major iPaaS platforms. It handles JSON, XML, CSV, and other formats natively. It has its own syntax for navigation, mapping, filtering, and structural transformation.

**Separation from orchestration:** DataWeave handles transformation. Mule flow XML handles orchestration (routing, fan-out, error handling). They are distinct languages with distinct concerns.

**Limitations:**
- Tied to MuleSoft — DataWeave only runs within the MuleSoft runtime
- Commercial license
- Significant learning curve (its own unique syntax)
- JVM only

### Elwood

**How it works:** Elwood is a functional JSON transformation language combining JSONPath navigation, KQL-style pipes, and LINQ-style lambdas. It is textual, version-controllable, and runs independently of any integration platform.

```
$.orders
| where(o => o.total > 100 and o.status == "shipped")
| orderBy(.total)
| select(o => {
    id: o.id,
    customer: o.customer.name.toUpper(),
    total: o.total,
    tag: `order-{o.id}`
  })
```

**Separation from orchestration:** Elwood expressions handle transformation. Pipeline YAML handles orchestration (sources, fan-out, destinations). They are separate concerns following the same principle as MuleSoft.

**Advantages over DataWeave:**

| | DataWeave | Elwood |
|---|---|---|
| Platform | MuleSoft only | Standalone, open-source |
| License | Commercial | MIT |
| Runtimes | JVM only | .NET, TypeScript, Node.js, browser |
| Syntax inspiration | Own syntax | JSONPath + KQL + LINQ (familiar to .NET/data developers) |
| Try before install | No | Browser playground |
| Version-controllable | Yes | Yes |
| Extensible | Within MuleSoft | Open — embed in any application |

---

## Summary Comparison

| Platform | Transformation Approach | Text-based & Diffable? | Standalone? | Scripting Escape Hatch |
|---|---|---|---|---|
| **Boomi** | Visual mapping + function chains | No | Map components are reusable | Groovy, JS (ES 5.1) |
| **Informatica** | Visual mapping + SQL-like expressions | Partially (expressions are text) | Mappings are standalone artifacts | None within mappings |
| **SAP CPI** | Visual XML mapping, XSLT, or Groovy | XSLT and Groovy are text | Steps within iFlow | Groovy (full JVM) |
| **SnapLogic** | Mapper with JS-subset expressions | Partially (expressions are text) | Embedded in pipeline | Expression Libraries |
| **Workato** | Inline allowlisted Ruby formulas | Partially (formulas are text) | No — inline in recipe steps | Custom Code Connector |
| **MuleSoft** | DataWeave (purpose-built DSL) | Yes | Within MuleSoft runtime | Not needed — DSL is powerful enough |
| **Elwood** | Purpose-built DSL (pipes + lambdas) | Yes | Fully standalone, MIT | Not needed — DSL is powerful enough |

---

## The Gap Elwood Fills

```
                    Transformation Power
                    ─────────────────────────────→
                    Low                                    High

Visual / Low-code │ Boomi maps        Informatica         │
                  │ SAP graphical     SnapLogic Mapper     │
                  │ Workato formulas                       │
                  │                                        │
Purpose-built DSL │                   DataWeave (MuleSoft) │
                  │                   Elwood  ←  ★         │
                  │                                        │
Full scripting    │                   Groovy / Python      │
                  │                   (escape hatch)       │
```

Between "visual mapping that hits a wall with complexity" and "escape to Groovy/Python" there is a gap. Only two tools fill it: MuleSoft's DataWeave (commercial, platform-locked) and Elwood (open-source, standalone, cross-platform).

Elwood's thesis: **data transformations deserve a purpose-built language that is textual, version-controllable, powerful enough for complex structural changes, and not locked to any vendor or platform.**
