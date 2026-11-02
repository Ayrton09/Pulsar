import { describe, expect, it } from 'vitest';
import { complete, consoleFileName, isSensitive } from './consoleTools';

describe('console tools', () => {
  it('completes from the newest command that starts with what was typed, each once', () => {
    const history = ['say hi', 'mp_maxrounds 30', 'MP_MAXROUNDS 30', 'mp_restartgame 1'];

    expect(complete('mp_', history, 0)).toBe('mp_restartgame 1');
    expect(complete('mp_', history, 1)).toBe('MP_MAXROUNDS 30');
    expect(complete('mp_', history, 2)).toBe('mp_restartgame 1'); // round again
    expect(complete('kick', history, 0)).toBeNull();
    expect(complete('say hi', history, 0)).toBeNull(); // already all of it
  });

  it('never keeps a command that may carry a secret', () => {
    expect(isSensitive('rcon_password x')).toBe(true);
    expect(isSensitive('sv_password abc')).toBe(true);
    expect(isSensitive('sv_setsteamaccount 0123')).toBe(true);
    expect(isSensitive('mp_maxrounds 30')).toBe(false);
    expect(isSensitive('kick player')).toBe(false);
  });

  it('names the download after the server and the moment', () => {
    expect(consoleFileName('abcd1234', new Date(2026, 10, 2, 9, 5))).toBe('console-abcd1234-2026-11-02_09-05.txt');
    expect(consoleFileName('../x', new Date(2026, 0, 1, 0, 0))).toBe('console-x-2026-01-01_00-00.txt');
  });
});
