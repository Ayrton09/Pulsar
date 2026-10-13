/** Only same-site paths are accepted as a return address (no open redirects such as "//evil.example"). */
export function safeReturnTo(value: string | null | undefined): string {
  if (!value || !value.startsWith('/') || value.startsWith('//') || value.startsWith('/\\')) {
    return '/';
  }
  // Browsers drop tabs and line breaks from a URL, so "/<tab>/evil.example" would be "//evil.example".
  if (/[\t\r\n]/.test(value)) {
    return '/';
  }
  // Whatever else resolves away from this site is refused too.
  try {
    return new URL(value, globalThis.location.origin).origin === globalThis.location.origin ? value : '/';
  } catch {
    return '/';
  }
}
