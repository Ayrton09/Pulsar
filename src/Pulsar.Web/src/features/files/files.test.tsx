import { QueryClient } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter } from 'react-router';
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import type { Schemas } from '../../api/client';
import { App } from '../../app/App';
import { initI18n } from '../../app/i18n';
import { routes } from '../../app/routes';
import { adminUser, fakeApi, json, problem, session } from '../../test/fakeApi';
import { connections, openedConnection } from '../../test/fakeSignalR';
import { breadcrumbs, nameProblem } from './paths';
import { decodeText, lineEndings } from './text';

// CodeMirror needs a real layout: the tests type into a plain text area with the same contract.
vi.mock('./CodeEditor', () => ({
  default: ({
    label,
    initial,
    readOnly,
    onChange,
    onSave,
  }: {
    label: string;
    initial: string;
    readOnly: boolean;
    onChange: (text: string) => void;
    onSave: () => void;
  }) => (
    <textarea
      aria-label={label}
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
  limits: { cpuPercent: null, memoryMb: null },
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
};

function listing(request: Request): Response {
  const path = new URL(request.url).searchParams.get('path') ?? '';
  const entries = listings[path];
  return entries ? json({ path, entries, truncated: false }) : problem(404, 'files.not_found');
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

function renderAt(path: string) {
  const router = createMemoryRouter(routes, { initialEntries: [path] });
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, refetchInterval: false } } });
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
