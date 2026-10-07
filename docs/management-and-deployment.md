# Drafts, deployments and management access

The Runtime API now separates authoring from deployment. This document describes the M2 local/filesystem runtime. Provider-native deployment catalogs and infrastructure remain M6 work; the existing Azure Blob/Redis startup registry is a legacy prototype, not a deployment target for this portal workflow.

## Local startup

From the sibling `elwood-portal` repository run `npm run dev:all`. The launcher enables **loopback-only local management access** and the API prints the path of its generated token file, normally `dotnet/src/Elwood.Runtime.Api/.elwood/state/management-token`. Copy the file's contents into the portal's management token field. Do not commit or share this file. The playground remains accessible without signing in.

The portal stores the token in the current tab's session storage. Sign out removes it. Management tokens are never sent to pipeline trigger endpoints. Starting the API directly does **not** enable anonymous access or generate a local credential: configure management identities, or explicitly set `Elwood__Management__LocalDevelopment=true` for local development.

## Authoring and deployment

1. Create or **Save draft**: stores YAML and scripts together, without changing live endpoints.
2. **Validate**: checks the current editor snapshot without saving or publishing it.
3. **Test draft**: runs the current editor snapshot synchronously, including definitions with `mode: async`. History records `isTest`, pipeline ID and the exact snapshot hash. Tests explicitly select real systems or mocks; see [local testing](local-testing.md).
4. **Deploy**: validates the saved draft, creates an immutable bundle and switches the active deployment. Unsaved editor changes must be saved first. A stale draft hash returns HTTP 409; reload and reconcile changes rather than retrying blindly.

Revisions are SHA-256 hashes of the complete YAML and script dictionary, ordered by script name. All included scripts contribute to the hash. An execution captures a detached bundle before starting, and stores its revision ID and snapshot reference. Queued steps use that execution's snapshot. A deployment affects subsequent triggers only. Resume and replay use the saved revision unless replay explicitly selects another published revision; see [durable processing](durable-processing.md).

The local catalog stores one atomic manifest per pipeline under `Elwood:StateDir/deployments`. Each manifest contains the mutable draft, immutable published bundles and active revision pointer. Writes use an interprocess lock and atomic replacement on the same local filesystem. Use one shared, persistent state directory for local API processes; this is not a distributed cloud catalog. Back up the entire state directory, including execution documents. Deploy detects endpoint collisions. Deleting a pipeline deactivates it and retains published history; retired IDs cannot be reused.

Published revision metadata and content are available through authenticated `GET /api/pipelines/{id}/revisions` and `GET /api/pipelines/{id}/revisions/{revision}`. The catalog is separate from Git commit history; saving a draft does not create a Git commit.

### Existing filesystem pipelines

Existing pipeline folders are shown as **Legacy / migrate** and keep their current trigger behavior. Editing and deletion are blocked until a maintainer with deployment permission chooses **Migrate existing deployment**. Migration captures the current YAML/scripts as the active immutable revision and creates an identical draft. It does not activate edited content, remove the original files, or change the endpoint. After migration, the catalog owns the pipeline; editing the old folder no longer changes its draft or deployment.

Management authentication and secret grants apply immediately, including legacy pipelines. Configure grants before switching an installation to this runtime. Legacy definitions with unsupported or unsafe configurations can still be imported for correction, but deployment validation must pass before a new revision is activated. Conflicting legacy routes fail explicitly rather than choosing a pipeline arbitrarily.

## Shared management identities

M2 uses provider-neutral bearer credentials with host-configured permissions and pipeline scopes. Individual token hashes identify maintainers or automation clients. OIDC/enterprise sign-in is not implemented; choose that integration before a broad shared rollout if required. Provision strong random tokens, keep their plaintext private and configure only SHA-256 hashes on the host. Revoking a token currently requires updating configuration and restarting the API.

Example host configuration (values are placeholders, not working credentials):

```json
{
  "Elwood": {
    "Management": {
      "Origins": ["https://portal.example.com"],
      "Tokens": [{
        "Name": "orders-maintainer",
        "TokenSha256": "REPLACE_WITH_SHA256_HEX_OF_A_RANDOM_TOKEN",
        "Permissions": ["read", "write", "test", "deploy", "recover"],
        "Pipelines": ["orders"]
      }]
    },
    "SecretGrants": {
      "orders": {
        "Test": ["crm.test.user", "crm.test.password"],
        "Run": ["crm.live.user", "crm.live.password"]
      }
    }
  }
}
```

| Permission | Operations |
|---|---|
| `read` | View authorized drafts, revisions, execution history and metrics |
| `write` | Create/save drafts, edit scripts, validate snapshots |
| `test` | Execute draft snapshots and test saved scripts |
| `deploy` | Migrate, activate revisions and delete/deactivate pipelines |
| `recover` | Resume/replay retained live executions (also requires pipeline read access) |

Scopes are exact pipeline IDs; `"*"` grants all pipelines. Permissions are independent; a portal maintainer normally needs `read` alongside other permissions. Execution authorization uses stable pipeline IDs, never the editable YAML display name. Old execution records without IDs are visible only to identities scoped to all pipelines. Lists and metrics are filtered on the server.

All management routes require authentication except health; the trigger routes retain their own pipeline-defined authentication. Remote bearer requests require HTTPS. Configure TLS on the API host; this runtime does not implicitly trust forwarded headers or authenticate on behalf of a reverse proxy. CORS permits only configured portal origins (localhost:3000 by default). The local developer identity accepts loopback connections only.

### Secret and filesystem boundaries

Secret grants are host configuration, never editable YAML. `Test` and `Run` grants are independent exact-key allowlists. Both `${KEY}` and `$secrets.key` pass through the scoped provider. Missing or unauthorized keys fail; scoped execution never falls through to arbitrary environment variables. Real-system management tests always use test grants, regardless of an input `isTest` flag. Mock tests do not resolve host secrets. Trigger authentication uses run grants. Secret values are not returned by management endpoints.

Keep the state, legacy pipeline and secret directories writable only by the runtime account and trusted operators. Identifiers and script names reject traversal, absolute paths, Windows device names/alternate streams, links and junctions. Bundles are parsed directly in memory, without writing client-supplied script paths into temporary directories. The management runtime disables host-filesystem source/destination connectors; local-file workflows remain available to the trusted CLI. HTTP connectors remain subject to the installation's network policy.

## API changes

| Request | Effect |
|---|---|
| `POST /api/pipelines/{id}` with `{yaml,scripts}` | Create a draft only |
| `PUT /api/pipelines/{id}` with `{yaml,scripts}` and `If-Match: "draftHash"` | Update that exact draft |
| `POST /api/pipelines/{id}/validate` with `{content:{yaml,scripts}}` | Validate supplied snapshot without saving; `{}` validates the saved draft |
| `POST /api/pipelines/{id}/deploy` with `{expectedRevisionId}` | Publish and activate the saved draft |
| `POST /api/pipelines/{id}/migrate` with `{expectedRevisionId}` | Explicitly adopt legacy content without changing live behavior |
| `POST /api/executions` with `{pipelineId,payload,testMode,mocks?,content?,expectedRevisionId?}` | Synchronous draft test with explicit real/mock connectors; see [local testing](local-testing.md) |
| `POST /api/pipelines/{id}/scripts/{name}/test` | Test the saved script; optional `If-Match` prevents stale tests |

Script tests return `X-Elwood-Revision` alongside the existing `X-Elwood-Warnings`. Trigger responses identify their published bundle with `X-Elwood-Revision`. Live async definitions return 202 only after durable acceptance. Recovery, attempt history, idempotency and retention are described in [durable processing](durable-processing.md). The legacy Azure Functions host remains outside this filesystem runtime contract.

## Verification

`DeploymentTests` covers draft/live isolation, explicit migration, collisions, stale and concurrent writes, immutable history, async revision pinning, filesystem containment and secret denial. The isolated `scripts/runtime-api.test.mjs` additionally checks real HTTP authentication, permissions and list filtering, snapshot tests, warning transport, draft/deploy semantics, traversal rejection, CORS and distinct live/test secret grants. Portal tests exercise bearer transport, conditional saves, explicit deploy/migration requests and snapshot identity.

Build the Runtime API before running its Node contract test. Azure adapter tests require Docker and run in the separate CI integration job. The Redis execution header now retains pipeline/revision/snapshot identity and the test flag; native cloud deployment wiring remains outside this local catalog.
