import { FitAddon } from '@xterm/addon-fit';
import { SearchAddon } from '@xterm/addon-search';
import { WebLinksAddon } from '@xterm/addon-web-links';
import { Terminal } from '@xterm/xterm';
import '@xterm/xterm/css/xterm.css';
import { useCallback, useEffect, useRef, useState, type FormEvent, type KeyboardEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from '../../components/ui/Button';
import { Alert } from '../../components/ui/Display';
import { Input } from '../../components/ui/Form';
import { errorMessage } from '../../lib/errors';
import { can, Permission } from '../../lib/permissions';
import { useCurrentUser } from '../auth/api';
import { useLiveStatus, useServerConsole } from '../live/hooks';
import type { ConsoleEntry } from '../live/types';
import { useConsoleCommand, type ServerDetails } from './api';

const SCROLLBACK = 5_000;
const HISTORY = 50;
const MAX_LENGTH = 512;

/**
 * The server's console (docs/architecture/07, "Console"): xterm writes what the panel streams, outside React state (a
 * render per line would be far too slow). Commands go through a separate field with history, shown only to users who
 * may type them. "Clear" only clears this view.
 */
export function ConsoleTab({ details }: { details: ServerDetails }) {
  const { t } = useTranslation();
  const me = useCurrentUser();
  const status = useLiveStatus();
  const { server } = details;
  const container = useRef<HTMLDivElement>(null);
  const terminal = useRef<Terminal | null>(null);
  const search = useRef<SearchAddon | null>(null);
  const [query, setQuery] = useState('');

  useEffect(() => {
    const term = new Terminal({
      convertEol: true, // log files end lines with \n alone
      scrollback: SCROLLBACK,
      disableStdin: true,
      cursorStyle: 'underline',
      cursorInactiveStyle: 'none',
      fontFamily: 'ui-monospace, SFMono-Regular, Consolas, "Liberation Mono", monospace',
      fontSize: 13,
      theme: { background: '#0b0f14', foreground: '#d6dde6' },
      // Links written by the game (OSC 8) open only when they are web links, never in this tab.
      linkHandler: { activate: (_event, uri) => openWebLink(uri), allowNonHttpProtocols: false },
    });
    const fit = new FitAddon();
    const finder = new SearchAddon();
    term.loadAddon(fit);
    term.loadAddon(finder);
    term.loadAddon(new WebLinksAddon((_event, uri) => openWebLink(uri)));
    // No clipboard addon: a game must not be able to write to the user's clipboard (OSC 52).
    const element = container.current;
    const observer = new ResizeObserver(() => fit.fit());
    if (element) {
      term.open(element);
      fit.fit();
      observer.observe(element);
    }
    terminal.current = term;
    search.current = finder;
    return () => {
      observer.disconnect();
      term.dispose();
      terminal.current = null;
      search.current = null;
    };
  }, []);

  const write = useCallback(
    (entries: ConsoleEntry[]) => {
      for (const entry of entries) {
        terminal.current?.write(entry.isGap ? `\r\n\x1b[2m—— ${t('servers.console.gap')} ——\x1b[0m\r\n` : entry.text);
      }
    },
    [t],
  );
  const stream = useServerConsole(server.id, write);
  const running = server.runtimeState === 'starting' || server.runtimeState === 'running';

  return (
    <section aria-label={t('servers.console.title')} className="space-y-3">
      {status === 'reconnecting' && <Alert>{t('servers.console.reconnecting')}</Alert>}
      {stream.refused && <Alert>{t('servers.console.refused')}</Alert>}
      {!server.nodeConnected && <p className="text-sm text-muted">{t('servers.console.nodeOffline')}</p>}
      <div className="flex flex-wrap items-center gap-2">
        <Input
          type="search"
          aria-label={t('servers.console.search')}
          placeholder={t('servers.console.search')}
          className="max-w-xs"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === 'Enter' && query) {
              e.preventDefault();
              if (e.shiftKey) {
                search.current?.findPrevious(query);
              } else {
                search.current?.findNext(query);
              }
            }
          }}
        />
        <Button variant="secondary" size="sm" disabled={!query} onClick={() => search.current?.findPrevious(query)}>
          {t('servers.console.previous')}
        </Button>
        <Button variant="secondary" size="sm" disabled={!query} onClick={() => search.current?.findNext(query)}>
          {t('servers.console.next')}
        </Button>
        <Button variant="secondary" size="sm" className="ml-auto" onClick={() => terminal.current?.clear()}>
          {t('servers.console.clear')}
        </Button>
      </div>
      <div
        ref={container}
        data-testid="console-output"
        className="h-[28rem] overflow-hidden rounded-md border border-border bg-[#0b0f14] p-2"
      />
      {can(me, Permission.ServerConsoleWrite) && <CommandInput serverId={server.id} running={running} />}
    </section>
  );
}

/** The command field: Enter sends, ↑/↓ walk through what was sent before (this page only). */
function CommandInput({ serverId, running }: { serverId: string; running: boolean }) {
  const { t } = useTranslation();
  const send = useConsoleCommand();
  const [line, setLine] = useState('');
  const [history, setHistory] = useState<string[]>([]);
  const [position, setPosition] = useState<number | null>(null);

  const submit = (event: FormEvent) => {
    event.preventDefault();
    const command = line.trim();
    if (!command || !running) {
      return;
    }
    send.mutate(
      { id: serverId, command },
      {
        onSuccess: (result) => {
          if (result.status === 'accepted') {
            setHistory((previous) => [...previous.filter((h) => h !== command), command].slice(-HISTORY));
            setLine('');
            setPosition(null);
          }
        },
      },
    );
  };

  const browse = (event: KeyboardEvent<HTMLInputElement>) => {
    if (event.key !== 'ArrowUp' && event.key !== 'ArrowDown') {
      return;
    }
    event.preventDefault();
    if (history.length === 0) {
      return;
    }
    const next =
      event.key === 'ArrowUp'
        ? Math.max(0, (position ?? history.length) - 1)
        : position === null
          ? null
          : position + 1 >= history.length
            ? null
            : position + 1;
    setPosition(next);
    setLine(next === null ? '' : (history[next] ?? ''));
  };

  const refused = send.data && send.data.status !== 'accepted';
  return (
    <form onSubmit={submit} className="space-y-2">
      <div className="flex gap-2">
        <Input
          aria-label={t('servers.console.command')}
          placeholder={running ? t('servers.console.commandPlaceholder') : t('servers.console.notRunning')}
          className="font-mono"
          maxLength={MAX_LENGTH}
          autoComplete="off"
          spellCheck={false}
          disabled={!running}
          value={line}
          onChange={(e) => setLine(e.target.value)}
          onKeyDown={browse}
        />
        <Button type="submit" disabled={!running || !line.trim()} busy={send.isPending}>
          {t('servers.console.send')}
        </Button>
      </div>
      {send.isError && <Alert>{errorMessage(t, send.error)}</Alert>}
      {refused && (
        <p role="status" className="text-sm text-danger">
          {t('servers.commandRefused', { status: send.data.status, code: send.data.errorCode ?? '' })}
        </p>
      )}
    </form>
  );
}

/** Web links open in a new tab without access to this one; anything else (file:, javascript:, …) is ignored. */
function openWebLink(uri: string) {
  try {
    const url = new URL(uri);
    if (url.protocol === 'http:' || url.protocol === 'https:') {
      window.open(url.href, '_blank', 'noopener,noreferrer');
    }
  } catch {
    // Not a URL.
  }
}
