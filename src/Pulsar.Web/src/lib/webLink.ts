/**
 * The address as a link when it is a web page (http or https); anything else (javascript:, data:, file:, not a URL) is
 * none, so a link built from what a manifest, a catalog or another server sent can only open a web page.
 */
export function webLink(address: string | null | undefined): string | undefined {
  if (!address) {
    return undefined;
  }

  try {
    const url = new URL(address);
    return url.protocol === 'http:' || url.protocol === 'https:' ? url.href : undefined;
  } catch {
    return undefined; // not a URL
  }
}
