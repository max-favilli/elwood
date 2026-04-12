# Getting Started with Elwood

This guide covers running the Elwood Portal and Runtime API locally for pipeline development and testing.

---

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Node.js 20+](https://nodejs.org/)
- Git

## Repositories

| Repo | What | Clone |
|---|---|---|
| **elwood** | Runtime engine, API, pipeline library | `git clone https://github.com/max-favilli/elwood.git` |
| **elwood-portal** | Web management UI | `git clone https://github.com/max-favilli/elwood-portal.git` |

Clone both side by side:
```
your-workspace/
├── Elwood/           (or elwood/)
└── elwood-portal/
```

---

## Quick Start

### 1. Install portal dependencies (one-time)

```bash
cd elwood-portal
npm install
```

### 2. Start the Runtime API + Portal

```powershell
cd elwood-portal
.\start-dev.ps1
```

This starts:
- **Runtime API** at http://localhost:5000
- **Portal** at http://localhost:3000

Open http://localhost:3000 in your browser.

### 3. Create your first pipeline

1. Click **Pipelines** in the sidebar
2. Click **New Pipeline**
3. Enter a pipeline ID (e.g., `my-first-pipeline`)
4. Edit the YAML and add `.elwood` scripts in the file tree
5. Click **Save**, then **Validate**

### 4. Test scripts in the Playground

Click **Playground** in the sidebar. Write Elwood expressions, provide JSON input, press **Ctrl+Enter** to evaluate. Browse the 86 built-in examples for learning.

### 5. Test the full pipeline via HTTP trigger

```bash
curl -X POST http://localhost:5000/api/v1/trigger/your/endpoint \
  -H "Content-Type: application/json" \
  -d '{"your": "payload"}'
```

The trigger endpoint matches against the `endpoint:` field in your pipeline YAML.

---

## Configuration

### Secrets (pipeline-level)

Pipelines reference secrets via `${KEY}` or `$secrets.path` in YAML. These are resolved from:

1. **`secrets.json`** (local file, next to the API) — for development
2. **Azure App Configuration** — for production / shared environments
3. **Environment variables** (`ELWOOD_SECRET_` prefix) — fallback

Create `secrets.json` in the API directory (`Elwood/dotnet/src/Elwood.Runtime.Api/`):

```json
{
  "MY-API-URL": "https://api.example.com",
  "MY-API-KEY": "secret-value"
}
```

This file is gitignored. Keys can use any format (dashes, dots, camelCase).

### Azure App Configuration (optional)

To connect to Azure App Configuration instead of (or in addition to) `secrets.json`:

Create `appsettings.Development.json` in the API directory:

```json
{
  "Elwood": {
    "AppConfiguration": "Endpoint=https://your-config.azconfig.io;Id=...;Secret=...",
    "AppConfigurationLabel": "dev"
  }
}
```

The label selects the environment. Same App Configuration store, different values per label.

Resolution priority: `secrets.json` → App Configuration → environment variables.

### Application Insights (optional)

Add to `appsettings.Development.json`:

```json
{
  "APPLICATIONINSIGHTS_CONNECTION_STRING": "InstrumentationKey=...;IngestionEndpoint=..."
}
```

All pipeline executions are logged with `ExecutionId` as a custom dimension. Query in Application Insights: `customDimensions.ExecutionId == "your-id"` to see everything that happened in one execution.

---

## Pipeline YAML Structure

```yaml
version: 2
name: my-pipeline
description: What this pipeline does
mode: sync                          # sync (returns response) or async (queue-based)
ttlSeconds: 259200                  # data retention (3 days)

sources:
  - name: trigger
    trigger: http
    endpoint: /my/endpoint          # URL path for HTTP trigger
    contentType: json
    map: trigger-map.elwood         # transform incoming data → IDM
    auth:                           # optional — basic auth for callers
      type: basic
      user: my-user
      password: $secrets.myPassword

  - name: api-call
    trigger: pull
    depends: trigger                # runs after trigger completes
    from:
      http:
        url: ${MY-API-URL}/endpoint
        method: POST
        user: ${MY-API-USER}        # basic auth for the API call
        password: ${MY-API-PASSWORD}
        body: $.request             # POST body from the IDM
        acceptedStatusCodes: "2xx,4xx,5xx"
        connectionTimeout: 30000
    map: response-map.elwood

outputs:
  - name: api-response
    response: true                  # returned to the HTTP caller (sync mode)
    responseStatusCode: $.statusCode  # dynamic HTTP status from data
    map: output.elwood
```

### Key concepts

- **Sources** fetch data and build the **IDM** (Intermediate Data Model)
- **Outputs** consume the IDM and deliver results
- **`.elwood` scripts** are transformation functions — `$` is the input, `$idm` is the accumulated data
- **`response: true`** marks which output is returned to the HTTP caller (sync mode)

---

## Elwood Scripts (.elwood)

Scripts use the Elwood DSL — pipes, lambdas, JSONPath navigation:

```elwood
// Filter active items and transform
$.items[*]
  | where .status == "active"
  | select { id: .id, name: .name.toUpper() }
```

```elwood
// Script with variables
let products = $idm.catalog.products
let enriched = $.orders[*] | select o => {
  ...o,
  productName: products | first p => p.sku == o.sku | select .name
}
return { orders: enriched }
```

Test scripts in the **Playground** before using them in pipelines.

---

## API Endpoints

| Method | URL | Purpose |
|---|---|---|
| `GET` | `/api/health` | Health check |
| `GET` | `/api/pipelines` | List all pipelines |
| `POST` | `/api/pipelines/{id}` | Create pipeline |
| `PUT` | `/api/pipelines/{id}` | Update pipeline |
| `DELETE` | `/api/pipelines/{id}` | Delete pipeline |
| `POST` | `/api/pipelines/{id}/validate` | Validate pipeline YAML |
| `POST` | `/api/v1/trigger/{path}` | **HTTP trigger** — execute pipeline by endpoint URL |
| `POST` | `/api/executions` | Execute pipeline by ID |
| `GET` | `/api/executions` | List recent executions |
| `GET` | `/api/executions/{id}` | Get execution details |
| `GET` | `/api/metrics` | Running/completed/failed counts |

---

## Keyboard Shortcuts (Portal)

| Shortcut | Action |
|---|---|
| `Ctrl+S` | Save pipeline |
| `Ctrl+B` | Toggle sidebar |
| `Ctrl+E` | Toggle file tree |
| `Ctrl+Enter` | Run (in Playground) |
