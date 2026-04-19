# Elwood Share Worker

Cloudflare Worker + KV that stores shared playground sessions, so large input files don't bloat the URL.

## How it works

- **Small payloads** (URL < 8000 chars): LZ-compressed inline in `#data=...` — no server needed
- **Large payloads**: uploaded to this worker, stored in KV with 90-day TTL, URL becomes `#s=<shortId>`

## Setup

### 1. Create KV namespace

```bash
cd playground/worker
npx wrangler kv namespace create SHARES
```

Copy the output `id` into `wrangler.toml`:

```toml
[[kv_namespaces]]
binding = "SHARES"
id = "paste-id-here"
```

### 2. Install & deploy

```bash
npm install
npm run deploy
```

### 3. Configure the playground

Set the worker URL in the playground's environment:

```bash
# playground/.env.production
VITE_SHARE_API=https://elwood-share.<your-subdomain>.workers.dev
```

For local dev, the playground defaults to `http://localhost:8787`. Run the worker locally with:

```bash
npm run dev
```

## API

### `POST /share`

Store a playground session.

**Body** (JSON):
```json
{ "e": "$.items[*] | select .name", "i": "{...}", "f": "json" }
```

**Response** `201`:
```json
{ "id": "aBcDeFgH" }
```

### `GET /share/:id`

Retrieve a stored session.

**Response** `200`: same JSON as the POST body.
**Response** `404`: `{ "error": "Share not found or expired" }`

## Limits

- Max payload: 25MB
- TTL: 90 days
- CORS: restricted to the configured origin + localhost
