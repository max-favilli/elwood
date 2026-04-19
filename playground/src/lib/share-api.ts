// Worker URL — set via env var or fallback to localhost for dev
const SHARE_API = import.meta.env.VITE_SHARE_API || 'http://localhost:8787';

export interface SharePayload {
  e: string;  // expression/script
  i: string;  // input data
  f: string;  // format (json, csv, xml, txt)
}

export async function createShare(payload: SharePayload): Promise<string> {
  const res = await fetch(`${SHARE_API}/share`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(payload),
  });

  if (!res.ok) {
    const err = await res.json().catch(() => ({ error: 'Upload failed' }));
    throw new Error((err as { error: string }).error || `HTTP ${res.status}`);
  }

  const { id } = await res.json() as { id: string };
  return id;
}

export async function loadShare(id: string): Promise<SharePayload> {
  const res = await fetch(`${SHARE_API}/share/${id}`);

  if (!res.ok) {
    const err = await res.json().catch(() => ({ error: 'Not found' }));
    throw new Error((err as { error: string }).error || `HTTP ${res.status}`);
  }

  return await res.json() as SharePayload;
}
