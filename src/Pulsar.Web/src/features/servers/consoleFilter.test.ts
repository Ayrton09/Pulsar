import { describe, expect, it } from 'vitest';
import { ConsoleFilter } from './consoleFilter';

const ESC = '\x1b';

describe('ConsoleFilter', () => {
  it('keeps text, colours, line breaks and links', () => {
    const filter = new ConsoleFilter();
    const text = `${ESC}[1;31mError${ESC}[0m: disk\tfull\r\n${ESC}]8;;https://example.com${ESC}\\site${ESC}]8;;${ESC}\\\n`;

    expect(filter.push(text)).toBe(text);
  });

  it('drops what could clear or rewrite what the admin reads', () => {
    const filter = new ConsoleFilter();

    expect(
      filter.push(
        `a${ESC}[2Jb${ESC}[3J${ESC}cc${ESC}[?1049hd${ESC}[5Ae${ESC}[Kf${ESC}]0;title\x07g${ESC}(Bh\x9b2Ji\x07j\x08k`,
      ),
    ).toBe('abcdefghijk');
  });

  it('turns a pseudo console moving to the next row into a line break', () => {
    const filter = new ConsoleFilter();

    // The first home (nothing written yet) adds nothing, as on the agent.
    expect(filter.push(`${ESC}[H${ESC}[2Jfirst line${ESC}[2;1Hsecond line${ESC}[3;1H`)).toBe(
      'first line\r\nsecond line\r\n',
    );
  });

  it('keeps a sequence split between two chunks whole', () => {
    const filter = new ConsoleFilter();

    expect(filter.push(`red ${ESC}[3`)).toBe('red ');
    expect(filter.push(`1mtext${ESC}`)).toBe(`${ESC}[31mtext`);
    expect(filter.push('[2Jmore')).toBe('more');
  });
});
