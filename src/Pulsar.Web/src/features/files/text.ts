/**
 * The file's line ending: one kind, null when it has a single line, 'mixed' when it has several kinds (the editor
 * writes every line with one ending, so it would change some of them).
 */
export function lineEndings(text: string): '\r\n' | '\n' | '\r' | 'mixed' | null {
  const found = new Set(text.match(/\r\n|\r|\n/g) ?? []);
  if (found.size > 1) {
    return 'mixed';
  }
  const [only] = found;
  return (only as '\r\n' | '\n' | '\r' | undefined) ?? null;
}

/** The file as text, or null when it is not UTF-8 text (a binary file: download it instead). */
export function decodeText(bytes: Uint8Array): { text: string; bom: boolean } | null {
  try {
    // ignoreBOM keeps a byte order mark in the text, so it is written back as it was.
    const text = new TextDecoder('utf-8', { fatal: true, ignoreBOM: true }).decode(bytes);
    if (text.includes('\0')) {
      return null;
    }
    const bom = text.charCodeAt(0) === 0xfeff;
    return { text: bom ? text.slice(1) : text, bom };
  } catch {
    return null;
  }
}
