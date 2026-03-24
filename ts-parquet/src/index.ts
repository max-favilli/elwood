import { registerMethod } from '@elwood-lang/core';
import { parquetRead } from 'hyparquet';

/**
 * Register fromParquet with the Elwood engine.
 * Called automatically on import.
 *
 * Note: toParquet is only available in the .NET package (Elwood.Parquet)
 * because Parquet writing requires schema + compression support that
 * no lightweight JS library provides. Use the .NET CLI or API for
 * Parquet output.
 */
export function register(): void {
  registerMethod('fromParquet', fromParquet);
}

function fromParquet(target: unknown, _args: unknown[]): unknown {
  const base64 = typeof target === 'string' ? target : '';

  let bytes: Uint8Array;
  try {
    // Decode base64 to bytes
    const binary = atob(base64);
    bytes = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
  } catch {
    return [];
  }

  const rows: Record<string, unknown>[] = [];

  // hyparquet's parquetRead is synchronous when given an arrayBuffer and onComplete
  parquetRead({
    file: bytes.buffer,
    onComplete: (data: Record<string, unknown>[]) => {
      rows.push(...data);
    },
  });

  return rows;
}

// Auto-register on import
register();
