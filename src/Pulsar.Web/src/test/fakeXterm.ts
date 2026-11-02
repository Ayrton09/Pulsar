/**
 * Stands in for xterm and its addons in unit tests (test/setup.ts; jsdom cannot lay out a terminal): it keeps what
 * was written, so tests read the console as text. The real terminal is covered by the end-to-end tests.
 */
export const terminals: Terminal[] = [];

const lines = (text: string) => text.replace(/\r\n/g, '\n').split('\n');

export class Terminal {
  text = '';
  disposed = false;
  readonly options: Record<string, unknown>;
  selection = '';
  keyHandler: ((event: KeyboardEvent) => boolean) | null = null;
  scrollHandler: (() => void) | null = null;
  scrolledToBottom = 0;
  /** Where the reader is: at the end when viewportY reaches baseY. */
  readonly buffer = {
    active: {
      viewportY: 0,
      baseY: 0,
      length: 0,
      getLine: (index: number) => {
        const line = lines(this.text)[index];
        return line === undefined ? undefined : { isWrapped: false, translateToString: () => line };
      },
    },
  };

  constructor(options: Record<string, unknown>) {
    this.options = { ...options };
    terminals.push(this);
  }

  open() {}

  loadAddon() {}

  attachCustomKeyEventHandler(handler: (event: KeyboardEvent) => boolean) {
    this.keyHandler = handler;
  }

  onScroll(handler: () => void) {
    this.scrollHandler = handler;
    return { dispose: () => (this.scrollHandler = null) };
  }

  scrollToBottom() {
    this.scrolledToBottom++;
  }

  hasSelection() {
    return this.selection.length > 0;
  }

  getSelection() {
    return this.selection;
  }

  write(data: string) {
    this.text += data;
    this.buffer.active.length = lines(this.text).length;
  }

  clear() {
    this.text = '';
    this.buffer.active.length = 0;
  }

  dispose() {
    this.disposed = true;
  }
}

export class FitAddon {
  fit() {}
}

export class SearchAddon {
  readonly searches: string[] = [];

  findNext(query: string) {
    this.searches.push(query);
    return true;
  }

  findPrevious(query: string) {
    this.searches.push(query);
    return true;
  }
}

export class WebLinksAddon {
  dispose() {}
}
