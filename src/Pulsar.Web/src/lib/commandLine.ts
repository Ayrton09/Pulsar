/**
 * A console command is one line without control characters (the panel refuses them): a pasted tab becomes a space,
 * other control characters are dropped.
 */
export function commandLine(text: string): string {
  // eslint-disable-next-line no-control-regex
  return text.replace(/\t/g, ' ').replace(/[\u0000-\u001f\u007f-\u009f]/g, '');
}
