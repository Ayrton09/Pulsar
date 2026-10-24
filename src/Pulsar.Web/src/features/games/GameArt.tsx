import { Box, Gamepad2 } from 'lucide-react';
import { useState } from 'react';
import { useAppearance } from './api';
import { gameArtwork, tileGradient, type ArtworkKind } from './artwork';

/**
 * A game's picture, decorative (the game's name is always written next to it). Its artwork when the panel shows it,
 * the game has it and it loads; otherwise a tile in the game's color with an icon. Until the panel says whether it
 * shows artwork, the tile: nothing is asked of Steam before that.
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
  const appearance = useAppearance();
  const src = appearance.data?.steamArtwork === true ? gameArtwork(templateKey, kind) : null;
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

  const Icon = templateKey === 'minecraft' || templateKey === 'minecraft-bedrock' ? Box : Gamepad2;
  return (
    <div
      aria-hidden="true"
      className={`flex items-center justify-center bg-linear-to-br ${tileGradient(templateKey)} ${className}`}
    >
      <Icon className="size-10 text-white/35" strokeWidth={1.5} />
    </div>
  );
}
