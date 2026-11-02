import type { Terminal } from '@xterm/xterm';

/**
 * Copies the console's selection on Ctrl+C or Cmd+C (with Shift too), which the terminal would otherwise take as its own
 * key; true when it did, so the terminal ignores the key. Only the reader's own key writes to the clipboard: a game
 * cannot (no clipboard addon, OSC 52).
 */
export function copySelection(event: KeyboardEvent, term: Pick<Terminal, 'hasSelection' | 'getSelection'>): boolean {
  if (
    event.type !== 'keydown' ||
    !(event.ctrlKey || event.metaKey) ||
    event.altKey ||
    event.key.toLowerCase() !== 'c' ||
    !term.hasSelection()
  ) {
    return false;
  }

  void navigator.clipboard?.writeText(term.getSelection()).catch(() => undefined);
  return true;
}
