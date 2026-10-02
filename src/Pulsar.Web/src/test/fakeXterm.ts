/**
 * Stands in for xterm and its addons in unit tests (test/setup.ts; jsdom cannot lay out a terminal): it keeps what
 * was written, so tests read the console as text. The real terminal is covered by the end-to-end tests.
 */
export const terminals: Terminal[] = [];

export class Terminal {
  text = '';
  disposed = false;
  readonly options: unknown;

  constructor(options: unknown) {
    this.options = options;
    terminals.push(this);
  }

  open() {}

  loadAddon() {}

  write(data: string) {
    this.text += data;
  }

  clear() {
    this.text = '';
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
