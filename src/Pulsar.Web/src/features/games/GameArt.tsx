import { Box, Gamepad2 } from 'lucide-react';
import { useState } from 'react';
import { steamArtwork, tileGradient, type ArtworkKind } from './artwork';

/**
 * A game's picture, decorative (the game's name is always written next to it). Steam's artwork when the game has it
 * and it loads; otherwise a tile in the game's color with an icon.
 */
export function GameArt({
  templateKey,
  kind,
  className = '',
}: {
  templateKey: string;
  kind: ArtworkKind;
  className?: string;
}) {
  const src = steamArtwork(templateKey, kind);
  // Remembers which address failed, so a different game shown here later is tried again.
  const [failed, setFailed] = useState<string | null>(null);
  if (src !== null && failed !== src) {
    return (
      <img
        src={src}
        alt=""
        referrerPolicy="no-referrer"
        loading="lazy"
        decoding="async"
        draggable={false}
        onError={() => setFailed(src)}
        className={`object-cover ${className}`}
      />
    );
  }

  const Icon = templateKey === 'minecraft' ? Box : Gamepad2;
  return (
    <div
      aria-hidden="true"
      className={`flex items-center justify-center bg-linear-to-br ${tileGradient(templateKey)} ${className}`}
    >
      <Icon className="size-10 text-white/35" strokeWidth={1.5} />
    </div>
  );
}
