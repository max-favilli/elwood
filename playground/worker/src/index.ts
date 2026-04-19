interface Env {
  SHARES: KVNamespace;
  ALLOWED_ORIGIN: string;
  MAX_PAYLOAD_BYTES: string;
  TTL_SECONDS: string;
}

function generateId(): string {
  const chars = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789';
  const bytes = new Uint8Array(8);
  crypto.getRandomValues(bytes);
  return Array.from(bytes, b => chars[b % chars.length]).join('');
}

function corsHeaders(origin: string, allowedOrigin: string): Record<string, string> {
  // Allow the configured origin + localhost for dev
  const isAllowed = origin === allowedOrigin
    || origin.startsWith('http://localhost:')
    || origin.startsWith('http://127.0.0.1:');

  if (!isAllowed) return {};

  return {
    'Access-Control-Allow-Origin': origin,
    'Access-Control-Allow-Methods': 'GET, POST, OPTIONS',
    'Access-Control-Allow-Headers': 'Content-Type',
    'Access-Control-Max-Age': '86400',
  };
}

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    const url = new URL(request.url);
    const origin = request.headers.get('Origin') || '';
    const cors = corsHeaders(origin, env.ALLOWED_ORIGIN);

    // CORS preflight
    if (request.method === 'OPTIONS') {
      return new Response(null, { status: 204, headers: cors });
    }

    // POST /share — store a payload, return short ID
    if (request.method === 'POST' && url.pathname === '/share') {
      const maxBytes = parseInt(env.MAX_PAYLOAD_BYTES) || 26214400;
      const contentLength = parseInt(request.headers.get('Content-Length') || '0');
      if (contentLength > maxBytes) {
        return Response.json(
          { error: `Payload too large (max ${Math.round(maxBytes / 1024 / 1024)}MB)` },
          { status: 413, headers: cors }
        );
      }

      let body: string;
      try {
        body = await request.text();
      } catch {
        return Response.json({ error: 'Failed to read body' }, { status: 400, headers: cors });
      }

      if (body.length > maxBytes) {
        return Response.json(
          { error: `Payload too large (max ${Math.round(maxBytes / 1024 / 1024)}MB)` },
          { status: 413, headers: cors }
        );
      }

      // Validate it's parseable JSON with expected shape
      try {
        const parsed = JSON.parse(body);
        if (!parsed.e && !parsed.i) {
          return Response.json({ error: 'Missing expression or input' }, { status: 400, headers: cors });
        }
      } catch {
        return Response.json({ error: 'Invalid JSON' }, { status: 400, headers: cors });
      }

      const id = generateId();
      const ttl = parseInt(env.TTL_SECONDS) || 7776000;

      await env.SHARES.put(id, body, { expirationTtl: ttl });

      return Response.json({ id }, {
        status: 201,
        headers: { ...cors, 'Content-Type': 'application/json' },
      });
    }

    // GET /share/:id — retrieve a stored payload
    if (request.method === 'GET' && url.pathname.startsWith('/share/')) {
      const id = url.pathname.slice(7); // strip "/share/"
      if (!id || id.length > 20) {
        return Response.json({ error: 'Invalid ID' }, { status: 400, headers: cors });
      }

      const data = await env.SHARES.get(id);
      if (!data) {
        return Response.json({ error: 'Share not found or expired' }, { status: 404, headers: cors });
      }

      return new Response(data, {
        headers: { ...cors, 'Content-Type': 'application/json' },
      });
    }

    return Response.json({ error: 'Not found' }, { status: 404, headers: cors });
  },
};
