/** Steam's own page of an item. */
export function workshopUrl(id: string) {
  return `https://steamcommunity.com/sharedfiles/filedetails/?id=${id}`;
}

/** An id, or the id in a Workshop link ("…/filedetails/?id=123"); null for anything else. */
export function parseWorkshopId(text: string): string | null {
  const trimmed = text.trim();
  const id = /[?&]id=(\d+)/.exec(trimmed)?.[1] ?? (/^\d+$/.test(trimmed) ? trimmed : null);
  return id && id.length <= 20 && !id.startsWith('0') ? id : null;
}
