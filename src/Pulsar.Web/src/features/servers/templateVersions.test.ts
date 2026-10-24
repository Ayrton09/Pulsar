import { describe, expect, it } from 'vitest';
import { compareTemplateVersions, newestOfEachGame } from './templateVersions';

describe('template versions', () => {
  it.each([
    ['1.0.0', '1.0.0', 0],
    ['1.0.1', '1.0.0', 1],
    ['1.10.0', '1.9.0', 1],
    ['2.0.0', '10.0.0', -1],
    ['1.0.0-beta', '1.0.0', -1],
    ['1.0.0-alpha.2', '1.0.0-alpha.10', -1],
    ['1.0.0-alpha.1', '1.0.0-alpha.beta', -1],
    ['1.0.0-alpha', '1.0.0-alpha.1', -1],
    ['1.0.0+build.7', '1.0.0', 0],
  ])('orders %s against %s as the panel does (%i)', (left, right, expected) => {
    expect(compareTemplateVersions(left, right)).toBe(expected);
    expect(compareTemplateVersions(right, left)).toBe(-expected || 0);
  });

  it('keeps the newest of each game, in the order they came', () => {
    const templates = [
      { key: 'ark-sa', version: '1.0.0' },
      { key: 'cs2', version: '1.0.0' },
      { key: 'ark-sa', version: '1.0.1' },
      { key: 'ark-sa', version: '1.0.1-beta' },
    ];
    expect(newestOfEachGame(templates)).toEqual([templates[1], templates[2]]);
  });
});
