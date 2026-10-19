import { FitAddon } from '@xterm/addon-fit';
import { SearchAddon } from '@xterm/addon-search';
import { WebLinksAddon } from '@xterm/addon-web-links';
import { Terminal } from '@xterm/xterm';
import '@xterm/xterm/css/xterm.css';
import { Terminal as TerminalIcon } from 'lucide-react';
import { useCallback, useEffect, useRef, useState, type FormEvent, type KeyboardEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from '../../components/ui/Button';
import { Alert } from '../../components/ui/Display';
import { Input } from '../../components/ui/Form';
import { commandLine } from '../../lib/commandLine';
import { errorMessage } from '../../lib/errors';
import { canOnServer, Permission } from '../../lib/permissions';
import { useCurrentUser } from '../auth/api';
import { useLiveStatus, useServerConsole } from '../live/hooks';
import type { ConsoleEntry } from '../live/types';
import { useConsoleCommand, type ServerDetails } from './api';
import { ConsoleFilter } from './consoleFilter';
import { commandRefusal } from './serverStatus';

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
  const [filter] = useState(() => new ConsoleFilter());
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
      theme: { background: '#07090d', foreground: '#d6dde6' },
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
      // Dimmed, in the reader's language (the current one: it may have changed since the tab opened), where a
      // sequence that never ended was dropped.
      const dropped = `\x1b[2m[${t('servers.console.dropped')}]\x1b[0m`;
      for (const entry of entries) {
        if (entry.isGap) {
          filter.reset();
        }
        // Game output is filtered: only text and colours reach the terminal (no clearing or cursor moves).
        terminal.current?.write(
          entry.isGap ? `\r\n\x1b[2m—— ${t('servers.console.gap')} ——\x1b[0m\r\n` : filter.push(entry.text, dropped),
        );
      }
    },
    [filter, t],
  );
  const stream = useServerConsole(server.id, write);
  const running = server.runtimeState === 'starting' || server.runtimeState === 'running';

  return (
    <section aria-label={t('servers.console.title')} className="space-y-3">
      {status === 'reconnecting' && <Alert>{t('servers.console.reconnecting')}</Alert>}
      {stream.refused && <Alert>{t('servers.console.refused')}</Alert>}
      {stream.failed && <Alert>{t('servers.console.failed')}</Alert>}
      {!server.nodeConnected && <p className="text-sm text-muted">{t('servers.console.nodeOffline')}</p>}
      <div className="overflow-hidden rounded-2xl border border-border bg-[#07090d] shadow-xl shadow-black/30">
        <div className="flex flex-wrap items-center gap-2 border-b border-border bg-surface/80 px-3 py-2">
          <span className="mr-auto flex items-center gap-2 text-sm font-medium">
            <TerminalIcon aria-hidden="true" className="size-4 text-accent" />
            {t('servers.console.title')}
          </span>
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
          <Button variant="secondary" size="sm" onClick={() => terminal.current?.clear()}>
            {t('servers.console.clear')}
          </Button>
        </div>
        <div className="h-[30rem] bg-[#07090d] p-3">
          <div ref={container} data-testid="console-output" className="size-full overflow-hidden" />
        </div>
        {canOnServer(me, server, Permission.ServerConsoleWrite) && (
          <div className="border-t border-border bg-surface/60 p-2">
            <CommandInput serverId={server.id} running={running} connected={server.nodeConnected} />
          </div>
        )}
      </div>
    </section>
  );
}

/** The command field: Enter sends, ↑/↓ walk through what was sent before (this page only). */
function CommandInput({ serverId, running, connected }: { serverId: string; running: boolean; connected: boolean }) {
  const { t } = useTranslation();
  const send = useConsoleCommand();
  const [line, setLine] = useState('');
  const [history, setHistory] = useState<string[]>([]);
  const [position, setPosition] = useState<number | null>(null);
  const ready = running && connected;

  const submit = (event: FormEvent) => {
    event.preventDefault();
    const command = line.trim();
    if (!command || !ready) {
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
          placeholder={
            !connected
              ? t('servers.nodeDisconnected')
              : running
                ? t('servers.console.commandPlaceholder')
                : t('servers.console.notRunning')
          }
          className="font-mono"
          maxLength={MAX_LENGTH}
          autoComplete="off"
          spellCheck={false}
          disabled={!ready}
          value={line}
          onChange={(e) => setLine(commandLine(e.target.value))}
          onKeyDown={browse}
        />
        <Button type="submit" disabled={!ready || !line.trim()} busy={send.isPending}>
          {t('servers.console.send')}
        </Button>
      </div>
      {send.isError && <Alert>{errorMessage(t, send.error)}</Alert>}
      {refused && (
        <p role="status" className="text-sm text-danger">
          {commandRefusal(t, send.data.status, send.data.errorCode)}
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
