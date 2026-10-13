/**
 * File manager paths, as the panel takes them: relative to the server directory, forward slashes, '' for the server
 * directory itself. The panel and the node check every path again; these helpers only build them and catch obvious
 * mistakes in names before a request.
 */
export function joinPath(directory: string, name: string): string {
  return directory === '' ? name : `${directory}/${name}`;
}

export function parentPath(path: string): string {
  const slash = path.lastIndexOf('/');
  return slash < 0 ? '' : path.slice(0, slash);
}

export function baseName(path: string): string {
  return path.slice(path.lastIndexOf('/') + 1);
}

/** Each folder on the way to `path`, for breadcrumbs: [{ name, path }]. */
export function breadcrumbs(path: string): { name: string; path: string }[] {
  if (path === '') {
    return [];
  }
  const parts = path.split('/');
  return parts.map((name, i) => ({ name, path: parts.slice(0, i + 1).join('/') }));
}

/**
 * Characters that are invisible in a name and either reorder what follows (direction embeddings, overrides and
 * isolates) or hide between letters (zero-width space, word joiner, invisible operators, byte order mark). Joiners,
 * non-joiners and direction marks are left out: emoji sequences, Persian and Hebrew names need them.
 */
const HIDDEN = new Set([
  0x200b, 0x202a, 0x202b, 0x202c, 0x202d, 0x202e, 0x2060, 0x2061, 0x2062, 0x2063, 0x2064, 0x2066, 0x2067, 0x2068,
  0x2069, 0xfeff,
]);

/**
 * A file name as the file manager shows it: any name is allowed on the server (games and players choose them), so a
 * character in HIDDEN is shown as its code ("[U+202E]") instead of acting, and "invoice<U+202E>fdp.exe" cannot pass
 * for "invoiceexe.pdf". Only for display: requests use the real name.
 */
export function visibleName(name: string): string {
  let shown = '';
  for (const character of name) {
    const code = character.codePointAt(0) ?? 0;
    shown += HIDDEN.has(code) ? `[U+${code.toString(16).toUpperCase().padStart(4, '0')}]` : character;
  }
  return shown;
}

const RESERVED = /^(con|prn|aux|nul|conin\$|conout\$|com[0-9¹²³]|lpt[0-9¹²³])$/i;

export type NameProblem = 'empty' | 'characters' | 'dots' | 'reserved' | 'length';

/** Why Windows (or Pulsar) would refuse `name` as a file or folder name; null when it is fine. */
export function nameProblem(name: string): NameProblem | null {
  if (name.trim() === '') {
    return 'empty';
  }
  if (name.length > 255) {
    return 'length';
  }
  // eslint-disable-next-line no-control-regex
  if (/[\\/:*?"<>|\u0000-\u001f\u007f]/.test(name)) {
    return 'characters';
  }
  if (name === '.' || name === '..' || name.startsWith(' ') || name.endsWith(' ') || name.endsWith('.')) {
    return 'dots';
  }
  const dot = name.indexOf('.');
  return RESERVED.test((dot < 0 ? name : name.slice(0, dot)).trimEnd()) ? 'reserved' : null;
}
