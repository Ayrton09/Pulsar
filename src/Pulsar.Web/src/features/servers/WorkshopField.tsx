import { useMutation, useQuery } from '@tanstack/react-query';
import { useId, useLayoutEffect, useRef, useState, type FormEvent, type KeyboardEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { api, unwrap, type Schemas } from '../../api/client';
import { Button } from '../../components/ui/Button';
import { Input } from '../../components/ui/Form';
import { errorMessage } from '../../lib/errors';
import { useFormatDate, useFormatFileSize } from '../../lib/format';
import { visibleName } from '../../lib/visibleText';
import type { TemplateVariable } from './api';
import { parseWorkshopId, workshopUrl } from './workshop';

type WorkshopItem = Schemas['WorkshopItemView'];

const LOOKUP_LIMIT = 100;

function lookup(appId: number, ids: string[]) {
  return unwrap(api.POST('/api/v1/workshop/lookup', { body: { appId, ids } }));
}

interface WorkshopFieldProps {
  variable: TemplateVariable & { workshop: Schemas['TemplateWorkshop'] };
  value: string;
  disabled: boolean;
  onChange: (value: string) => void;
  control: { id: string; 'aria-describedby'?: string; 'aria-invalid'?: true };
}

/**
 * A variable that lists Steam Workshop items (Project Zomboid's): the items by name, looked up on Steam through the
 * panel, added by id or link (a collection adds its items) and removed one by one. The value stays the game's list.
 */
export function WorkshopField({ variable, value, disabled, onChange, control }: WorkshopFieldProps) {
  const { t } = useTranslation();
  const { appId, separator } = variable.workshop;
  const ids = split(value, separator);
  // Only what the panel accepts as ids is looked up: one other token would make it refuse the whole batch.
  const lookupIds = ids.filter((id) => parseWorkshopId(id) === id);
  // The value when a lookup ends, not when it began: the list may have changed meanwhile.
  const latest = useRef(value);
  useLayoutEffect(() => {
    latest.current = value;
  }, [value]);
  const [text, setText] = useState('');
  const [problem, setProblem] = useState<string | null>(null);
  const [collection, setCollection] = useState<WorkshopItem | null>(null);
  // Added while Steam could not be asked: kept as typed, and said so in the list.
  const [unchecked, setUnchecked] = useState<ReadonlySet<string>>(new Set());

  const details = useQuery({
    queryKey: ['workshop', appId, lookupIds],
    queryFn: async () => {
      const found = new Map<string, WorkshopItem>();
      for (let i = 0; i < lookupIds.length; i += LOOKUP_LIMIT) {
        for (const item of await lookup(appId, lookupIds.slice(i, i + LOOKUP_LIMIT))) {
          found.set(item.id, item);
        }
      }
      return found;
    },
    // Looking up needs the right to change the list: read-only, the ids are shown as they are.
    enabled: lookupIds.length > 0 && !disabled,
    staleTime: 10 * 60 * 1000,
    retry: false,
  });

  const add = useMutation({ mutationFn: (id: string) => lookup(appId, [id]) });

  const set = (next: string[]) => {
    const joined = next.join(separator);
    if (variable.maxLength != null && joined.length > variable.maxLength) {
      setProblem(t('servers.workshop.tooMany'));
      return;
    }
    setProblem(null);
    latest.current = joined;
    onChange(joined);
  };

  const append = (id: string) => {
    const current = split(latest.current, separator);
    if (!current.includes(id)) {
      set([...current, id]);
    }
    setText('');
  };

  const submit = (event?: FormEvent) => {
    event?.preventDefault();
    const id = parseWorkshopId(text);
    setCollection(null);
    if (!id) {
      setProblem(t('servers.workshop.invalid'));
      return;
    }
    if (ids.includes(id)) {
      setProblem(t('servers.workshop.duplicate'));
      return;
    }
    setProblem(null);
    add.mutate(id, {
      onSuccess: ([item]) => {
        if (item?.isCollection) {
          setCollection(item);
          return;
        }
        append(id);
      },
      // Steam could not be asked: the id still goes in, as typed, marked as not checked.
      onError: () => {
        setUnchecked((current) => new Set(current).add(id));
        append(id);
      },
    });
  };

  const addCollection = (items: string[]) => {
    set([...ids, ...items.filter((id) => !ids.includes(id))]);
    setCollection(null);
    setText('');
  };

  return (
    <div className="space-y-2">
      {ids.length === 0 ? (
        <p className="text-sm text-muted">{t('servers.workshop.empty')}</p>
      ) : (
        <ul aria-label={variable.label} className="divide-y divide-border rounded-md border border-border">
          {ids.map((id) => (
            <WorkshopRow
              key={id}
              id={id}
              item={details.data?.get(id)}
              loading={details.isFetching && !details.data?.has(id)}
              unchecked={unchecked.has(id)}
              disabled={disabled}
              onRemove={() => set(ids.filter((other) => other !== id))}
            />
          ))}
        </ul>
      )}
      {details.isError && <p className="text-xs text-danger">{errorMessage(t, details.error)}</p>}
      {!disabled && (
        <div className="flex gap-2">
          <Input
            {...control}
            value={text}
            placeholder={t('servers.workshop.placeholder')}
            onChange={(e) => setText(e.target.value)}
            onKeyDown={(e: KeyboardEvent<HTMLInputElement>) => e.key === 'Enter' && submit(e)}
          />
          <Button type="button" variant="secondary" busy={add.isPending} onClick={() => submit()}>
            {t('servers.workshop.add')}
          </Button>
        </div>
      )}
      {problem && (
        <p role="alert" className="text-xs text-danger">
          {problem}
        </p>
      )}
      {collection && (
        <div role="status" className="flex flex-wrap items-center gap-2 rounded-md border border-border p-3 text-sm">
          <span className="min-w-0 break-words">
            {t('servers.workshop.collection', {
              title: collection.title ?? collection.id,
              count: collection.items.length,
            })}
          </span>
          <Button type="button" variant="secondary" onClick={() => addCollection(collection.items)}>
            {t('servers.workshop.addCollection', { count: collection.items.length })}
          </Button>
        </div>
      )}
    </div>
  );
}

function split(value: string, separator: string) {
  return value
    .split(separator)
    .map((id) => id.trim())
    .filter(Boolean);
}

function WorkshopRow({
  id,
  item,
  loading,
  unchecked,
  disabled,
  onRemove,
}: {
  id: string;
  item: WorkshopItem | undefined;
  loading: boolean;
  /** Added while Steam could not be asked. */
  unchecked: boolean;
  disabled: boolean;
  onRemove: () => void;
}) {
  const { t } = useTranslation();
  const formatSize = useFormatFileSize();
  const formatDate = useFormatDate();
  const noteId = useId();
  // A Workshop author chose the title: its hidden characters are shown, not acted on.
  const name = item?.found ? visibleName(item.title ?? id) : id;
  const facts = item?.found
    ? [
        item.size != null ? formatSize(item.size) : null,
        item.updatedAt ? t('servers.workshop.updated', { when: formatDate(item.updatedAt) }) : null,
      ]
    : [];
  return (
    <li className="flex items-center justify-between gap-3 px-3 py-2 text-sm">
      <div className="min-w-0">
        <a
          href={workshopUrl(id)}
          aria-describedby={noteId}
          target="_blank"
          rel="noreferrer noopener"
          className="break-words hover:text-accent hover:underline"
        >
          {name}
        </a>
        <span id={noteId} className="sr-only">
          {t('common.opensInNewTab')}
        </span>
        <p className="text-xs text-muted">
          {[
            item?.found ? id : null,
            ...facts,
            loading ? t('common.loading') : null,
            !item && !loading && unchecked ? t('servers.workshop.notChecked') : null,
            item && !item.found ? t('servers.workshop.notFound') : null,
            item?.otherGame ? t('servers.workshop.otherGame') : null,
            item?.isCollection ? t('servers.workshop.isCollection') : null,
          ]
            .filter(Boolean)
            .join(' · ')}
        </p>
      </div>
      {!disabled && (
        <Button
          type="button"
          variant="secondary"
          aria-label={t('servers.workshop.remove', { name })}
          onClick={onRemove}
        >
          {t('servers.workshop.removeShort')}
        </Button>
      )}
    </li>
  );
}
