import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import type { Schemas } from '../../api/client';
import { initI18n } from '../../app/i18n';
import { fakeApi, json, problem } from '../../test/fakeApi';
import type { TemplateVariable } from './api';
import { parseReforgerId } from './catalogs';
import { VariableFields } from './VariableFields';

const variable: TemplateVariable = {
  key: 'mods',
  label: 'Mods',
  type: 'string',
  default: '',
  required: false,
  secret: false,
  userEditable: true,
  pattern: '^([0-9A-Fa-f]{16}(,[0-9A-Fa-f]{16})*)?$',
  maxLength: 2048,
  min: null,
  max: null,
  options: [],
  description: null,
  reforgerWorkshop: { separator: ',' },
};

const WHERE = '5965550F24A0C152';
const BARRETT = 'ABCDEF0123456789';
const GONE = '0000000000000001';

function mod(id: string, changes: Partial<Schemas['ReforgerModView']> = {}): Schemas['ReforgerModView'] {
  return {
    id,
    found: true,
    name: `Mod ${id}`,
    author: 'someone',
    version: '1.0.0',
    size: 3 * 1024 * 1024,
    updated: '2025-03-03T14:44:25Z',
    url: `https://reforger.armaplatform.com/workshop/${id.toUpperCase()}`,
    ...changes,
  };
}

const workshop: Record<string, Partial<Schemas['ReforgerModView']>> = {
  [WHERE]: { name: 'Where Am I', version: '1.2.0' },
  [BARRETT]: { name: 'Barrett M82', author: 'gunsmith' },
  [GONE]: { found: false, name: null, author: null, version: null, size: null, updated: null, url: null },
};

function Editor({ initial, disabled = false, changes }: { initial: string; disabled?: boolean; changes: string[] }) {
  const [value, setValue] = useState(initial);
  return (
    <VariableFields
      variables={[variable]}
      values={{ mods: value }}
      readOnly={() => disabled}
      onChange={(_, next) => {
        changes.push(next);
        setValue(next);
      }}
    />
  );
}

function renderEditor(initial: string, { disabled = false, down = false } = {}) {
  const changes: string[] = [];
  const lookups: string[][] = [];
  const searches: string[] = [];
  fakeApi({
    'POST /api/v1/reforger-workshop/lookup': async (request) => {
      const body = (await request.json()) as { ids: string[] };
      lookups.push(body.ids);
      if (down) {
        return problem(502, 'mods.catalog_unavailable');
      }
      return json(body.ids.map((id) => mod(id, workshop[id.toUpperCase()] ?? {})));
    },
    'GET /api/v1/reforger-workshop/search': (request) => {
      const query = new URL(request.url).searchParams.get('query') ?? '';
      searches.push(query);
      const hits = [mod(BARRETT, workshop[BARRETT])].filter((m) => m.name?.toLowerCase().includes(query.toLowerCase()));
      return json({ hits, total: hits.length });
    },
  });
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <Editor initial={initial} disabled={disabled} changes={changes} />
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

describe('arma reforger workshop editor', () => {
  it('reads ids in capitals or not', () => {
    expect(parseReforgerId(' 5965550f24a0c152 ')).toBe(WHERE);
    expect(parseReforgerId('5965550F24A0C15')).toBeNull();
    expect(parseReforgerId('5965550F24A0C15G')).toBeNull();
  });

  it('shows the mods by name, with their version and size, and removes them', async () => {
    const { changes, lookups } = renderEditor(`${WHERE.toLowerCase()},${GONE}`);

    const list = screen.getByRole('list', { name: 'Mods' });
    expect(await within(list).findByRole('link', { name: 'Where Am I' })).toHaveAttribute(
      'href',
      `https://reforger.armaplatform.com/workshop/${WHERE}`,
    );
    expect(within(list).getByText(/v1\.2\.0 · 3 MB/)).toBeInTheDocument();
    expect(within(list).getByText(/Not in the Workshop/)).toBeInTheDocument();
    expect(lookups).toEqual([[WHERE.toLowerCase(), GONE]]);

    await userEvent.click(screen.getByRole('button', { name: 'Remove Where Am I' }));
    expect(changes).toEqual([GONE]);
  });

  it('adds mods found in the Workshop, and by id in capitals', async () => {
    const { changes, searches } = renderEditor(WHERE.toLowerCase());

    await userEvent.type(screen.getByRole('searchbox', { name: "Search Arma Reforger's Workshop" }), 'barrett');
    const results = await screen.findByRole('list', { name: 'Mods found' }, { timeout: 2000 });
    expect(searches).toEqual(['barrett']);
    await userEvent.click(within(results).getByRole('button', { name: 'Add' }));
    expect(changes).toEqual([`${WHERE.toLowerCase()},${BARRETT}`]);

    const input = screen.getByRole('textbox', { name: 'Mods' });
    await userEvent.type(input, `${WHERE}{Enter}`);
    expect(screen.getByRole('alert')).toHaveTextContent('That mod is in the list already.'); // whatever its case
    await userEvent.clear(input);
    await userEvent.type(input, 'not an id{Enter}');
    expect(screen.getByRole('alert')).toHaveTextContent('A Workshop id is 16 characters');
    await userEvent.clear(input);
    await userEvent.type(input, `${GONE.toLowerCase()}{Enter}`);
    await waitFor(() => expect(changes.at(-1)).toBe(`${WHERE.toLowerCase()},${BARRETT},${GONE}`));
  });

  it('adds an id as typed when the Workshop cannot be read', async () => {
    const { changes } = renderEditor('', { down: true });

    await userEvent.type(screen.getByRole('textbox', { name: 'Mods' }), `${BARRETT}{Enter}`);

    await waitFor(() => expect(changes).toEqual([BARRETT]));
    expect(screen.getByRole('list', { name: 'Mods' })).toHaveTextContent(BARRETT);
    expect(screen.getByRole('searchbox')).toBeInTheDocument(); // no key to blame: the search stays
  });

  it('only shows the ids when the user may not change them, without reading the Workshop', () => {
    const { lookups, searches } = renderEditor(WHERE, { disabled: true });

    expect(screen.getByRole('list', { name: 'Mods' })).toHaveTextContent(WHERE);
    expect(screen.queryByRole('button', { name: /Add|Remove/ })).not.toBeInTheDocument();
    expect(lookups).toEqual([]);
    expect(searches).toEqual([]);
  });
});
