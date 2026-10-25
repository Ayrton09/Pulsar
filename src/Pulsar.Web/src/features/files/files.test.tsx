import { defaultKeymap, toggleTabFocusMode } from '@codemirror/commands';
import { QueryClient } from '@tanstack/react-query';
import { act, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter } from 'react-router';
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import type { Schemas } from '../../api/client';
import { App } from '../../app/App';
import { initI18n } from '../../app/i18n';
import { routes } from '../../app/routes';
import { adminUser, fakeApi, json, problem, session } from '../../test/fakeApi';
import { connections, openedConnection } from '../../test/fakeSignalR';
import { breadcrumbs, nameProblem, visibleName } from './paths';
import { decodeText, lineEndings } from './text';

// CodeMirror needs a real layout: the tests type into a plain text area with the same contract.
vi.mock('./CodeEditor', () => ({
  default: ({
    label,
    initial,
    readOnly,
    describedBy,
    onChange,
    onSave,
  }: {
    label: string;
    initial: string;
    readOnly: boolean;
    describedBy?: string;
    onChange: (text: string) => void;
    onSave: () => void;
  }) => (
    <textarea
      aria-label={label}
      aria-describedby={describedBy}
      defaultValue={initial}
      readOnly={readOnly}
      onChange={(e) => onChange(e.target.value)}
      onKeyDown={(e) => {
        if (e.ctrlKey && e.key === 's') {
          e.preventDefault();
          onSave();
        }
      }}
    />
  ),
}));

const serverId = '0192f1a0-0000-7000-8000-0000000000e1';
const editor = { ...adminUser, permissions: ['server.view', 'server.files.view', 'server.files.edit'] };
const viewer = { ...adminUser, permissions: ['server.view', 'server.files.view'] };

const server: Schemas['ServerSummary'] = {
  id: serverId,
  shortId: 'k3x7m2qa',
  name: 'Retakes',
  nodeId: '0192f1a0-0000-7000-8000-0000000000a1',
  nodeName: 'Buenos Aires 1',
  nodeConnected: true,
  templateId: '0192f1a0-0000-7000-8000-0000000000f1',
  templateName: 'Counter-Strike 2',
  templateKey: 'cs2',
  gameAddress: null,
  gameUpdateAvailable: false,
  installState: 'installed',
  runtimeState: 'offline',
  desiredState: 'stopped',
  lastStateReason: null,
  lastExitCode: null,
  lastStartedAt: null,
  lastStoppedAt: null,
  activeOperation: null,
  createdAt: '2026-10-02T12:00:00Z',
};

const details: Schemas['ServerDetails'] = {
  server,
  description: null,
  autoStart: false,
  restartOnCrash: true,
  templateKey: 'cs2',
  templateVersion: '1.0.0',
  canUpdate: true,
  ports: [],
  recentOperations: [],
  limits: { cpuPercent: null, memoryMb: null, databases: 2 },
  hasAddons: false,
};

const at = '2026-10-03T10:00:00Z';
const listings: Record<string, Schemas['FileEntry'][]> = {
  '': [
    { name: 'game', kind: 'directory', size: 0, modifiedAt: at },
    { name: 'logs', kind: 'link', size: 0, modifiedAt: at },
    { name: 'server.cfg', kind: 'file', size: 18, modifiedAt: at },
    { name: 'de_dust2.bsp', kind: 'file', size: 80 * 1024 * 1024, modifiedAt: at },
  ],
  game: [{ name: 'csgo', kind: 'directory', size: 0, modifiedAt: at }],
  // A name a game or a player chose: the direction override would show it as "invoiceexe.pdf".
  odd: [{ name: `invoice${String.fromCodePoint(0x202e)}fdp.exe`, kind: 'file', size: 4, modifiedAt: at }],
  // Folders the panel could not list whole: one over the listing's cap, one with names it cannot open.
  capped: [{ name: 'a.cfg', kind: 'file', size: 1, modifiedAt: at }],
  hidden: [{ name: 'b.cfg', kind: 'file', size: 1, modifiedAt: at }],
};
const incomplete: Record<string, { truncated: boolean; skipped: number }> = {
  capped: { truncated: true, skipped: 0 },
  hidden: { truncated: false, skipped: 3 },
};

function listing(request: Request): Response {
  const path = new URL(request.url).searchParams.get('path') ?? '';
  const entries = listings[path];
  return entries
    ? json({ path, entries, ...(incomplete[path] ?? { truncated: false, skipped: 0 }) })
    : problem(404, 'files.not_found');
}

/** XMLHttpRequest stand-in: uploads and saves go through it (fetch cannot report upload progress). */
class FakeXhr {
  static sent: FakeXhr[] = [];
  static handler: (xhr: FakeXhr) => Promise<{ status: number; body: unknown }> = async () => ({
    status: 500,
    body: undefined,
  });

  method = '';
  url = '';
  headers: Record<string, string> = {};
  body: Blob | null = null;
  status = 0;
  responseText = '';
  withCredentials = false;
  upload: { onprogress: ((event: { lengthComputable: boolean; loaded: number; total: number }) => void) | null } = {
    onprogress: null,
  };
  onload: (() => void) | null = null;
  onerror: (() => void) | null = null;
  onabort: (() => void) | null = null;

  open(method: string, url: string) {
    this.method = method;
    this.url = url;
  }

  setRequestHeader(name: string, value: string) {
    this.headers[name] = value;
  }

  send(body: Blob) {
    this.body = body;
    FakeXhr.sent.push(this);
    void FakeXhr.handler(this).then(({ status, body: answer }) => {
      this.upload.onprogress?.({ lengthComputable: true, loaded: body.size, total: body.size });
      this.status = status;
      this.responseText = answer === undefined ? '' : JSON.stringify(answer);
      this.onload?.();
    });
  }

  abort() {
    this.onabort?.();
  }

  get path() {
    return new URL(this.url, 'http://localhost').searchParams.get('path');
  }
}

function written(path: string, size: number, version = 'v2'): { status: number; body: unknown } {
  return { status: 200, body: { entry: { name: path.split('/').pop(), kind: 'file', size, modifiedAt: at }, version } };
}

let queryClient: QueryClient;

function renderAt(path: string, staleTime = 0) {
  const router = createMemoryRouter(routes, { initialEntries: [path] });
  queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false, refetchInterval: false, staleTime } },
  });
  render(<App router={router} queryClient={queryClient} />);
  return router;
}

beforeAll(async () => {
  await initI18n('en-US');
});

beforeEach(() => {
  FakeXhr.sent = [];
  vi.stubGlobal('XMLHttpRequest', FakeXhr);
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('file paths and text', () => {
  it.each([
    ['', 'empty'],
    ['a/b', 'characters'],
    ['a\\b', 'characters'],
    ['x:y', 'characters'],
    ['what?', 'characters'],
    ['..', 'dots'],
    ['name.', 'dots'],
    [' name', 'dots'],
    ['CON', 'reserved'],
    ['nul.txt', 'reserved'],
    ['com1.cfg', 'reserved'],
    ['x'.repeat(256), 'length'],
    ['server.cfg', null],
    ['.pulsar', null],
    ['COM10', null],
  ] as const)('judges the name %j as %s', (name, expected) => {
    expect(nameProblem(name)).toBe(expected);
  });

  it('shows the characters that would hide in a name or reorder it, and leaves the ones scripts need', () => {
    const c = (code: number) => String.fromCodePoint(code);
    expect(visibleName(`invoice${c(0x202e)}fdp.exe`)).toBe('invoice[U+202E]fdp.exe');
    expect(visibleName(`a${c(0x2066)}b${c(0x2069)}`)).toBe('a[U+2066]b[U+2069]');
    expect(visibleName(`ab${c(0x200b)}c${c(0xfeff)}`)).toBe('ab[U+200B]c[U+FEFF]');
    // Every other format character, the line and paragraph separators and the invisible fillers.
    expect(visibleName(`a${c(0x061c)}b`)).toBe('a[U+061C]b');
    expect(visibleName(`a${c(0xe0041)}b`)).toBe('a[U+E0041]b');
    expect(visibleName(`a${c(0x00ad)}b`)).toBe('a[U+00AD]b');
    expect(visibleName(`a${c(0x3164)}b${c(0x115f)}${c(0x1160)}`)).toBe('a[U+3164]b[U+115F][U+1160]');
    expect(visibleName(`a${c(0x2028)}b${c(0x2029)}`)).toBe('a[U+2028]b[U+2029]');
    expect(visibleName(`a${c(0x180e)}b${c(0x206a)}${c(0x206f)}`)).toBe('a[U+180E]b[U+206A][U+206F]');
    // A family emoji (joined by ZWJ), Persian with a ZWNJ and Hebrew with a right-to-left mark are names as they are.
    const family = `${c(0x1f468)}${c(0x200d)}${c(0x1f469)}${c(0x200d)}${c(0x1f467)}`;
    const persian = `${c(0x0645)}${c(0x06cc)}${c(0x200c)}${c(0x0631)}${c(0x0648)}${c(0x0645)}`;
    const hebrew = `${c(0x05e9)}${c(0x05dc)}${c(0x05d5)}${c(0x05dd)}${c(0x200f)}.txt`;
    for (const name of [family, persian, hebrew, 'server.cfg']) {
      expect(visibleName(name)).toBe(name);
    }
  });

  it('splits a path into breadcrumbs', () => {
    expect(breadcrumbs('')).toEqual([]);
    expect(breadcrumbs('game/csgo')).toEqual([
      { name: 'game', path: 'game' },
      { name: 'csgo', path: 'game/csgo' },
    ]);
  });

  it('opens UTF-8 text, keeps a byte order mark and refuses binary files', () => {
    const encoder = new TextEncoder();
    expect(decodeText(encoder.encode('hostname "x"'))).toEqual({ text: 'hostname "x"', bom: false });
    expect(decodeText(new Uint8Array([0xef, 0xbb, 0xbf, ...encoder.encode('sv_cheats 0')]))).toEqual({
      text: 'sv_cheats 0',
      bom: true,
    });
    expect(decodeText(new Uint8Array([0x4d, 0x5a, 0x90, 0x00]))).toBeNull();
    expect(decodeText(new Uint8Array([0xff, 0xfe, 0x41]))).toBeNull();
  });
});

describe('files tab', () => {
  it('browses folders, shows links without following them and offers downloads', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session(editor),
      [`GET /api/v1/servers/${serverId}`]: () => json(details),
      [`GET /api/v1/servers/${serverId}/files`]: listing,
    });

    const router = renderAt(`/servers/${serverId}?tab=files`);

    const table = await screen.findByRole('table', { name: 'Files' });
    const rows = within(table).getAllByRole('row');
    expect(rows[1]).toHaveTextContent('game/');
    expect(rows[2]).toHaveTextContent('logs');
    expect(within(rows[2] as HTMLElement).getByText('link')).toBeInTheDocument();
    expect(within(rows[2] as HTMLElement).queryByRole('button', { name: 'logs' })).not.toBeInTheDocument();
    expect(rows[4]).toHaveTextContent('80 MB');
    // Too large for the editor: downloaded, not opened.
    expect(within(rows[4] as HTMLElement).queryByRole('button', { name: 'de_dust2.bsp' })).not.toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Download server.cfg' })).toHaveAttribute(
      'href',
      `/api/v1/servers/${serverId}/files/download?path=server.cfg`,
    );

    await userEvent.click(screen.getByRole('button', { name: 'game/' }));

    expect(await screen.findByRole('button', { name: 'csgo/' })).toBeInTheDocument();
    expect(router.state.location.search).toBe('?tab=files&path=game');
    expect(screen.getByRole('navigation', { name: 'Location' })).toHaveTextContent('server/game');
    await userEvent.click(screen.getByRole('button', { name: '..' }));
    expect(await screen.findByRole('button', { name: 'game/' })).toBeInTheDocument();
  });

  it('says apart that a listing was cut and that entries with names Pulsar cannot open are not shown', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session(editor),
      [`GET /api/v1/servers/${serverId}`]: () => json(details),
      [`GET /api/v1/servers/${serverId}/files`]: listing,
    });

    renderAt(`/servers/${serverId}?tab=files&path=hidden`);

    expect(await screen.findByText('3 entries with names Pulsar cannot open are not shown.')).toBeInTheDocument();
    expect(screen.queryByText(/Only the first/)).not.toBeInTheDocument();
  });

  it('says a listing was cut at its cap without blaming names', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session(editor),
      [`GET /api/v1/servers/${serverId}`]: () => json(details),
      [`GET /api/v1/servers/${serverId}/files`]: listing,
    });

    renderAt(`/servers/${serverId}?tab=files&path=capped`);

    expect(await screen.findByText('Only the first 1 entries are shown.')).toBeInTheDocument();
    expect(screen.queryByText(/cannot open/)).not.toBeInTheDocument();
  });

  it('shows a name that hides a direction override as it really is, and still acts on the real name', async () => {
    const deleted: (string | null)[] = [];
    fakeApi({
      'GET /api/v1/auth/session': session(editor),
      [`GET /api/v1/servers/${serverId}`]: () => json(details),
      [`GET /api/v1/servers/${serverId}/files`]: listing,
      [`DELETE /api/v1/servers/${serverId}/files`]: (request) => {
        deleted.push(new URL(request.url).searchParams.get('path'));
        return new Response(null, { status: 204 });
      },
    });
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(true);

    renderAt(`/servers/${serverId}?tab=files&path=odd`);

    const table = await screen.findByRole('table', { name: 'Files' });
    expect(within(table).getByText('invoice[U+202E]fdp.exe')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Download invoice[U+202E]fdp.exe' })).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Delete invoice[U+202E]fdp.exe' }));

    expect(confirm).toHaveBeenCalledWith(expect.stringContaining('invoice[U+202E]fdp.exe'));
    await waitFor(() => expect(deleted).toEqual([`odd/invoice${String.fromCodePoint(0x202e)}fdp.exe`]));
    confirm.mockRestore();
  });

  it('creates folders and deletes after asking', async () => {
    const created: unknown[] = [];
    const deleted: (string | null)[] = [];
    fakeApi({
      'GET /api/v1/auth/session': session(editor),
      [`GET /api/v1/servers/${serverId}`]: () => json(details),
      [`GET /api/v1/servers/${serverId}/files`]: listing,
      [`POST /api/v1/servers/${serverId}/files/directories`]: async (request) => {
        created.push(await request.json());
        return new Response(null, { status: 204 });
      },
      [`DELETE /api/v1/servers/${serverId}/files`]: (request) => {
        deleted.push(new URL(request.url).searchParams.get('path'));
        return new Response(null, { status: 204 });
      },
    });
    renderAt(`/servers/${serverId}?tab=files&path=game`);
    await screen.findByRole('button', { name: 'csgo/' });

    await userEvent.click(screen.getByRole('button', { name: 'New folder' }));
    const dialog = await screen.findByRole('dialog', { name: 'New folder' });
    await userEvent.type(within(dialog).getByLabelText('Name'), 'con');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Create' }));
    expect(within(dialog).getByText('Windows reserves that name (CON, NUL, COM1…).')).toBeInTheDocument();
    await userEvent.clear(within(dialog).getByLabelText('Name'));
    await userEvent.type(within(dialog).getByLabelText('Name'), 'cfg');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Create' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(created).toEqual([{ path: 'game/cfg' }]);

    const confirm = vi.spyOn(window, 'confirm').mockReturnValueOnce(false).mockReturnValueOnce(true);
    await userEvent.click(screen.getByRole('button', { name: 'Delete csgo' }));
    await userEvent.click(screen.getByRole('button', { name: 'Delete csgo' }));
    await waitFor(() => expect(deleted).toEqual(['game/csgo']));
    expect(confirm).toHaveBeenCalledWith('Delete the folder csgo and everything in it? This cannot be undone.');
  });

  it('edits a file and saves it against the version it opened', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session(editor),
      [`GET /api/v1/servers/${serverId}`]: () => json(details),
      [`GET /api/v1/servers/${serverId}/files`]: listing,
      [`GET /api/v1/servers/${serverId}/files/content`]: () =>
        new Response('hostname "Retakes"\n', { headers: { ETag: '"v1"', 'Content-Type': 'application/octet-stream' } }),
    });
    FakeXhr.handler = async (xhr) =>
      xhr.headers['If-Match'] === '"v1"'
        ? { status: 412, body: { status: 412, code: 'files.changed' } }
        : written('server.cfg', 20, 'v3');
    renderAt(`/servers/${serverId}?tab=files`);

    await userEvent.click(await screen.findByRole('button', { name: 'server.cfg' }));
    const text = await screen.findByLabelText('Contents of server.cfg');
    expect(text).toHaveValue('hostname "Retakes"\n');
    expect(screen.getByRole('status')).toHaveTextContent('All changes saved');

    await userEvent.type(text, '// x');
    expect(screen.getByRole('status')).toHaveTextContent('Unsaved changes');
    await userEvent.keyboard('{Control>}s{/Control}');

    // Someone changed it meanwhile: nothing is overwritten until the user says so.
    expect(await screen.findByText(/The file changed on the server since you opened it/)).toBeInTheDocument();
    expect(FakeXhr.sent[0]?.method).toBe('PUT');
    expect(FakeXhr.sent[0]?.path).toBe('server.cfg');
    expect(await FakeXhr.sent[0]?.body?.text()).toBe('hostname "Retakes"\n// x');
    await userEvent.click(screen.getByRole('button', { name: 'Overwrite' }));

    await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent('All changes saved'));
    expect(FakeXhr.sent[1]?.headers['If-Match']).toBeUndefined();
    expect(FakeXhr.sent[1]?.headers['X-XSRF-TOKEN']).toBeUndefined(); // no cookie in the tests
  });

  it('keeps what is being edited when the server changes meanwhile', async () => {
    const api = fakeApi({
      'GET /api/v1/auth/session': session(editor),
      [`GET /api/v1/servers/${serverId}`]: () => json(details),
      [`GET /api/v1/servers/${serverId}/files/content`]: () =>
        new Response('sv_cheats 0', { headers: { ETag: '"v1"' } }),
    });
    const count = (suffix: string) =>
      api.mock.calls.filter(([input]) =>
        new URL(input instanceof Request ? input.url : String(input)).pathname.endsWith(suffix),
      ).length;
    const before = connections.length;
    renderAt(`/servers/${serverId}?tab=files&file=server.cfg`);
    const live = await openedConnection(before);
    const text = await screen.findByLabelText('Contents of server.cfg');
    await userEvent.type(text, ' // mine');

    // The game started and the panel says the server changed: its page refreshes, the open file does not.
    live.emit('ServersChanged', [serverId]);
    await waitFor(() => expect(count(`/servers/${serverId}`)).toBeGreaterThan(1));

    expect(count('/files/content')).toBe(1);
    expect(screen.getByLabelText('Contents of server.cfg')).toHaveValue('sv_cheats 0 // mine');
    expect(screen.getByRole('status')).toHaveTextContent('Unsaved changes');
  });

  describe('the same file on two servers', () => {
    const otherId = '0192f1a0-0000-7000-8000-0000000000e2';
    const other: Schemas['ServerDetails'] = { ...details, server: { ...server, id: otherId, name: 'Deathmatch' } };
    const contentPath = (id: string) => `/api/v1/servers/${id}/files/content`;

    /** Reads that answer only when the test says so, in the order the test chooses. */
    function delayedReads() {
      const pending: Record<string, ((response: Response) => void)[]> = { [serverId]: [], [otherId]: [] };
      const read = (id: string) => () =>
        new Promise<Response>((resolve) => {
          pending[id]?.push(resolve);
        });
      const answer = (id: string, index: number, text: string, version: string) =>
        act(async () => {
          pending[id]?.[index]?.(new Response(text, { headers: { ETag: `"${version}"` } }));
          await new Promise((resolve) => setTimeout(resolve, 0));
        });
      const count = (id: string) => pending[id]?.length ?? 0;
      return { read, answer, count };
    }

    /** Both servers already in the cache: going from one to the other shows its page at once. */
    function renderBoth(first: string) {
      const router = createMemoryRouter(routes, { initialEntries: [first] });
      queryClient = new QueryClient({
        defaultOptions: { queries: { retry: false, refetchInterval: false, staleTime: Infinity } },
      });
      queryClient.setQueryData(['servers', serverId], details);
      queryClient.setQueryData(['servers', otherId], other);
      render(<App router={router} queryClient={queryClient} />);
      return router;
    }

    it("never shows or saves one server's file as the other's, back and forward, whatever order reads answer in", async () => {
      const reads = delayedReads();
      fakeApi({
        'GET /api/v1/auth/session': session(editor),
        [`GET /api/v1/servers/${serverId}`]: () => json(details),
        [`GET /api/v1/servers/${otherId}`]: () => json(other),
        [`GET ${contentPath(serverId)}`]: reads.read(serverId),
        [`GET ${contentPath(otherId)}`]: reads.read(otherId),
      });
      FakeXhr.handler = async (xhr) => written(xhr.path ?? '', 30, 'b3');
      const router = renderBoth(`/servers/${serverId}?tab=files&file=server.cfg`);
      await waitFor(() => expect(reads.count(serverId)).toBe(1));
      await reads.answer(serverId, 0, 'hostname "Retakes"\n', 'a1');
      expect(await screen.findByLabelText('Contents of server.cfg')).toHaveValue('hostname "Retakes"\n');

      // To the other server's same file: while it is read, nothing of the first one is shown.
      await act(() => router.navigate(`/servers/${otherId}?tab=files&file=server.cfg`));
      await waitFor(() => expect(reads.count(otherId)).toBe(1));
      expect(screen.queryByLabelText('Contents of server.cfg')).not.toBeInTheDocument();
      expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('Deathmatch');

      // Back to the first one and forward again before any of those reads answered.
      await act(() => router.navigate(-1));
      await waitFor(() => expect(reads.count(serverId)).toBe(2));
      await act(() => router.navigate(1));
      await waitFor(() => expect(reads.count(otherId)).toBe(2));

      // The latest read of this server answers first, then the ones given up on: they change nothing.
      await reads.answer(otherId, 1, 'hostname "Deathmatch"\n', 'b1');
      await reads.answer(serverId, 1, 'hostname "Retakes"\n', 'a1');
      await reads.answer(otherId, 0, 'hostname "an older read"\n', 'b0');
      const text = screen.getByLabelText('Contents of server.cfg');
      expect(text).toHaveValue('hostname "Deathmatch"\n');
      expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('Deathmatch');

      await userEvent.type(text, '// x');
      await userEvent.keyboard('{Control>}s{/Control}');

      await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent('All changes saved'));
      expect(FakeXhr.sent).toHaveLength(1);
      expect(new URL(FakeXhr.sent[0]?.url ?? '', 'http://localhost').pathname).toBe(contentPath(otherId));
      expect(FakeXhr.sent[0]?.path).toBe('server.cfg');
      expect(FakeXhr.sent[0]?.headers['If-Match']).toBe('"b1"');
      expect(await FakeXhr.sent[0]?.body?.text()).toBe('hostname "Deathmatch"\n// x');
    });

    it('asks before leaving unsaved changes for the other server, and opens its file afresh there', async () => {
      const reads = delayedReads();
      fakeApi({
        'GET /api/v1/auth/session': session(editor),
        [`GET /api/v1/servers/${serverId}`]: () => json(details),
        [`GET /api/v1/servers/${otherId}`]: () => json(other),
        [`GET ${contentPath(serverId)}`]: reads.read(serverId),
        [`GET ${contentPath(otherId)}`]: reads.read(otherId),
      });
      const confirm = vi.spyOn(window, 'confirm').mockReturnValueOnce(false).mockReturnValueOnce(true);
      const router = renderBoth(`/servers/${serverId}?tab=files&file=server.cfg`);
      await waitFor(() => expect(reads.count(serverId)).toBe(1));
      await reads.answer(serverId, 0, 'sv_cheats 0', 'a1');
      await userEvent.type(await screen.findByLabelText('Contents of server.cfg'), ' // mine');

      await act(() => router.navigate(`/servers/${otherId}?tab=files&file=server.cfg`));

      // Kept: still this server, still editing.
      expect(confirm).toHaveBeenCalledWith('Discard your unsaved changes?');
      expect(router.state.location.pathname).toBe(`/servers/${serverId}`);
      expect(screen.getByLabelText('Contents of server.cfg')).toHaveValue('sv_cheats 0 // mine');

      await act(() => router.navigate(`/servers/${otherId}?tab=files&file=server.cfg`));

      await waitFor(() => expect(reads.count(otherId)).toBe(1));
      expect(screen.queryByLabelText('Contents of server.cfg')).not.toBeInTheDocument();
      await reads.answer(otherId, 0, 'sv_cheats 1', 'b1');
      expect(screen.getByLabelText('Contents of server.cfg')).toHaveValue('sv_cheats 1');
      expect(screen.getByRole('status')).toHaveTextContent('All changes saved');
      expect(confirm).toHaveBeenCalledTimes(2);
    });
  });

  it('fetches the open folder again when the server changes (an install or a restore rewrites the files)', async () => {
    const api = fakeApi({
      'GET /api/v1/auth/session': session(editor),
      [`GET /api/v1/servers/${serverId}`]: () => json(details),
      [`GET /api/v1/servers/${serverId}/files`]: listing,
    });
    const listed = () =>
      api.mock.calls.filter(([input]) =>
        new URL(input instanceof Request ? input.url : String(input)).pathname.endsWith('/files'),
      ).length;
    const before = connections.length;
    renderAt(`/servers/${serverId}?tab=files`);
    const live = await openedConnection(before);
    await screen.findByRole('table', { name: 'Files' });
    const first = listed();

    // Another server changed, or only a node did: this server's files did not.
    live.emit('ServersChanged', ['0192f1a0-0000-7000-8000-0000000000ff']);
    live.emit('NodesChanged', [server.nodeId]);
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(listed()).toBe(first);

    live.emit('ServersChanged', [serverId]);

    await waitFor(() => expect(listed()).toBeGreaterThan(first));
  });

  it('says a file that is not UTF-8 is not UTF-8 text, and offers it for download', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session(editor),
      [`GET /api/v1/servers/${serverId}`]: () => json(details),
      // Windows-1252 "ñ".
      [`GET /api/v1/servers/${serverId}/files/content`]: () =>
        new Response(new Uint8Array([0x61, 0xf1, 0x6f]), { headers: { ETag: '"v1"' } }),
    });
    renderAt(`/servers/${serverId}?tab=files&file=server.cfg`);

    expect(await screen.findByText(/not UTF-8 text/)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Download' })).toBeInTheDocument();
  });

  it('asks before leaving unsaved changes for another tab, and finds the file again there', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session(editor),
      [`GET /api/v1/servers/${serverId}`]: () => json(details),
      [`GET /api/v1/servers/${serverId}/files/content`]: () =>
        new Response('sv_cheats 0', { headers: { ETag: '"v1"' } }),
    });
    const confirm = vi.spyOn(window, 'confirm').mockReturnValueOnce(false).mockReturnValueOnce(true);
    const router = renderAt(`/servers/${serverId}?tab=files&file=server.cfg`);
    const user = userEvent.setup();
    await user.type(await screen.findByLabelText('Contents of server.cfg'), ' // mine');

    await user.click(screen.getByRole('tab', { name: 'Overview' }));

    // Kept: still editing, nothing lost.
    expect(confirm).toHaveBeenCalledWith('Discard your unsaved changes?');
    expect(screen.getByLabelText('Contents of server.cfg')).toHaveValue('sv_cheats 0 // mine');
    expect(router.state.location.search).toBe('?tab=files&file=server.cfg');

    await user.click(screen.getByRole('tab', { name: 'Overview' }));

    await waitFor(() => expect(screen.queryByLabelText('Contents of server.cfg')).not.toBeInTheDocument());
    expect(confirm).toHaveBeenCalledTimes(2);
    // The open file stays in the address: back in Files, it opens again.
    await user.click(screen.getByRole('tab', { name: 'Files' }));
    expect(await screen.findByLabelText('Contents of server.cfg')).toHaveValue('sv_cheats 0');
  });

  it('asks once when closing unsaved changes', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session(editor),
      [`GET /api/v1/servers/${serverId}`]: () => json(details),
      [`GET /api/v1/servers/${serverId}/files`]: listing,
      [`GET /api/v1/servers/${serverId}/files/content`]: () =>
        new Response('sv_cheats 0', { headers: { ETag: '"v1"' } }),
    });
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(true);
    renderAt(`/servers/${serverId}?tab=files&file=server.cfg`);
    const user = userEvent.setup();
    await user.type(await screen.findByLabelText('Contents of server.cfg'), ' // mine');

    await user.click(screen.getByRole('button', { name: 'Close' }));

    expect(await screen.findByRole('table', { name: 'Files' })).toBeInTheDocument();
    expect(confirm).toHaveBeenCalledOnce();
  });

  it('says how to leave the editor with the keyboard', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session(editor),
      [`GET /api/v1/servers/${serverId}`]: () => json(details),
      [`GET /api/v1/servers/${serverId}/files/content`]: () =>
        new Response('sv_cheats 0', { headers: { ETag: '"v1"' } }),
    });
    renderAt(`/servers/${serverId}?tab=files&file=server.cfg`);

    expect(await screen.findByLabelText('Contents of server.cfg')).toHaveAccessibleDescription(
      'Tab indents. To leave the editor with the keyboard, press Esc and then Tab, or Ctrl+M (Shift+Alt+M on a Mac) to make Tab move the focus.',
    );
  });

  it("relies on CodeMirror's own Ctrl+M (Shift+Alt+M on a Mac), which the hint names", () => {
    // The editor is replaced in these tests; its keymap is CodeMirror's defaultKeymap, which binds tab-focus mode.
    expect(defaultKeymap).toContainEqual(
      expect.objectContaining({ key: 'Ctrl-m', mac: 'Shift-Alt-m', run: toggleTabFocusMode }),
    );
  });

  it('refreshes the listing after a save and does not save an unchanged file', async () => {
    const api = fakeApi({
      'GET /api/v1/auth/session': session(editor),
      [`GET /api/v1/servers/${serverId}`]: () => json(details),
      [`GET /api/v1/servers/${serverId}/files`]: listing,
      [`GET /api/v1/servers/${serverId}/files/content`]: () =>
        new Response('sv_cheats 0', { headers: { ETag: '"v1"' } }),
    });
    const listed = () =>
      api.mock.calls.filter(([input]) =>
        new URL(input instanceof Request ? input.url : String(input)).pathname.endsWith('/files'),
      ).length;
    FakeXhr.handler = async () => written('server.cfg', 20);
    // As in the app: a listing is fresh for 30 s.
    renderAt(`/servers/${serverId}?tab=files`, 30_000);
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'server.cfg' }));
    const text = await screen.findByLabelText('Contents of server.cfg');

    // Nothing changed: Ctrl+S writes nothing.
    await user.click(text);
    await user.keyboard('{Control>}s{/Control}');
    expect(FakeXhr.sent).toHaveLength(0);

    await user.type(text, ' // x');
    await user.keyboard('{Control>}s{/Control}');
    await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent('All changes saved'));
    const before = listed();
    await user.click(screen.getByRole('button', { name: 'Close' }));

    await screen.findByRole('table', { name: 'Files' });
    await waitFor(() => expect(listed()).toBeGreaterThan(before));
  });

  it('does not offer changes while the node is disconnected', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session(editor),
      [`GET /api/v1/servers/${serverId}`]: () => json({ ...details, server: { ...server, nodeConnected: false } }),
      [`GET /api/v1/servers/${serverId}/files`]: listing,
    });
    renderAt(`/servers/${serverId}?tab=files`);

    await screen.findByRole('table', { name: 'Files' });
    expect(screen.getByText('The node is offline: files cannot be changed until it reconnects.')).toBeInTheDocument();
    for (const name of ['New folder', 'New file', 'Upload', 'Rename server.cfg', 'Delete server.cfg']) {
      expect(screen.getByRole('button', { name })).toBeDisabled();
    }
  });

  it('uploads files and asks before replacing one', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session(editor),
      [`GET /api/v1/servers/${serverId}`]: () => json(details),
      [`GET /api/v1/servers/${serverId}/files`]: listing,
    });
    FakeXhr.handler = async (xhr) =>
      xhr.headers['If-None-Match'] === '*' && xhr.path === 'server.cfg'
        ? { status: 409, body: { status: 409, code: 'files.exists' } }
        : written(xhr.path ?? '', xhr.body?.size ?? 0);
    renderAt(`/servers/${serverId}?tab=files`);
    await screen.findByRole('table', { name: 'Files' });

    await userEvent.upload(screen.getByLabelText('Upload'), [
      new File(['map'], 'de_test.bsp'),
      new File(['hostname "new"'], 'server.cfg'),
    ]);

    expect(await screen.findByText('A file with that name already exists.')).toBeInTheDocument();
    expect(screen.getByText('Uploaded')).toBeInTheDocument();
    expect(FakeXhr.sent.map((x) => [x.path, x.headers['If-None-Match']])).toEqual([
      ['de_test.bsp', '*'],
      ['server.cfg', '*'],
    ]);
    await userEvent.click(screen.getByRole('button', { name: 'Replace' }));
    await waitFor(() => expect(screen.getAllByText('Uploaded')).toHaveLength(2));
    expect(FakeXhr.sent[2]?.headers['If-None-Match']).toBeUndefined();
  });

  it('asks for the session again when an upload finds it ended', async () => {
    let sessions = 0;
    fakeApi({
      'GET /api/v1/auth/session': () => {
        sessions++;
        return json({ setupRequired: false, user: editor, insecureConnection: false });
      },
      [`GET /api/v1/servers/${serverId}`]: () => json(details),
      [`GET /api/v1/servers/${serverId}/files`]: listing,
    });
    // Uploads go around the client the rest of the app uses (for their progress): a 401 still sends to the sign-in.
    FakeXhr.handler = async () => ({ status: 401, body: { status: 401, code: 'auth.required' } });
    renderAt(`/servers/${serverId}?tab=files`);
    await screen.findByRole('table', { name: 'Files' });
    const before = sessions;

    await userEvent.upload(screen.getByLabelText('Upload'), [new File(['map'], 'de_test.bsp')]);

    await waitFor(() => expect(sessions).toBeGreaterThan(before));
  });

  it('asks about a name that exists only in the folder it was uploaded to', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session(editor),
      [`GET /api/v1/servers/${serverId}`]: () => json(details),
      [`GET /api/v1/servers/${serverId}/files`]: listing,
    });
    FakeXhr.handler = async (xhr) =>
      xhr.headers['If-None-Match'] === '*'
        ? { status: 409, body: { status: 409, code: 'files.exists' } }
        : written(xhr.path ?? '', xhr.body?.size ?? 0);
    renderAt(`/servers/${serverId}?tab=files`);
    await screen.findByRole('table', { name: 'Files' });
    await userEvent.upload(screen.getByLabelText('Upload'), [new File(['hostname "new"'], 'server.cfg')]);
    await screen.findByRole('button', { name: 'Replace' });

    // Another folder: the question belongs to the first one.
    await userEvent.click(screen.getByRole('button', { name: 'game/' }));
    await screen.findByRole('button', { name: 'csgo/' });
    expect(screen.queryByRole('button', { name: 'Replace' })).not.toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'server' }));
    await userEvent.click(await screen.findByRole('button', { name: 'Replace' }));
    await waitFor(() => expect(FakeXhr.sent).toHaveLength(2));
    expect(FakeXhr.sent[1]?.path).toBe('server.cfg');
    expect(FakeXhr.sent[1]?.headers['If-None-Match']).toBeUndefined();
  });

  it('finishes an upload in the folder it started in, and refreshes that folder, after another one was opened', async () => {
    const api = fakeApi({
      'GET /api/v1/auth/session': session(editor),
      [`GET /api/v1/servers/${serverId}`]: () => json(details),
      [`GET /api/v1/servers/${serverId}/files`]: listing,
    });
    const listed = (folder: string) =>
      api.mock.calls.filter(([input]) => {
        const url = new URL(input instanceof Request ? input.url : String(input));
        return url.pathname.endsWith('/files') && url.searchParams.get('path') === folder;
      }).length;
    let finish: (() => void) | undefined;
    FakeXhr.handler = (xhr) =>
      new Promise((resolve) => {
        finish = () => resolve(written(xhr.path ?? '', xhr.body?.size ?? 0));
      });
    renderAt(`/servers/${serverId}?tab=files&path=game`);
    await screen.findByRole('button', { name: 'csgo/' });

    await userEvent.upload(screen.getByLabelText('Upload'), [new File(['map'], 'de_test.bsp')]);
    await waitFor(() => expect(FakeXhr.sent).toHaveLength(1));
    // Still uploading when the root folder is opened.
    await userEvent.click(screen.getByRole('button', { name: 'server' }));
    await screen.findByRole('button', { name: 'game/' });
    const rootListings = listed('');
    const gameListings = listed('game');

    finish?.();
    await waitFor(() => expect(queryClient.getQueryState(['serverFiles', serverId, 'game'])?.isInvalidated).toBe(true));

    expect(FakeXhr.sent.map((x) => x.path)).toEqual(['game/de_test.bsp']);
    // The folder written to is the one marked stale; the open one is not fetched again for it.
    expect(queryClient.getQueryState(['serverFiles', serverId, ''])?.isInvalidated).toBe(false);
    expect(listed('')).toBe(rootListings);
    expect(listed('game')).toBe(gameListings);
  });

  it('says what a name with hidden characters really reads when renaming it, and sends the name as it is', async () => {
    const bodies: unknown[] = [];
    fakeApi({
      'GET /api/v1/auth/session': session(editor),
      [`GET /api/v1/servers/${serverId}`]: () => json(details),
      [`GET /api/v1/servers/${serverId}/files`]: listing,
      [`POST /api/v1/servers/${serverId}/files/move`]: async (request) => {
        bodies.push(await request.json());
        return json({});
      },
    });
    renderAt(`/servers/${serverId}?tab=files&path=odd`);
    const real = `invoice${String.fromCodePoint(0x202e)}fdp.exe`;

    await userEvent.click(await screen.findByRole('button', { name: 'Rename invoice[U+202E]fdp.exe' }));
    const dialog = await screen.findByRole('dialog');
    const field = within(dialog).getByLabelText('Name');

    expect(field).toHaveValue(real);
    expect(
      within(dialog).getByText('This name has hidden characters. It reads: invoice[U+202E]fdp.exe'),
    ).toBeInTheDocument();
    await userEvent.type(field, 'x');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Rename' }));
    await waitFor(() => expect(bodies).toEqual([{ path: `odd/${real}`, destination: `odd/${real}x` }]));
  });

  it('does not add a note to an ordinary name when renaming it', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session(editor),
      [`GET /api/v1/servers/${serverId}`]: () => json(details),
      [`GET /api/v1/servers/${serverId}/files`]: listing,
    });
    renderAt(`/servers/${serverId}?tab=files`);

    await userEvent.click(await screen.findByRole('button', { name: 'Rename server.cfg' }));
    const dialog = await screen.findByRole('dialog');

    expect(within(dialog).queryByText(/hidden characters/)).not.toBeInTheDocument();
  });

  it('tells which line endings a file has', () => {
    expect(lineEndings('a\r\nb\r\n')).toBe('\r\n');
    expect(lineEndings('a\nb')).toBe('\n');
    expect(lineEndings('a\rb\r')).toBe('\r');
    expect(lineEndings('one line')).toBeNull();
    expect(lineEndings('a\r\nb\nc')).toBe('mixed');
    expect(lineEndings('a\rb\nc')).toBe('mixed');
  });

  it('does not save a file whose line endings it would change', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session(editor),
      [`GET /api/v1/servers/${serverId}`]: () => json(details),
      [`GET /api/v1/servers/${serverId}/files/content`]: () =>
        new Response('hostname "x"\r\nsv_cheats 0\n', { headers: { ETag: '"v1"' } }),
    });
    renderAt(`/servers/${serverId}?tab=files&file=server.cfg`);

    // Windows and Unix line endings together: the editor would rewrite them all, so it only shows the file.
    expect(await screen.findByLabelText('Contents of server.cfg')).toHaveAttribute('readonly');
    expect(screen.getByText(/mixes Windows and Unix line endings/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Save' })).not.toBeInTheDocument();
  });

  it('is read only for users who may only look at files', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session(viewer),
      [`GET /api/v1/servers/${serverId}`]: () => json(details),
      [`GET /api/v1/servers/${serverId}/files`]: listing,
      [`GET /api/v1/servers/${serverId}/files/content`]: () =>
        new Response('sv_cheats 0', { headers: { ETag: '"v1"' } }),
    });
    renderAt(`/servers/${serverId}?tab=files`);

    await screen.findByRole('table', { name: 'Files' });
    expect(screen.queryByRole('button', { name: 'Upload' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /^Delete/ })).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'server.cfg' }));
    expect(await screen.findByLabelText('Contents of server.cfg')).toHaveAttribute('readonly');
    expect(screen.getByRole('status')).toHaveTextContent('Read only');
    expect(screen.queryByRole('button', { name: 'Save' })).not.toBeInTheDocument();
  });

  it('is not offered without the permission to see files', async () => {
    fakeApi({
      'GET /api/v1/auth/session': session({ ...adminUser, permissions: ['server.view'] }),
      [`GET /api/v1/servers/${serverId}`]: () => json(details),
    });
    renderAt(`/servers/${serverId}`);

    await screen.findByRole('tab', { name: 'Overview' });
    expect(screen.queryByRole('tab', { name: 'Files' })).not.toBeInTheDocument();
  });
});
