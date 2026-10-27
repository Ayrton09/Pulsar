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
import { parseWorkshopId } from './workshop';

const variable: TemplateVariable = {
  key: 'workshopItems',
  label: 'Workshop items',
  type: 'string',
  default: '',
  required: false,
  secret: false,
  userEditable: true,
  pattern: '^([0-9]+(;[0-9]+)*)?$',
  maxLength: 2048,
  min: null,
  max: null,
  options: [],
  description: null,
  workshop: { appId: 108600, separator: ';' },
};

function item(id: string, changes: Partial<Schemas['WorkshopItemView']> = {}): Schemas['WorkshopItemView'] {
  return {
    id,
    found: true,
    title: `Mod ${id}`,
    otherGame: false,
    size: 2 * 1024 * 1024,
    updatedAt: '2026-10-01T12:00:00Z',
    isCollection: false,
    items: [],
    ...changes,
  };
}

const steam: Record<string, Schemas['WorkshopItemView']> = {
  '111': item('111', { title: 'True Water' }),
  '222': item('222', { found: false, title: null, size: null, updatedAt: null }),
  '333': item('333', { title: 'A hat', otherGame: true }),
  '444': item('444', { title: 'Brita weapons' }),
  '900': item('900', { title: 'Survival pack', isCollection: true, items: ['111', '501', '502'] }),
};

/** The editor as forms use it, keeping the value it reports. */
function Editor({ initial, disabled = false, changes }: { initial: string; disabled?: boolean; changes: string[] }) {
  const [value, setValue] = useState(initial);
  return (
    <VariableFields
      variables={[variable]}
      values={{ workshopItems: value }}
      readOnly={() => disabled}
      onChange={(_, next) => {
        changes.push(next);
        setValue(next);
      }}
    />
  );
}

/** `held`: lookups of that id are answered only once `release` resolves. */
function renderEditor(initial: string, disabled = false, held?: { id: string; release: Promise<void> }) {
  const changes: string[] = [];
  const lookups: string[][] = [];
  fakeApi({
    'POST /api/v1/workshop/lookup': async (request) => {
      const body = (await request.json()) as { appId: number; ids: string[] };
      expect(body.appId).toBe(108600);
      lookups.push(body.ids);
      if (held && body.ids.includes(held.id)) {
        await held.release;
      }
      return json(body.ids.map((id) => steam[id] ?? item(id)));
    },
  });
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <Editor initial={initial} disabled={disabled} changes={changes} />
    </QueryClientProvider>,
  );
  return { changes, lookups };
}

beforeAll(async () => {
  await initI18n('en-US');
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('workshop editor', () => {
  it('reads ids and links', () => {
    expect(parseWorkshopId(' 3806657188 ')).toBe('3806657188');
    expect(parseWorkshopId('https://steamcommunity.com/sharedfiles/filedetails/?id=3806657188&searchtext=')).toBe(
      '3806657188',
    );
    expect(parseWorkshopId('https://steamcommunity.com/workshop/filedetails/?l=english&id=42')).toBe('42');
    expect(parseWorkshopId('0123')).toBeNull();
    expect(parseWorkshopId('mod name')).toBeNull();
  });

  it('shows the items by name and removes them', async () => {
    const { changes, lookups } = renderEditor('111;222;333');

    const list = screen.getByRole('list', { name: 'Workshop items' });
    expect(await within(list).findByRole('link', { name: 'True Water' })).toHaveAttribute(
      'href',
      'https://steamcommunity.com/sharedfiles/filedetails/?id=111',
    );
    expect(within(list).getAllByText(/2 MB/)).toHaveLength(2);
    expect(within(list).getByText(/Steam does not know it/)).toBeInTheDocument();
    expect(within(list).getByText(/It is not in this game's Workshop/)).toBeInTheDocument();
    expect(lookups).toEqual([['111', '222', '333']]);

    await userEvent.click(screen.getByRole('button', { name: 'Remove A hat' }));
    expect(changes).toEqual(['111;222']);
  });

  it('adds items by id or link, and the items of a collection', async () => {
    const { changes } = renderEditor('111');
    const input = screen.getByRole('textbox', { name: 'Workshop items' });

    await userEvent.type(input, 'not an id{Enter}');
    expect(screen.getByRole('alert')).toHaveTextContent('That is not a Workshop id or link.');
    await userEvent.clear(input);
    await userEvent.type(input, '111{Enter}');
    expect(screen.getByRole('alert')).toHaveTextContent('That item is in the list already.');

    await userEvent.clear(input);
    await userEvent.type(input, 'https://steamcommunity.com/sharedfiles/filedetails/?id=444');
    await userEvent.click(screen.getByRole('button', { name: 'Add' }));
    await waitFor(() => expect(changes).toEqual(['111;444']));
    expect(input).toHaveValue('');

    await userEvent.type(input, '900{Enter}');
    const collection = await screen.findByRole('status');
    expect(collection).toHaveTextContent('Survival pack is a collection of 3 items.');
    await userEvent.click(within(collection).getByRole('button', { name: 'Add its 3 items' }));
    expect(changes.at(-1)).toBe('111;444;501;502'); // 111 was there already
  });

  it('adds an item as typed when Steam cannot be asked, and marks it as not checked', async () => {
    const changes: string[] = [];
    fakeApi({ 'POST /api/v1/workshop/lookup': () => problem(503, 'workshop.unavailable') });
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={client}>
        <Editor initial="" changes={changes} />
      </QueryClientProvider>,
    );

    await userEvent.type(screen.getByRole('textbox', { name: 'Workshop items' }), '555{Enter}');

    await waitFor(() => expect(changes).toEqual(['555']));
    const list = screen.getByRole('list', { name: 'Workshop items' });
    expect(await within(list).findByText(/Not checked on Steam/)).toBeInTheDocument();
  });

  it('tells screen readers that an item link opens a new tab', async () => {
    renderEditor('111');

    const link = await screen.findByRole('link', { name: 'True Water' });

    expect(link).toHaveAttribute('target', '_blank');
    expect(link).toHaveAccessibleDescription('(opens in a new tab)');
  });

  it('only shows the ids when the user may not change them, without asking Steam', async () => {
    // Looking items up needs a right to change them: a viewer without it sees the ids, as links.
    const { lookups } = renderEditor('111', true);

    expect(await screen.findByRole('link', { name: '111' })).toHaveAttribute(
      'href',
      'https://steamcommunity.com/sharedfiles/filedetails/?id=111',
    );
    expect(screen.queryByRole('button', { name: 'Add' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Remove/ })).not.toBeInTheDocument();
    expect(lookups).toEqual([]);
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('looks up only the tokens that are Workshop ids', async () => {
    const { lookups } = renderEditor('111;abc;0123;222');

    expect(await screen.findByRole('link', { name: 'True Water' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'abc' })).toBeInTheDocument();
    expect(lookups).toEqual([['111', '222']]);
  });

  it('keeps a removal made while an added item is being looked up', async () => {
    let release = () => {};
    const { changes } = renderEditor('111;333', false, {
      id: '444',
      release: new Promise<void>((resolve) => {
        release = resolve;
      }),
    });
    await screen.findByRole('link', { name: 'A hat' });

    await userEvent.type(screen.getByRole('textbox', { name: 'Workshop items' }), '444{Enter}');
    await userEvent.click(screen.getByRole('button', { name: 'Remove A hat' }));
    expect(changes).toEqual(['111']);
    release();

    await waitFor(() => expect(changes).toEqual(['111', '111;444']));
  });

  it('adds both of two items added quickly', async () => {
    let release = () => {};
    const { changes } = renderEditor('111', false, {
      id: '444',
      release: new Promise<void>((resolve) => {
        release = resolve;
      }),
    });
    const input = screen.getByRole('textbox', { name: 'Workshop items' });

    await userEvent.type(input, '444{Enter}');
    // The text stays while it is looked up: the user clears it for the next one.
    await userEvent.clear(input);
    await userEvent.type(input, '333{Enter}');
    await waitFor(() => expect(changes).toEqual(['111;333']));
    release();

    await waitFor(() => expect(changes).toEqual(['111;333', '111;333;444']));
  });
});
