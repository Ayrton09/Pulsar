const ESC = '\x1b';
const BEL = '\x07';
const CSI_C1 = '\x9b';
const OSC_C1 = '\x9d';
/** An unfinished sequence longer than this is not one: it is dropped rather than held forever. */
const MAX_PENDING = 4096;

type Sequence =
  | { length: number; kind: 'csi'; params: string; final: string }
  | { length: number; kind: 'osc'; body: string }
  | { length: number; kind: 'dropped' };

/**
 * What a game writes, made safe for the console view (docs/architecture/07, "Console"): text, line breaks, carriage
 * returns, tabs, colours (SGR) and web links (OSC 8) go through; every other control character or sequence (clearing
 * the screen or the scrollback, the alternate screen, cursor moves, a reset, titles) is dropped, so player or game text
 * cannot wipe or rewrite what the admin reads. A pseudo console ends rows with a move to the start of the next one
 * instead of a line break: such a move becomes a line break (after text only, as the agent reads it). A sequence split
 * between two chunks is held until the rest arrives.
 */
export class ConsoleFilter {
  private pending = '';
  private lineHasText = false;

  /** Forgets an unfinished sequence (after a gap, its rest will not come). */
  reset() {
    this.pending = '';
  }

  push(chunk: string): string {
    const text = this.pending + chunk;
    this.pending = '';
    let out = '';
    let i = 0;
    while (i < text.length) {
      const c = text[i] as string;
      if (c === ESC || c === CSI_C1 || c === OSC_C1) {
        const sequence = parse(text, i);
        if (sequence === null) {
          const rest = text.slice(i);
          this.pending = rest.length > MAX_PENDING ? '' : rest;
          break;
        }
        out += this.translate(sequence);
        i += sequence.length;
        continue;
      }
      const code = c.charCodeAt(0);
      if (c === '\n') {
        out += c;
        this.lineHasText = false;
      } else if (c === '\r' || c === '\t') {
        out += c;
      } else if (code >= 0x20 && code !== 0x7f && !(code >= 0x80 && code < 0xa0)) {
        out += c;
        this.lineHasText = true;
      }
      i++;
    }
    return out;
  }

  private translate(sequence: Sequence): string {
    if (sequence.kind === 'dropped') {
      return '';
    }
    if (sequence.kind === 'osc') {
      // Hyperlinks only: the terminal's link handler opens web links and nothing else.
      return sequence.body.startsWith('8;') ? `${ESC}]${sequence.body}${ESC}\\` : '';
    }
    if (sequence.final === 'm' && /^[0-9;:]*$/.test(sequence.params)) {
      return `${ESC}[${sequence.params}m`;
    }
    if (sequence.final === 'H' && /^[0-9]*(;1?)?$/.test(sequence.params)) {
      if (this.lineHasText) {
        this.lineHasText = false;
        return '\r\n';
      }
    }
    return '';
  }
}

/** The sequence starting at `start` (a malformed one is dropped up to where it breaks), null when unfinished. */
function parse(text: string, start: number): Sequence | null {
  let i = start;
  let kind: 'csi' | 'osc' | 'string' | 'escape';
  if (text[i] === CSI_C1) {
    kind = 'csi';
    i += 1;
  } else if (text[i] === OSC_C1) {
    kind = 'osc';
    i += 1;
  } else {
    const next = text[i + 1];
    if (next === undefined) {
      return null;
    }
    i += 2;
    kind = next === '[' ? 'csi' : next === ']' ? 'osc' : 'PX^_'.includes(next) ? 'string' : 'escape';
    if (kind === 'escape') {
      // ESC, intermediates (ESC ( B), then one final character (ESC c, ESC 7…). Anything else ends it unfinished: the
      // ESC (and intermediates) go, and that character is read again (a line break after a lone ESC stays).
      let j = i - 1;
      while (j < text.length && isIntermediate(text, j)) {
        j++;
      }
      if (j >= text.length) {
        return null;
      }
      return inRange(text, j, 0x30, 0x7e)
        ? { length: j + 1 - start, kind: 'dropped' }
        : { length: j - start, kind: 'dropped' };
    }
  }

  if (kind === 'csi') {
    const paramsStart = i;
    while (i < text.length && inRange(text, i, 0x30, 0x3f)) {
      i++;
    }
    const params = text.slice(paramsStart, i);
    while (i < text.length && isIntermediate(text, i)) {
      i++;
    }
    if (i >= text.length) {
      return null;
    }
    if (!inRange(text, i, 0x40, 0x7e)) {
      return { length: i - start, kind: 'dropped' };
    }
    return { length: i + 1 - start, kind: 'csi', params, final: text[i] as string };
  }

  // OSC, DCS, SOS, PM and APC strings end with BEL (OSC) or ESC \.
  const bodyStart = i;
  while (i < text.length) {
    if (text[i] === BEL) {
      return finish(i, i + 1);
    }
    if (text[i] === ESC) {
      if (i + 1 >= text.length) {
        return null;
      }
      return finish(i, text[i + 1] === '\\' ? i + 2 : i);
    }
    i++;
  }
  return null;

  function finish(bodyEnd: number, end: number): Sequence {
    return kind === 'osc'
      ? { length: end - start, kind: 'osc', body: text.slice(bodyStart, bodyEnd) }
      : { length: end - start, kind: 'dropped' };
  }
}

function inRange(text: string, index: number, low: number, high: number) {
  const code = text.charCodeAt(index);
  return code >= low && code <= high;
}

function isIntermediate(text: string, index: number) {
  return inRange(text, index, 0x20, 0x2f);
}
