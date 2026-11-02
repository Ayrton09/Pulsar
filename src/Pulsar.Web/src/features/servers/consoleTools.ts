import type { Terminal } from '@xterm/xterm';

export const FONT_SIZES = { min: 10, max: 24, default: 13 } as const;
const FONT_KEY = 'pulsar.console.fontSize';
const HISTORY_KEY = 'pulsar.console.history';
export const HISTORY = 50;

/** Everything the console holds (its scrollback included), as plain text: what "Copy all" and "Download" take. */
export function consoleText(term: Pick<Terminal, 'buffer'>): string {
  const buffer = term.buffer.active;
  const lines: string[] = [];
  for (let i = 0; i < buffer.length; i++) {
    const line = buffer.getLine(i);
    if (!line) {
      continue;
    }
    const text = line.translateToString(true);
    // A line the terminal wrapped continues the one before it.
    if (line.isWrapped && lines.length > 0) {
      lines[lines.length - 1] += text;
    } else {
      lines.push(text);
    }
  }
  while (lines.length > 0 && lines[lines.length - 1] === '') {
    lines.pop();
  }
  return lines.length === 0 ? '' : lines.join('\n') + '\n';
}

/** The downloaded file's name: the server's short id and the moment, in the reader's time zone. */
export function consoleFileName(shortId: string, now: Date): string {
  const two = (n: number) => String(n).padStart(2, '0');
  const stamp = `${now.getFullYear()}-${two(now.getMonth() + 1)}-${two(now.getDate())}_${two(now.getHours())}-${two(now.getMinutes())}`;
  return `console-${shortId.replace(/[^A-Za-z0-9_-]/g, '')}-${stamp}.txt`;
}

/** The text size the reader chose, kept in this browser (the default when none, or when storage is unavailable). */
export function loadFontSize(): number {
  try {
    const stored = Number(localStorage.getItem(FONT_KEY));
    return Number.isInteger(stored) && stored >= FONT_SIZES.min && stored <= FONT_SIZES.max
      ? stored
      : FONT_SIZES.default;
  } catch {
    return FONT_SIZES.default;
  }
}

export function saveFontSize(size: number) {
  try {
    localStorage.setItem(FONT_KEY, String(size));
  } catch {
    // Not kept: the next visit starts at the default.
  }
}

/** A command that may carry a secret (a password, a token, a game server login): never kept in the browser. */
export function isSensitive(command: string): boolean {
  return /pass|token|secret|steamaccount|apikey|api_key|key\s/i.test(command);
}

const historyKey = (userId: string, serverId: string) => `${HISTORY_KEY}.${userId}.${serverId}`;

/** The commands this user sent to this server from this browser, oldest first. */
export function loadHistory(userId: string, serverId: string): string[] {
  try {
    const stored: unknown = JSON.parse(localStorage.getItem(historyKey(userId, serverId)) ?? '[]');
    return Array.isArray(stored)
      ? stored.filter((c): c is string => typeof c === 'string' && c.length > 0 && !isSensitive(c)).slice(-HISTORY)
      : [];
  } catch {
    return [];
  }
}

export function saveHistory(userId: string, serverId: string, history: string[]) {
  try {
    localStorage.setItem(
      historyKey(userId, serverId),
      JSON.stringify(history.filter((c) => !isSensitive(c)).slice(-HISTORY)),
    );
  } catch {
    // Kept for this page only.
  }
}

/**
 * What Tab completes `typed` to: the commands sent before that start with it, newest first, each once (case aside);
 * `turn` walks through them. Null when none does.
 */
export function complete(typed: string, history: readonly string[], turn: number): string | null {
  const prefix = typed.toLocaleLowerCase();
  const seen = new Set<string>();
  const matches: string[] = [];
  for (let i = history.length - 1; i >= 0; i--) {
    const command = history[i] ?? '';
    const key = command.toLocaleLowerCase();
    if (key.startsWith(prefix) && key !== prefix && !seen.has(key)) {
      seen.add(key);
      matches.push(command);
    }
  }
  return matches.length === 0 ? null : (matches[turn % matches.length] ?? null);
}
