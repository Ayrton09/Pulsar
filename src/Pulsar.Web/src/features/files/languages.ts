import { json } from '@codemirror/lang-json';
import { StreamLanguage, type LanguageSupport, type StreamParser } from '@codemirror/language';
import { lua } from '@codemirror/legacy-modes/mode/lua';
import { powerShell } from '@codemirror/legacy-modes/mode/powershell';
import { properties } from '@codemirror/legacy-modes/mode/properties';
import { shell } from '@codemirror/legacy-modes/mode/shell';
import { toml } from '@codemirror/legacy-modes/mode/toml';
import { xml } from '@codemirror/legacy-modes/mode/xml';
import { yaml } from '@codemirror/legacy-modes/mode/yaml';
import type { Extension } from '@codemirror/state';

/**
 * Source engine configuration and KeyValues files (server.cfg, gamemode configs, .vdf): a command or key first, then
 * its values; // comments; quoted strings; several commands on a line separated by semicolons.
 */
const sourceConfig: StreamParser<{ first: boolean }> = {
  name: 'source-cfg',
  startState: () => ({ first: true }),
  token(stream, state) {
    if (stream.sol()) {
      state.first = true;
    }
    if (stream.eatSpace()) {
      return null;
    }
    if (stream.match('//')) {
      stream.skipToEnd();
      return 'comment';
    }
    const next = stream.peek();
    if (next === ';') {
      stream.next();
      state.first = true;
      return null;
    }
    if (next === '{' || next === '}') {
      stream.next();
      state.first = true;
      return 'bracket';
    }
    const first = state.first;
    state.first = false;
    if (next === '"') {
      stream.next();
      while (!stream.eol() && stream.next() !== '"') {
        // to the closing quote
      }
      return first ? 'keyword' : 'string';
    }
    stream.match(/^[^\s";{}]+/);
    if (first) {
      return 'keyword';
    }
    return /^-?\d+(\.\d+)?$/.test(stream.current()) ? 'number' : 'string';
  },
  languageData: { commentTokens: { line: '//' } },
};

const languages: Record<string, () => LanguageSupport | StreamLanguage<unknown>> = {
  json: () => json(),
  cfg: () => StreamLanguage.define(sourceConfig),
  vdf: () => StreamLanguage.define(sourceConfig),
  acf: () => StreamLanguage.define(sourceConfig),
  res: () => StreamLanguage.define(sourceConfig),
  vmt: () => StreamLanguage.define(sourceConfig),
  kv: () => StreamLanguage.define(sourceConfig),
  ini: () => StreamLanguage.define(properties),
  properties: () => StreamLanguage.define(properties),
  conf: () => StreamLanguage.define(properties),
  toml: () => StreamLanguage.define(toml),
  yaml: () => StreamLanguage.define(yaml),
  yml: () => StreamLanguage.define(yaml),
  xml: () => StreamLanguage.define(xml),
  lua: () => StreamLanguage.define(lua),
  sh: () => StreamLanguage.define(shell),
  ps1: () => StreamLanguage.define(powerShell),
};

/** The syntax highlighting for a file name; none for formats the editor does not know. */
export function languageFor(name: string): Extension {
  const extension = name.includes('.') ? name.slice(name.lastIndexOf('.') + 1).toLowerCase() : '';
  return languages[extension]?.() ?? [];
}
