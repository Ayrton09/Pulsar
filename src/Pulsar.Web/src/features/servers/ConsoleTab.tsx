import { FitAddon } from '@xterm/addon-fit';
import { SearchAddon } from '@xterm/addon-search';
import { WebLinksAddon } from '@xterm/addon-web-links';
import { Terminal } from '@xterm/xterm';
import '@xterm/xterm/css/xterm.css';
import {
  ArrowDownToLine,
  Check,
  Copy,
  Download,
  Maximize2,
  Minimize2,
  Pause,
  Play,
  Terminal as TerminalIcon,
  ZoomIn,
  ZoomOut,
} from 'lucide-react';
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
import { copySelection } from './consoleCopy';
import { ConsoleFilter } from './consoleFilter';
import {
  complete,
  consoleFileName,
  consoleText,
  FONT_SIZES,
  HISTORY,
  loadFontSize,
  loadHistory,
  saveFontSize,
  saveHistory,
} from './consoleTools';
import { commandRefusal } from './serverStatus';

const SCROLLBACK = 5_000;
const MAX_LENGTH = 512;
/** Writes held while the console is paused; beyond them the oldest go, marked as lost. */
const HELD = 2_000;

/**
 * The server's console (docs/architecture/07, "Console"): xterm writes what the panel streams, outside React state (a
 * render per line would be far too slow). Commands go through a separate field with history, shown only to users who
 * may type them. "Clear" only clears this view. The reader can pause it (what comes meanwhile is held, then written),
 * copy or download all it holds, make it full screen and change its text size (kept in this browser).
 */
export function ConsoleTab({ details }: { details: ServerDetails }) {
  const { t } = useTranslation();
  const me = useCurrentUser();
  const status = useLiveStatus();
  const { server } = details;
  const container = useRef<HTMLDivElement>(null);
  const terminal = useRef<Terminal | null>(null);
  const search = useRef<SearchAddon | null>(null);
  const fitter = useRef<FitAddon | null>(null);
  const card = useRef<HTMLDivElement>(null);
  const [filter] = useState(() => new ConsoleFilter());
  const [query, setQuery] = useState('');
  const [fontSize, setFontSize] = useState(loadFontSize);
  const [atEnd, setAtEnd] = useState(true);
  const [fullScreen, setFullScreen] = useState(false);
  const [copied, setCopied] = useState(false);
  // While paused, what comes is held here and written on resume.
  const held = useRef<string[]>([]);
  const pausedRef = useRef(false);
  const [paused, setPaused] = useState(false);
  const [heldCount, setHeldCount] = useState(0);

  useEffect(() => {
    const term = new Terminal({
      convertEol: true, // log files end lines with \n alone
      scrollback: SCROLLBACK,
      disableStdin: true,
      cursorStyle: 'underline',
      cursorInactiveStyle: 'none',
      fontFamily: 'ui-monospace, SFMono-Regular, Consolas, "Liberation Mono", monospace',
      fontSize: loadFontSize(),
      // A terminal: dark in either theme of the panel, as the game's own console is.
      theme: { background: '#07090d', foreground: '#d6dde6' },
    });
    const fit = new FitAddon();
    const finder = new SearchAddon();
    term.loadAddon(fit);
    term.loadAddon(finder);
    term.loadAddon(new WebLinksAddon((_event, uri) => openWebLink(uri)));
    // No clipboard addon: a game must not be able to write to the user's clipboard (OSC 52). The reader copies what
    // they selected with Ctrl+C (Cmd+C).
    term.attachCustomKeyEventHandler((event) => !copySelection(event, term));
    // Whether the reader is at the end: the terminal follows new output only there.
    const scrolled = term.onScroll(() => {
      const buffer = term.buffer.active;
      setAtEnd(buffer.viewportY >= buffer.baseY);
    });
    const element = container.current;
    const observer = new ResizeObserver(() => fit.fit());
    if (element) {
      term.open(element);
      fit.fit();
      observer.observe(element);
    }
    terminal.current = term;
    search.current = finder;
    fitter.current = fit;
    return () => {
      scrolled.dispose();
      observer.disconnect();
      term.dispose();
      terminal.current = null;
      search.current = null;
      fitter.current = null;
    };
  }, []);

  useEffect(() => {
    const term = terminal.current;
    if (term) {
      term.options.fontSize = fontSize;
      fitter.current?.fit();
    }
    saveFontSize(fontSize);
  }, [fontSize]);

  useEffect(() => {
    const changed = () => setFullScreen(document.fullscreenElement === card.current && card.current !== null);
    document.addEventListener('fullscreenchange', changed);
    return () => document.removeEventListener('fullscreenchange', changed);
  }, []);

  const show = useCallback((text: string) => {
    if (!pausedRef.current) {
      terminal.current?.write(text);
      return;
    }
    held.current.push(text);
    if (held.current.length > HELD) {
      held.current.splice(0, held.current.length - HELD);
    }
    setHeldCount(held.current.length);
  }, []);

  const togglePause = () => {
    if (pausedRef.current) {
      pausedRef.current = false;
      const waiting = held.current.join('');
      held.current = [];
      setHeldCount(0);
      terminal.current?.write(waiting);
      terminal.current?.scrollToBottom();
    } else {
      pausedRef.current = true;
    }
    setPaused(pausedRef.current);
  };

  const copyAll = () => {
    const term = terminal.current;
    if (!term) {
      return;
    }
    void navigator.clipboard
      ?.writeText(consoleText(term))
      .then(() => {
        setCopied(true);
        window.setTimeout(() => setCopied(false), 2_000);
      })
      .catch(() => undefined);
  };

  const download = () => {
    const term = terminal.current;
    if (!term) {
      return;
    }
    const url = URL.createObjectURL(new Blob([consoleText(term)], { type: 'text/plain;charset=utf-8' }));
    const link = document.createElement('a');
    link.href = url;
    link.download = consoleFileName(server.shortId, new Date());
    link.click();
    URL.revokeObjectURL(url);
  };

  const toggleFullScreen = () => {
    if (document.fullscreenElement) {
      void document.exitFullscreen().catch(() => undefined);
    } else {
      void card.current?.requestFullscreen?.().catch(() => undefined);
    }
  };

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
        show(
          entry.isGap ? `\r\n\x1b[2m—— ${t('servers.console.gap')} ——\x1b[0m\r\n` : filter.push(entry.text, dropped),
        );
      }
    },
    [filter, show, t],
  );
  const stream = useServerConsole(server.id, write);
  const running = server.runtimeState === 'starting' || server.runtimeState === 'running';

  return (
    <section aria-label={t('servers.console.title')} className="space-y-3">
      {status === 'reconnecting' && <Alert>{t('servers.console.reconnecting')}</Alert>}
      {stream.refused && <Alert>{t('servers.console.refused')}</Alert>}
      {stream.failed && <Alert>{t('servers.console.failed')}</Alert>}
      {!server.nodeConnected && <p className="text-sm text-muted">{t('servers.console.nodeOffline')}</p>}
      <div
        ref={card}
        className={`overflow-hidden border border-border bg-surface shadow-xl shadow-black/30 ${
          fullScreen ? 'flex h-screen flex-col' : 'rounded-2xl'
        }`}
      >
        <div className="flex flex-wrap items-center gap-2 border-b border-border px-3 py-2">
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
          <span className="flex items-center gap-1">
            <Button
              variant="secondary"
              size="icon"
              icon={paused ? Play : Pause}
              aria-label={paused ? t('servers.console.resume') : t('servers.console.pause')}
              title={paused ? t('servers.console.resume') : t('servers.console.pause')}
              aria-pressed={paused}
              onClick={togglePause}
            />
            <Button
              variant="secondary"
              size="icon"
              icon={copied ? Check : Copy}
              aria-label={t('servers.console.copyAll')}
              title={copied ? t('servers.console.copied') : t('servers.console.copyAll')}
              onClick={copyAll}
            />
            <Button
              variant="secondary"
              size="icon"
              icon={Download}
              aria-label={t('servers.console.download')}
              title={t('servers.console.download')}
              onClick={download}
            />
            <Button
              variant="secondary"
              size="icon"
              icon={ZoomOut}
              aria-label={t('servers.console.smaller')}
              title={t('servers.console.smaller')}
              disabled={fontSize <= FONT_SIZES.min}
              onClick={() => setFontSize((size) => Math.max(FONT_SIZES.min, size - 1))}
            />
            <Button
              variant="secondary"
              size="icon"
              icon={ZoomIn}
              aria-label={t('servers.console.larger')}
              title={t('servers.console.larger')}
              disabled={fontSize >= FONT_SIZES.max}
              onClick={() => setFontSize((size) => Math.min(FONT_SIZES.max, size + 1))}
            />
            <Button
              variant="secondary"
              size="icon"
              icon={fullScreen ? Minimize2 : Maximize2}
              aria-label={fullScreen ? t('servers.console.exitFullScreen') : t('servers.console.fullScreen')}
              title={fullScreen ? t('servers.console.exitFullScreen') : t('servers.console.fullScreen')}
              onClick={toggleFullScreen}
            />
          </span>
        </div>
        {copied && (
          <p role="status" className="sr-only">
            {t('servers.console.copied')}
          </p>
        )}
        <div className={`relative bg-[#07090d] p-3 ${fullScreen ? 'min-h-0 flex-1' : 'h-[max(30rem,60vh)]'}`}>
          <div ref={container} data-testid="console-output" className="size-full overflow-hidden" />
          {(paused || !atEnd) && (
            <div className="absolute right-4 bottom-4 flex items-center gap-2">
              {paused && (
                <span role="status" className="rounded-md bg-surface/90 px-2 py-1 text-xs text-muted">
                  {t('servers.console.held', { count: heldCount })}
                </span>
              )}
              {!paused && (
                <Button size="sm" icon={ArrowDownToLine} onClick={() => terminal.current?.scrollToBottom()}>
                  {t('servers.console.toEnd')}
                </Button>
              )}
            </div>
          )}
        </div>
        {canOnServer(me, server, Permission.ServerConsoleWrite) && (
          <div className="border-t border-border p-2">
            <CommandInput serverId={server.id} userId={me.id} running={running} connected={server.nodeConnected} />
          </div>
        )}
      </div>
    </section>
  );
}

/**
 * The command field: Enter sends, ↑/↓ walk through what was sent before, Tab completes from it. What was sent is kept
 * in this browser for this user and server, never a command that may carry a secret.
 */
function CommandInput({
  serverId,
  userId,
  running,
  connected,
}: {
  serverId: string;
  userId: string;
  running: boolean;
  connected: boolean;
}) {
  const { t } = useTranslation();
  const send = useConsoleCommand();
  const [line, setLine] = useState('');
  const [history, setHistory] = useState<string[]>(() => loadHistory(userId, serverId));
  const [position, setPosition] = useState<number | null>(null);
  // What Tab completes: the text typed before the first Tab, and how many times it was pressed since.
  const [completing, setCompleting] = useState<{ typed: string; turn: number } | null>(null);
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
            setHistory((previous) => {
              const next = [...previous.filter((h) => h !== command), command].slice(-HISTORY);
              saveHistory(userId, serverId, next);
              return next;
            });
            setLine('');
            setPosition(null);
            setCompleting(null);
          }
        },
      },
    );
  };

  const browse = (event: KeyboardEvent<HTMLInputElement>) => {
    if (event.key === 'Tab' && !event.shiftKey && line.trim() !== '') {
      const typed = completing?.typed ?? line;
      const turn = completing ? completing.turn + 1 : 0;
      const completed = complete(typed, history, turn);
      if (completed !== null) {
        event.preventDefault();
        setCompleting({ typed, turn });
        setLine(completed);
      }
      return;
    }
    if (event.key !== 'ArrowUp' && event.key !== 'ArrowDown') {
      return;
    }
    setCompleting(null);
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
          onChange={(e) => {
            setLine(commandLine(e.target.value));
            setCompleting(null);
          }}
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
