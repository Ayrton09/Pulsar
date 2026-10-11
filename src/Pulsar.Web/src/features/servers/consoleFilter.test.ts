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

  it('drops a lone escape without the character after it', () => {
    const filter = new ConsoleFilter();

    expect(filter.push(`foo${ESC}\nbar${ESC}\tbaz${ESC}(\r\nqux`)).toBe('foo\nbar\tbaz\r\nqux');
  });

  it('turns a carriage return without a line break into one, so later text cannot overwrite a row', () => {
    const filter = new ConsoleFilter();

    expect(filter.push('Admin: ban griefer\rPlayer: hi')).toBe('Admin: ban griefer\r\nPlayer: hi');
    // A line break after it (in the next chunk too) is the same line break, not another one.
    expect(filter.push('progress 10%\r')).toBe('progress 10%\r\n');
    expect(filter.push('\nnext\r\n')).toBe('next\r\n');
  });

  it('drops a string sequence that never ends, says so and shows what follows', () => {
    const filter = new ConsoleFilter();
    const push = (chunk: string) => filter.push(chunk, '[dropped]');

    // A title with no terminator: held while it may still end, up to the end of its line.
    expect(push(`before ${ESC}]0;title`)).toBe('before ');
    expect(push(' more\nafter')).toBe('[dropped]\nafter');

    // Too long to be held: dropped, and later output is not held behind it.
    expect(push(`${ESC}]0;${'x'.repeat(3000)}`)).toBe('');
    expect(push('y'.repeat(2000))).toBe('[dropped]');
    expect(push('more')).toBe('more');
  });

  it('drops a control sequence that never ends, says so and shows what follows', () => {
    const filter = new ConsoleFilter();
    const push = (chunk: string) => filter.push(chunk, '[dropped]');

    expect(push(`a${ESC}[${'1;'.repeat(5000)}mb`)).toBe('a[dropped]b');
    expect(push(`c${ESC}[${'1;'.repeat(5000)}`)).toBe('c[dropped]');
    expect(push('d')).toBe('d');
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
