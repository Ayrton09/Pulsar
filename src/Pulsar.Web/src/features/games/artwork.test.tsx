import { fireEvent, render } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { GameArt } from './GameArt';
import { steamArtwork } from './artwork';

describe('game artwork', () => {
  it('builds Steam addresses only from its own table of games', () => {
    expect(steamArtwork('rust', 'hero')).toBe(
      'https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/252490/library_hero.jpg',
    );
    expect(steamArtwork('minecraft', 'header')).toBeNull();
    expect(steamArtwork('constructor', 'header')).toBeNull();
    expect(steamArtwork('https://example.com/x', 'header')).toBeNull();
  });

  it('draws a tile when the game has no artwork or it does not load', () => {
    const { container, rerender } = render(<GameArt templateKey="rust" kind="header" />);
    const image = container.querySelector('img');
    if (image === null) {
      throw new Error('no image');
    }
    fireEvent.error(image);
    expect(container.querySelector('img')).toBeNull();
    expect(container.querySelector('svg')).not.toBeNull();

    // Another game is tried again.
    rerender(<GameArt templateKey="valheim" kind="header" />);
    expect(container.querySelector('img')).not.toBeNull();
    rerender(<GameArt templateKey="minecraft" kind="header" />);
    expect(container.querySelector('img')).toBeNull();
  });
});
