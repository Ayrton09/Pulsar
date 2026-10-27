import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import type { Schemas } from '../../api/client';
import { initI18n } from '../../app/i18n';
import { fakeApi, json, problem } from '../../test/fakeApi';
import type { TemplateVariable } from './api';
import { VariableFields } from './VariableFields';

const variable: TemplateVariable = {
  key: 'mods',
  label: 'Mods',
  type: 'string',
  default: '',
  required: false,
  secret: false,
  userEditable: true,
  pattern: '^([0-9]+(,[0-9]+)*)?$',
  maxLength: 2048,
  min: null,
  max: null,
  options: [],
  description: null,
  curseForge: { gameId: 83374, separator: ',' },
};

function project(
  id: string,
  changes: Partial<Schemas['CurseForgeProjectView']> = {},
): Schemas['CurseForgeProjectView'] {
  return {
    id,
    found: true,
    name: `Mod ${id}`,
    author: 'someone',
    url: `https://www.curseforge.com/ark-survival-ascended/mods/mod-${id}`,
    otherGame: false,
    downloads: 1200,
    updated: '2026-10-01T12:00:00Z',
    ...changes,
  };
}

const curseForge: Record<string, Schemas['CurseForgeProjectView']> = {
  '928548': project('928548', {
    name: 'Super Spyglass',
    url: 'https://www.curseforge.com/ark-survival-ascended/mods/super-spyglass',
  }),
  '238222': project('238222', { name: 'Just Enough Items', otherGame: true }),
  '999999': project('999999', { found: false, name: null, author: null, url: null }),
  '929420': project('929420', { name: 'Awesome Teleporters' }),
};

/** The editor as forms use it, keeping the value it reports. */
function Editor({
  initial,
  disabled = false,
  maxLength,
  changes,
}: {
  initial: string;
  disabled?: boolean;
  maxLength?: number | undefined;
  changes: string[];
}) {
  const [value, setValue] = useState(initial);
  return (
    <VariableFields
      variables={[{ ...variable, maxLength: maxLength ?? variable.maxLength }]}
      values={{ mods: value }}
      readOnly={() => disabled}
      onChange={(_, next) => {
        changes.push(next);
        setValue(next);
      }}
    />
  );
}

/** `held`: lookups of that id are answered only once `release` resolves. */
function renderEditor(
  initial: string,
  {
    disabled = false,
    configured = true,
    maxLength,
    held,
  }: {
    disabled?: boolean;
    configured?: boolean;
    maxLength?: number;
    held?: { id: string; release: Promise<void> };
  } = {},
) {
  const changes: string[] = [];
  const lookups: string[][] = [];
  const searches: string[] = [];
  fakeApi({
    'POST /api/v1/curseforge/lookup': async (request) => {
      const body = (await request.json()) as { gameId: number; ids: string[] };
      expect(body.gameId).toBe(83374);
      lookups.push(body.ids);
      if (held && body.ids.includes(held.id)) {
        await held.release;
      }
      if (!configured) {
        return problem(409, 'mods.catalog_not_configured');
      }
      return json(body.ids.map((id) => curseForge[id] ?? project(id)));
    },
    'GET /api/v1/curseforge/search': (request) => {
      const url = new URL(request.url);
      expect(url.searchParams.get('gameId')).toBe('83374');
      const query = url.searchParams.get('query') ?? '';
      searches.push(query);
      if (!configured) {
        return problem(409, 'mods.catalog_not_configured');
      }
      const hits = Object.values(curseForge).filter(
        (p) => !p.otherGame && p.found && (p.name ?? '').toLowerCase().includes(query.toLowerCase()),
      );
      return json({ hits, total: hits.length });
    },
  });
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <Editor initial={initial} disabled={disabled} maxLength={maxLength} changes={changes} />
    </QueryClientProvider>,
  );
  return { changes, lookups, searches };
}

beforeAll(async () => {
  await initI18n('en-US');
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('curseforge list editor', () => {
  it('shows the mods by name and removes them', async () => {
    const { changes, lookups } = renderEditor('928548,238222,999999');

    const list = screen.getByRole('list', { name: 'Mods' });
    expect(await within(list).findByRole('link', { name: 'Super Spyglass' })).toHaveAttribute(
      'href',
      'https://www.curseforge.com/ark-survival-ascended/mods/super-spyglass',
    );
    expect(within(list).getByText(/Another game's/)).toBeInTheDocument();
    expect(within(list).getByText(/Not on CurseForge/)).toBeInTheDocument();
    expect(lookups).toEqual([['928548', '238222', '999999']]);

    await userEvent.click(screen.getByRole('button', { name: 'Remove Just Enough Items' }));
    expect(changes).toEqual(['928548,999999']);
  });

  it('adds mods found by a search, and by id', async () => {
    const { changes, searches } = renderEditor('');

    await userEvent.type(screen.getByRole('searchbox', { name: "Search the game's mods on CurseForge" }), 'tele');
    const results = await screen.findByRole('list', { name: 'Mods found' }, { timeout: 2000 });
    expect(searches).toEqual(['tele']); // asked once typing paused, not per key
    await userEvent.click(within(results).getByRole('button', { name: 'Add' }));
    expect(changes).toEqual(['929420']);
    expect(within(results).getByRole('button', { name: 'Added' })).toBeDisabled();

    const input = screen.getByRole('textbox', { name: 'Mods' });
    await userEvent.type(input, '0123{Enter}');
    expect(screen.getByRole('alert')).toHaveTextContent('A CurseForge project id is a number.');
    await userEvent.clear(input);
    await userEvent.type(input, '929420{Enter}');
    expect(screen.getByRole('alert')).toHaveTextContent('That mod is in the list already.');
    await userEvent.clear(input);
    await userEvent.type(input, '928548{Enter}');
    await waitFor(() => expect(changes.at(-1)).toBe('929420,928548'));
    expect(await screen.findByRole('link', { name: 'Super Spyglass' })).toBeInTheDocument();
  });

  it('takes ids as typed without the panel key, and says why', async () => {
    const { changes } = renderEditor('', { configured: false });

    await userEvent.type(screen.getByRole('textbox', { name: 'Mods' }), '555{Enter}');

    await waitFor(() => expect(changes).toEqual(['555']));
    expect(await screen.findByText(/Without the panel's CurseForge key/)).toBeInTheDocument();
    expect(screen.queryByRole('searchbox')).not.toBeInTheDocument();
    expect(screen.getByRole('list', { name: 'Mods' })).toHaveTextContent('555');
  });

  it('only shows the ids when the user may not change them, without asking CurseForge', () => {
    const { lookups, searches } = renderEditor('928548', { disabled: true });

    expect(screen.getByRole('list', { name: 'Mods' })).toHaveTextContent('928548');
    expect(screen.queryByRole('button', { name: /Add|Remove/ })).not.toBeInTheDocument();
    expect(screen.queryByRole('searchbox')).not.toBeInTheDocument();
    expect(lookups).toEqual([]);
    expect(searches).toEqual([]);
  });

  it('looks up only the tokens that are project ids', async () => {
    const { lookups } = renderEditor('928548,abc,0123');

    expect(await screen.findByRole('link', { name: 'Super Spyglass' })).toBeInTheDocument();
    expect(screen.getByRole('list', { name: 'Mods' })).toHaveTextContent('abc');
    expect(lookups).toEqual([['928548']]);
  });

  it('adds both of two ids added quickly, and keeps a removal made meanwhile', async () => {
    let release = () => {};
    const { changes } = renderEditor('238222', {
      held: {
        id: '928548',
        release: new Promise<void>((resolve) => {
          release = resolve;
        }),
      },
    });
    const input = screen.getByRole('textbox', { name: 'Mods' });

    await userEvent.type(input, '928548{Enter}');
    await userEvent.type(input, '929420{Enter}');
    await waitFor(() => expect(changes).toEqual(['238222,929420']));
    await userEvent.click(await screen.findByRole('button', { name: 'Remove Just Enough Items' }));
    expect(changes.at(-1)).toBe('929420');
    release();

    await waitFor(() => expect(changes.at(-1)).toBe('929420,928548'));
  });

  it('says when the list is as long as the setting takes', async () => {
    // 13 characters: one more id of six digits does not fit.
    const { changes } = renderEditor('928548,238222', { maxLength: 13 });

    await userEvent.type(screen.getByRole('textbox', { name: 'Mods' }), '929420{Enter}');

    expect(await screen.findByRole('alert')).toHaveTextContent('The list is longer than this setting takes.');
    expect(changes).toEqual([]);
  });
});
