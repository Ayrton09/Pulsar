/**
 * Characters that are invisible in a name and either reorder what follows (direction embeddings, overrides and
 * isolates, the Arabic letter mark) or hide between letters (zero-width space, soft hyphen, word joiner, invisible
 * operators, byte order mark, tag characters, the Mongolian vowel separator): the Unicode format category. Joiners,
 * non-joiners and the left-to-right and right-to-left marks are left out, since emoji sequences, Persian and Hebrew
 * names need them. The line and paragraph separators and the invisible Hangul fillers are not format characters but
 * hide the same way.
 */
const HIDDEN_FORMAT = /\p{Cf}/u;
const HIDDEN_OTHER = new Set([0x2028, 0x2029, 0x115f, 0x1160, 0x3164]);
const NEEDED = new Set([0x200c, 0x200d, 0x200e, 0x200f]);

function isHidden(code: number, character: string): boolean {
  return !NEEDED.has(code) && (HIDDEN_OTHER.has(code) || HIDDEN_FORMAT.test(character));
}

/**
 * A name as the panel shows it (a file, a server, a node, a Workshop item: games, players, nodes and users choose them):
 * a hidden character is shown as its code ("[U+202E]") instead of acting, and "invoice<U+202E>fdp.exe" cannot pass for
 * "invoiceexe.pdf". Only for display: requests use the real name.
 */
export function visibleName(name: string): string {
  let shown = '';
  for (const character of name) {
    const code = character.codePointAt(0) ?? 0;
    shown += isHidden(code, character) ? `[U+${code.toString(16).toUpperCase().padStart(4, '0')}]` : character;
  }
  return shown;
}
