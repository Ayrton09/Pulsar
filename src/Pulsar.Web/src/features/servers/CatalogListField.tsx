import { useMutation, useQuery } from '@tanstack/react-query';
import { useEffect, useId, useLayoutEffect, useRef, useState, type FormEvent, type KeyboardEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { ApiError } from '../../api/client';
import { Button } from '../../components/ui/Button';
import { Input } from '../../components/ui/Form';
import { errorMessage } from '../../lib/errors';
import { useFormatFileSize } from '../../lib/format';
import { visibleName } from '../../lib/visibleText';
import type { TemplateVariable } from './api';
import type { Catalog, CatalogItem } from './catalogs';

/** How long typing pauses before the catalog is asked: each search is a request to it. */
const SEARCH_DELAY = 400;

interface CatalogListFieldProps {
  variable: TemplateVariable;
  separator: string;
  catalog: Catalog;
  value: string;
  disabled: boolean;
  onChange: (value: string) => void;
  control: { id: string; 'aria-describedby'?: string; 'aria-invalid'?: true };
}

/**
 * A variable that lists a catalog's projects (Pulsar 1.0.14; ARK: Survival Ascended's mods on CurseForge, Arma
 * Reforger's Workshop mods): the projects by name, looked up through the panel, found by a search or added by id, and
 * removed one by one. When the catalog cannot be asked (CurseForge without the panel's key) the ids are added as typed.
 * The value stays the game's list.
 */
export function CatalogListField({
  variable,
  separator,
  catalog,
  value,
  disabled,
  onChange,
  control,
}: CatalogListFieldProps) {
  const { t } = useTranslation();
  const texts = `servers.${catalog.texts}`;
  const ids = split(value, separator);
  const lookupIds = ids.filter((id) => catalog.parseId(id) !== null);
  // The value when a lookup ends, not when it began: the list may have changed meanwhile.
  const latest = useRef(value);
  useLayoutEffect(() => {
    latest.current = value;
  }, [value]);
  const [text, setText] = useState('');
  const [words, setWords] = useState('');
  const [query, setQuery] = useState('');
  const [problem, setProblem] = useState<string | null>(null);
  useEffect(() => {
    const timer = setTimeout(() => setQuery(words.trim()), SEARCH_DELAY);
    return () => clearTimeout(timer);
  }, [words]);

  const details = useQuery({
    queryKey: [...catalog.key, lookupIds],
    queryFn: async () => {
      const found = new Map<string, CatalogItem>();
      for (let i = 0; i < lookupIds.length; i += catalog.lookupLimit) {
        for (const item of await catalog.lookup(lookupIds.slice(i, i + catalog.lookupLimit))) {
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
  const search = useQuery({
    queryKey: [...catalog.key, 'search', query],
    queryFn: () => catalog.search(query),
    enabled: !disabled && query.length >= 2,
    staleTime: 5 * 60 * 1000,
    retry: false,
  });
  const check = useMutation({ mutationFn: (id: string) => catalog.lookup([id]) });
  // Without the panel's key CurseForge cannot be asked: ids still go in as typed.
  const noKey = [details.error, search.error, check.error].some(
    (error) => error instanceof ApiError && error.code === 'mods.catalog_not_configured',
  );
  const listed = (id: string) => ids.some((other) => same(other, id));

  const set = (next: string[]) => {
    const joined = next.join(separator);
    if (variable.maxLength != null && joined.length > variable.maxLength) {
      setProblem(t(`${texts}.tooMany`));
      return;
    }
    setProblem(null);
    latest.current = joined;
    onChange(joined);
  };

  const append = (id: string) => {
    const current = split(latest.current, separator);
    if (!current.some((other) => same(other, id))) {
      set([...current, id]);
    }
  };

  const submit = (event?: FormEvent) => {
    event?.preventDefault();
    const id = catalog.parseId(text);
    if (!id) {
      setProblem(t(`${texts}.invalid`));
      return;
    }
    if (listed(id)) {
      setProblem(t(`${texts}.duplicate`));
      return;
    }
    setProblem(null);
    // Looked up first, so it shows by name; the catalog out of reach (or no key), it goes in as typed. Each call appends
    // its own id: mutate's own callbacks would run only for the last of two quick adds.
    void check.mutateAsync(id).then(
      () => append(id),
      () => append(id),
    );
    setText('');
  };

  return (
    <div className="space-y-2">
      {ids.length === 0 ? (
        <p className="text-sm text-muted">{t(`${texts}.empty`)}</p>
      ) : (
        <ul aria-label={variable.label} className="divide-y divide-border rounded-md border border-border">
          {ids.map((id) => (
            <ItemRow
              key={id}
              texts={texts}
              id={id}
              item={details.data?.get(id)}
              loading={details.isFetching && !details.data?.has(id)}
              disabled={disabled}
              onRemove={() => set(ids.filter((other) => other !== id))}
            />
          ))}
        </ul>
      )}
      {noKey ? (
        <p className="text-xs text-muted">{t(`${texts}.needsKey`)}</p>
      ) : (
        details.isError && <p className="text-xs text-danger">{errorMessage(t, details.error)}</p>
      )}
      {!disabled && (
        <div className="flex gap-2">
          <Input
            {...control}
            value={text}
            inputMode={catalog.numeric ? 'numeric' : 'text'}
            placeholder={t(`${texts}.placeholder`)}
            onChange={(e) => setText(e.target.value)}
            onKeyDown={(e: KeyboardEvent<HTMLInputElement>) => e.key === 'Enter' && submit(e)}
          />
          <Button type="button" variant="secondary" busy={check.isPending} onClick={() => submit()}>
            {t(`${texts}.add`)}
          </Button>
        </div>
      )}
      {problem && (
        <p role="alert" className="text-xs text-danger">
          {problem}
        </p>
      )}
      {!disabled && !noKey && (
        <div className="space-y-2">
          <Input
            type="search"
            aria-label={t(`${texts}.search`)}
            placeholder={t(`${texts}.search`)}
            maxLength={100}
            value={words}
            onChange={(e) => setWords(e.target.value)}
          />
          {search.isError && <p className="text-xs text-danger">{errorMessage(t, search.error)}</p>}
          {search.data && search.data.hits.length === 0 && (
            <p className="text-xs text-muted">{t(`${texts}.noResults`)}</p>
          )}
          {search.data && search.data.hits.length > 0 && (
            <ul aria-label={t(`${texts}.results`)} className="divide-y divide-border rounded-md border border-border">
              {search.data.hits.map((hit) => (
                <li key={hit.id} className="flex items-center justify-between gap-3 px-3 py-2 text-sm">
                  <div className="min-w-0">
                    <span className="break-words">{visibleName(hit.name ?? hit.id)}</span>
                    <Notes texts={texts} id={hit.id} item={hit} />
                  </div>
                  <Button
                    type="button"
                    variant="secondary"
                    size="sm"
                    disabled={listed(hit.id)}
                    onClick={() => append(hit.id)}
                  >
                    {listed(hit.id) ? t(`${texts}.added`) : t(`${texts}.add`)}
                  </Button>
                </li>
              ))}
            </ul>
          )}
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

/** Ids of one project: Arma Reforger's are hexadecimal, in capitals or not. */
function same(a: string, b: string) {
  return a.toUpperCase() === b.toUpperCase();
}

/** Its id, author, version and size, and why it is suspect. */
function Notes({
  texts,
  id,
  item,
  loading = false,
}: {
  texts: string;
  id: string;
  item: CatalogItem | undefined;
  loading?: boolean;
}) {
  const { t } = useTranslation();
  const formatSize = useFormatFileSize();
  return (
    <p className="text-xs text-muted">
      {[
        item?.found ? id : null,
        item?.author ? t(`${texts}.by`, { author: item.author }) : null,
        item?.version ? `v${item.version}` : null,
        item?.size ? formatSize(item.size) : null,
        loading ? t('common.loading') : null,
        item && !item.found ? t(`${texts}.notFound`) : null,
        item?.otherGame ? t(`${texts}.otherGame`) : null,
      ]
        .filter(Boolean)
        .join(' · ')}
    </p>
  );
}

function ItemRow({
  texts,
  id,
  item,
  loading,
  disabled,
  onRemove,
}: {
  texts: string;
  id: string;
  item: CatalogItem | undefined;
  loading: boolean;
  disabled: boolean;
  onRemove: () => void;
}) {
  const { t } = useTranslation();
  const noteId = useId();
  // An author chose the name: its hidden characters are shown, not acted on.
  const name = item?.found ? visibleName(item.name ?? id) : id;
  return (
    <li className="flex items-center justify-between gap-3 px-3 py-2 text-sm">
      <div className="min-w-0">
        {item?.url ? (
          <>
            <a
              href={item.url}
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
          </>
        ) : (
          <span className="break-words">{name}</span>
        )}
        <Notes texts={texts} id={id} item={item} loading={loading} />
      </div>
      {!disabled && (
        <Button type="button" variant="secondary" aria-label={t(`${texts}.remove`, { name })} onClick={onRemove}>
          {t(`${texts}.removeShort`)}
        </Button>
      )}
    </li>
  );
}
