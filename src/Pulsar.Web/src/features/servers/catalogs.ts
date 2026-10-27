import { api, unwrap } from '../../api/client';

/** What the editor shows of an id, as its catalog names it. */
export interface CatalogItem {
  id: string;
  found: boolean;
  name: string | null;
  author: string | null;
  url: string | null;
  /** The catalog knows it, as another game's. */
  otherGame?: boolean;
  version?: string | null;
  size?: number | null;
}

/** A catalog a variable lists ids of, as the panel asks it. */
export interface Catalog {
  /** Where its texts are: `servers.<texts>.*`. */
  texts: 'curseforge' | 'reforger';
  /** What its queries are cached under. */
  key: readonly unknown[];
  /** Ids a lookup takes at most. */
  lookupLimit: number;
  lookup: (ids: string[]) => Promise<CatalogItem[]>;
  search: (query: string) => Promise<{ hits: CatalogItem[]; total: number }>;
  /** The id typed or pasted, as the list writes it; null when it is none. */
  parseId: (text: string) => string | null;
  numeric: boolean;
}

/** A CurseForge project's id: a positive number without leading zeros. */
export function parseCurseForgeId(text: string): string | null {
  const id = text.trim();
  return /^[1-9][0-9]{0,9}$/.test(id) && Number(id) <= 2147483647 ? id : null;
}

/** An Arma Reforger Workshop id: 16 hexadecimal digits, written in capitals. */
export function parseReforgerId(text: string): string | null {
  const id = text.trim();
  return /^[0-9A-Fa-f]{16}$/.test(id) ? id.toUpperCase() : null;
}

/** CurseForge's projects of one game, with the administrator's key (Pulsar 1.0.14; ARK: Survival Ascended's mods). */
export function curseForgeCatalog(gameId: number): Catalog {
  return {
    texts: 'curseforge',
    key: ['curseforge', gameId],
    lookupLimit: 100,
    lookup: (ids) => unwrap(api.POST('/api/v1/curseforge/lookup', { body: { gameId, ids } })),
    search: (query) =>
      unwrap(api.GET('/api/v1/curseforge/search', { params: { query: { gameId, query, offset: 0 } } })),
    parseId: parseCurseForgeId,
    numeric: true,
  };
}

/** Arma Reforger's Workshop, read from its pages by the panel (Pulsar 1.0.14). */
export const reforgerCatalog: Catalog = {
  texts: 'reforger',
  key: ['reforger-workshop'],
  lookupLimit: 50,
  lookup: (ids) => unwrap(api.POST('/api/v1/reforger-workshop/lookup', { body: { ids } })),
  search: (query) => unwrap(api.GET('/api/v1/reforger-workshop/search', { params: { query: { query, page: 1 } } })),
  parseId: parseReforgerId,
  numeric: false,
};
