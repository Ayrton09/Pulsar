/**
 * Game artwork from Steam's store CDN, and Minecraft's cover from minecraft.net (it is not on Steam): the image origins
 * the panel's Content-Security-Policy allows besides its own while the artwork is on. Addresses come only from these
 * fixed tables, keyed by the built-in templates' keys: nothing a user typed ever becomes an image address. Templates not
 * listed here get a drawn tile instead.
 */
const steamApps: Readonly<Record<string, number>> = {
  '7dtd': 251570,
  'abiotic-factor': 427410,
  'ark-sa': 2399830,
  'arma-reforger': 1874880,
  'conan-exiles': 440900,
  cs2: 730,
  css: 240,
  dst: 322330,
  enshrouded: 1203620,
  icarus: 1149460,
  kf2: 232090,
  l4d2: 550,
  palworld: 1623730,
  'project-zomboid': 108600,
  rust: 252490,
  sandstorm: 581320,
  scpsl: 700330,
  sotf: 1326470,
  soulmask: 2646460,
  'space-engineers': 244850,
  squad: 393380,
  terraria: 105600,
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

/** Minecraft's cover on its own store page (Java & Bedrock), for the cards and the page banner alike. */
const minecraftCover =
  'https://www.minecraft.net/content/dam/minecraftnet/games/minecraft/key-art/Hero-Image_Vanilla_Standard_1200x675.jpg';

/** Games that are not on Steam, by template key. */
const otherArtwork: Readonly<Record<string, string>> = {
  minecraft: minecraftCover,
  'minecraft-bedrock': minecraftCover,
};

/**
 * How a picture sits in its frame when the frame crops it: Minecraft's cover has its logo at the top, which a card's
 * wider frame would cut, so the card keeps the top and crops the bottom.
 */
export function artworkPosition(templateKey: string, kind: ArtworkKind): string {
  return kind === 'header' && Object.hasOwn(otherArtwork, templateKey) ? 'object-top' : 'object-center';
}

export function gameArtwork(templateKey: string, kind: ArtworkKind): string | null {
  if (Object.hasOwn(otherArtwork, templateKey)) {
    return otherArtwork[templateKey] ?? null;
  }
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
  if (templateKey === 'minecraft' || templateKey === 'minecraft-bedrock') {
    return 'from-emerald-600/70 to-green-950';
  }
  let hash = 0;
  for (const char of templateKey) {
    hash = (hash * 31 + char.charCodeAt(0)) >>> 0;
  }
  return tiles[hash % tiles.length] ?? tiles[0];
}
