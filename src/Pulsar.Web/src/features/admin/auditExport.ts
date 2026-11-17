import type { AuditEntry } from './api';

/** Quotes CSV cells and prevents spreadsheet formulas, including leading whitespace before a formula marker. */
function cell(value: string | null) {
  const text = value ?? '';
  const safe = /^\s*[=+@-]/u.test(text) || /^[\t\r\n]/u.test(text) ? `'${text}` : text;
  return `"${safe.replaceAll('"', '""')}"`;
}

export function auditCsv(entries: AuditEntry[]) {
  const header = [
    'id',
    'occurredAt',
    'action',
    'outcome',
    'actorType',
    'actorUserId',
    'actorDisplay',
    'targetType',
    'targetId',
    'ipAddress',
  ] as const;
  return (
    '\uFEFF' +
    [header.join(','), ...entries.map((entry) => header.map((key) => cell(entry[key])).join(','))].join('\r\n') +
    '\r\n'
  );
}
