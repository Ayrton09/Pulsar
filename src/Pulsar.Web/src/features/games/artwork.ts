/**
 * Game artwork from Steam's store CDN, the one image origin the panel's Content-Security-Policy allows besides its own.
 * Addresses are built only from this fixed table of Steam app numbers, keyed by the built-in templates' keys: nothing a
 * user typed ever becomes an image address. Games that are not on Steam (Minecraft) and templates not listed here get
 * a drawn tile instead.
 */
const steamApps: Readonly<Record<string, number>> = {
  '7dtd': 251570,
  'ark-sa': 2399830,
  'arma-reforger': 1874880,
  cs2: 730,
  css: 240,
  enshrouded: 1203620,
  l4d2: 550,
  palworld: 1623730,
  'project-zomboid': 108600,
  rust: 252490,
  scpsl: 700330,
  sotf: 1326470,
  tf2: 440,
  unturned: 304930,
  valheim: 892970,
  vrising: 1604030,
};

/** A card's picture, the store header with the game's logo (460×215), or a page's wide banner (3840×1240). */
export type ArtworkKind = 'header' | 'hero';

const files: Record<ArtworkKind, string> = {
  header: 'header.jpg',
  hero: 'library_hero.jpg',
};

export const steamArtworkOrigin = 'https://shared.akamai.steamstatic.com';

export function steamArtwork(templateKey: string, kind: ArtworkKind): string | null {
  const app = Object.hasOwn(steamApps, templateKey) ? steamApps[templateKey] : undefined;
  return app === undefined ? null : `${steamArtworkOrigin}/store_item_assets/steam/apps/${app}/${files[kind]}`;
}

const tiles = [
  'from-violet-600/60 to-indigo-950',
  'from-sky-600/60 to-slate-950',
  'from-amber-600/60 to-stone-950',
  'from-rose-600/60 to-zinc-950',
  'from-teal-600/60 to-slate-950',
  'from-fuchsia-600/60 to-neutral-950',
] as const;

/** The gradient of a game's drawn tile: Minecraft's green, otherwise one picked by its key, the same every time. */
export function tileGradient(templateKey: string): string {
  if (templateKey === 'minecraft') {
    return 'from-emerald-600/70 to-green-950';
  }
  let hash = 0;
  for (const char of templateKey) {
    hash = (hash * 31 + char.charCodeAt(0)) >>> 0;
  }
  return tiles[hash % tiles.length] ?? tiles[0];
}
