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
