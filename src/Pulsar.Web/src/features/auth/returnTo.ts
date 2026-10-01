/** Only same-site paths are accepted as a return address (no open redirects such as "//evil.example"). */
export function safeReturnTo(value: string | null | undefined): string {
  return value && value.startsWith('/') && !value.startsWith('//') && !value.startsWith('/\\') ? value : '/';
}
