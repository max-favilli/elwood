import { registerMethod } from '@elwood-lang/core';
import * as XLSX from 'xlsx';

/**
 * Register fromXlsx and toXlsx methods with the Elwood engine.
 * Called automatically on import.
 */
export function register(): void {
  registerMethod('fromXlsx', fromXlsx);
  registerMethod('toXlsx', toXlsx);
}

function getOpt(args: unknown[], key: string, defaultVal: string): string {
  if (args.length > 0 && typeof args[0] === 'object' && args[0] !== null) {
    const v = (args[0] as Record<string, unknown>)[key];
    if (v !== undefined && v !== null) return String(v);
  }
  return defaultVal;
}

function getOptBool(args: unknown[], key: string, defaultVal: boolean): boolean {
  if (args.length > 0 && typeof args[0] === 'object' && args[0] !== null) {
    const v = (args[0] as Record<string, unknown>)[key];
    if (v !== undefined && v !== null) return v === true || v === 'true';
  }
  return defaultVal;
}

function getOptNumber(args: unknown[], key: string, defaultVal: number): number {
  if (args.length > 0 && typeof args[0] === 'object' && args[0] !== null) {
    const v = (args[0] as Record<string, unknown>)[key];
    if (v !== undefined && v !== null) return Number(v);
  }
  return defaultVal;
}

function getAlphabeticColumnName(index: number): string {
  let name = '';
  let i = index + 1;
  while (i > 0) { i--; name = String.fromCharCode(65 + i % 26) + name; i = Math.floor(i / 26); }
  return name;
}

function fromXlsx(target: unknown, args: unknown[]): unknown {
  const base64 = typeof target === 'string' ? target : '';
  const hasHeaders = getOptBool(args, 'headers', true);
  const sheetOpt = args.length > 0 && typeof args[0] === 'object' && args[0] !== null
    ? (args[0] as Record<string, unknown>)['sheet']
    : undefined;

  let workbook: XLSX.WorkBook;
  try {
    workbook = XLSX.read(base64, { type: 'base64' });
  } catch {
    return [];
  }

  // Find the target sheet
  let sheetName: string;
  if (typeof sheetOpt === 'string') {
    sheetName = workbook.SheetNames.includes(sheetOpt) ? sheetOpt : workbook.SheetNames[0];
  } else if (typeof sheetOpt === 'number') {
    sheetName = workbook.SheetNames[sheetOpt] ?? workbook.SheetNames[0];
  } else {
    sheetName = workbook.SheetNames[0];
  }

  if (!sheetName) return [];
  const sheet = workbook.Sheets[sheetName];
  if (!sheet) return [];

  if (hasHeaders) {
    // XLSX.utils.sheet_to_json returns array of objects with header keys
    return XLSX.utils.sheet_to_json(sheet, { defval: '' });
  }

  // No headers: return array of objects with alphabetic column names
  const rows: unknown[][] = XLSX.utils.sheet_to_json(sheet, { header: 1, defval: '' });
  const maxCols = Math.max(...rows.map(r => r.length), 0);
  const colNames = Array.from({ length: maxCols }, (_, i) => getAlphabeticColumnName(i));

  return rows.map(row => {
    const obj: Record<string, unknown> = {};
    colNames.forEach((col, j) => { obj[col] = j < row.length ? String(row[j] ?? '') : ''; });
    return obj;
  });
}

function toXlsx(target: unknown, args: unknown[]): unknown {
  const includeHeaders = getOptBool(args, 'headers', true);
  const sheetName = getOpt(args, 'sheet', 'Sheet1');

  if (!Array.isArray(target)) return '';

  const workbook = XLSX.utils.book_new();
  const sheet = XLSX.utils.json_to_sheet(target as Record<string, unknown>[], {
    skipHeader: !includeHeaders,
  });
  XLSX.utils.book_append_sheet(workbook, sheet, sheetName);

  const buffer = XLSX.write(workbook, { type: 'base64', bookType: 'xlsx' });
  return buffer;
}

// Auto-register on import
register();
