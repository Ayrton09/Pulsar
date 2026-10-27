import { useMutation, useQuery } from '@tanstack/react-query';
import { useEffect, useId, useLayoutEffect, useRef, useState, type FormEvent, type KeyboardEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { ApiError, api, unwrap, type Schemas } from '../../api/client';
import { Button } from '../../components/ui/Button';
import { Input } from '../../components/ui/Form';
import { errorMessage } from '../../lib/errors';
import { visibleName } from '../../lib/visibleText';
import type { TemplateVariable } from './api';

type Project = Schemas['CurseForgeProjectView'];

const LOOKUP_LIMIT = 100;
/** How long typing pauses before CurseForge is asked: each search is a request with the administrator's key. */
const SEARCH_DELAY = 400;

function lookup(gameId: number, ids: string[]) {
  return unwrap(api.POST('/api/v1/curseforge/lookup', { body: { gameId, ids } }));
}

/** A CurseForge project's id: a positive number without leading zeros. */
function parseProjectId(text: string): string | null {
  const id = text.trim();
  return /^[1-9][0-9]{0,9}$/.test(id) && Number(id) <= 2147483647 ? id : null;
}

interface CurseForgeListFieldProps {
  variable: TemplateVariable & { curseForge: Schemas['TemplateCurseForge'] };
  value: string;
  disabled: boolean;
  onChange: (value: string) => void;
  control: { id: string; 'aria-describedby'?: string; 'aria-invalid'?: true };
}

/**
 * A variable that lists CurseForge projects (Pulsar 1.0.14; ARK: Survival Ascended's mods): the projects by name, looked
 * up on CurseForge through the panel with the administrator's key, found by a search of the game's mods or added by id,
 * and removed one by one. Without a key the ids are added as typed. The value stays the game's list.
 */
export function CurseForgeListField({ variable, value, disabled, onChange, control }: CurseForgeListFieldProps) {
  const { t } = useTranslation();
  const { gameId, separator } = variable.curseForge;
  const ids = split(value, separator);
  const lookupIds = ids.filter((id) => parseProjectId(id) === id);
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
    queryKey: ['curseforge', gameId, lookupIds],
    queryFn: async () => {
      const found = new Map<string, Project>();
      for (let i = 0; i < lookupIds.length; i += LOOKUP_LIMIT) {
        for (const project of await lookup(gameId, lookupIds.slice(i, i + LOOKUP_LIMIT))) {
          found.set(project.id, project);
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
    queryKey: ['curseforge-search', gameId, query],
    queryFn: () => unwrap(api.GET('/api/v1/curseforge/search', { params: { query: { gameId, query, offset: 0 } } })),
    enabled: !disabled && query.length >= 2,
    staleTime: 5 * 60 * 1000,
    retry: false,
  });
  const check = useMutation({ mutationFn: (id: string) => lookup(gameId, [id]) });
  // Without the panel's key CurseForge cannot be asked: ids still go in as typed.
  const noKey = [details.error, search.error, check.error].some(
    (error) => error instanceof ApiError && error.code === 'mods.catalog_not_configured',
  );

  const set = (next: string[]) => {
    const joined = next.join(separator);
    if (variable.maxLength != null && joined.length > variable.maxLength) {
      setProblem(t('servers.curseforge.tooMany'));
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
  };

  const submit = (event?: FormEvent) => {
    event?.preventDefault();
    const id = parseProjectId(text);
    if (!id) {
      setProblem(t('servers.curseforge.invalid'));
      return;
    }
    if (ids.includes(id)) {
      setProblem(t('servers.curseforge.duplicate'));
      return;
    }
    setProblem(null);
    // Looked up first, so it shows by name; CurseForge out of reach (or no key), it goes in as typed. Each call appends
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
        <p className="text-sm text-muted">{t('servers.curseforge.empty')}</p>
      ) : (
        <ul aria-label={variable.label} className="divide-y divide-border rounded-md border border-border">
          {ids.map((id) => (
            <ProjectRow
              key={id}
              id={id}
              project={details.data?.get(id)}
              loading={details.isFetching && !details.data?.has(id)}
              disabled={disabled}
              onRemove={() => set(ids.filter((other) => other !== id))}
            />
          ))}
        </ul>
      )}
      {noKey ? (
        <p className="text-xs text-muted">{t('servers.curseforge.needsKey')}</p>
      ) : (
        details.isError && <p className="text-xs text-danger">{errorMessage(t, details.error)}</p>
      )}
      {!disabled && (
        <div className="flex gap-2">
          <Input
            {...control}
            value={text}
            inputMode="numeric"
            placeholder={t('servers.curseforge.placeholder')}
            onChange={(e) => setText(e.target.value)}
            onKeyDown={(e: KeyboardEvent<HTMLInputElement>) => e.key === 'Enter' && submit(e)}
          />
          <Button type="button" variant="secondary" busy={check.isPending} onClick={() => submit()}>
            {t('servers.curseforge.add')}
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
            aria-label={t('servers.curseforge.search')}
            placeholder={t('servers.curseforge.search')}
            maxLength={100}
            value={words}
            onChange={(e) => setWords(e.target.value)}
          />
          {search.isError && <p className="text-xs text-danger">{errorMessage(t, search.error)}</p>}
          {search.data && search.data.hits.length === 0 && (
            <p className="text-xs text-muted">{t('servers.curseforge.noResults')}</p>
          )}
          {search.data && search.data.hits.length > 0 && (
            <ul
              aria-label={t('servers.curseforge.results')}
              className="divide-y divide-border rounded-md border border-border"
            >
              {search.data.hits.map((hit) => (
                <li key={hit.id} className="flex items-center justify-between gap-3 px-3 py-2 text-sm">
                  <div className="min-w-0">
                    <span className="break-words">{visibleName(hit.name ?? hit.id)}</span>
                    <p className="text-xs text-muted">
                      {[hit.id, hit.author ? t('servers.curseforge.by', { author: hit.author }) : null]
                        .filter(Boolean)
                        .join(' · ')}
                    </p>
                  </div>
                  <Button
                    type="button"
                    variant="secondary"
                    size="sm"
                    disabled={ids.includes(hit.id)}
                    onClick={() => append(hit.id)}
                  >
                    {ids.includes(hit.id) ? t('servers.curseforge.added') : t('servers.curseforge.add')}
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

function ProjectRow({
  id,
  project,
  loading,
  disabled,
  onRemove,
}: {
  id: string;
  project: Project | undefined;
  loading: boolean;
  disabled: boolean;
  onRemove: () => void;
}) {
  const { t } = useTranslation();
  const noteId = useId();
  // An author chose the name: its hidden characters are shown, not acted on.
  const name = project?.found ? visibleName(project.name ?? id) : id;
  return (
    <li className="flex items-center justify-between gap-3 px-3 py-2 text-sm">
      <div className="min-w-0">
        {project?.url ? (
          <>
            <a
              href={project.url}
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
        <p className="text-xs text-muted">
          {[
            project?.found ? id : null,
            project?.author ? t('servers.curseforge.by', { author: project.author }) : null,
            loading ? t('common.loading') : null,
            project && !project.found ? t('servers.curseforge.notFound') : null,
            project?.otherGame ? t('servers.curseforge.otherGame') : null,
          ]
            .filter(Boolean)
            .join(' · ')}
        </p>
      </div>
      {!disabled && (
        <Button
          type="button"
          variant="secondary"
          aria-label={t('servers.curseforge.remove', { name })}
          onClick={onRemove}
        >
          {t('servers.curseforge.removeShort')}
        </Button>
      )}
    </li>
  );
}
