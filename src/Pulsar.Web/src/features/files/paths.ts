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
