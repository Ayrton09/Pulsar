import { Plus } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Button } from '../../components/ui/Button';
import { GameArt } from '../games/GameArt';
import type { Schemas } from '../../api/client';
import { useGameText } from './gameText';

type TemplateView = Schemas['TemplateView'];

/** One game template as a row of the list shown without game artwork: what its card says, without the picture. */
export function GameRow({ template, onCreate }: { template: TemplateView; onCreate?: (() => void) | undefined }) {
  const { t } = useTranslation();
  const description = useGameText(template.source === 'bundled' ? template.key : undefined).description(
    template.description,
  );
  return (
    <article
      aria-labelledby={`game-card-${template.id}`}
      className="flex flex-col gap-3 px-4 py-3 sm:flex-row sm:items-center sm:gap-6"
    >
      <div className="min-w-0 flex-1">
        <div className="flex min-w-0 flex-wrap items-center gap-2">
          <h2 id={`game-card-${template.id}`} className="min-w-0 truncate text-base font-semibold">
            {template.name}
          </h2>
          <span
            className={`rounded-full border border-border px-2 py-0.5 text-xs ${
              template.source === 'bundled' ? 'text-muted' : 'text-accent'
            }`}
          >
            {t(`templates.sources.${template.source}`)}
          </span>
        </div>
        <p className="truncate text-xs text-muted">
          <code>{template.key}</code> · {t('templates.versionValue', { version: template.version })}
        </p>
        {description && <p className="mt-1 line-clamp-2 text-sm text-muted">{description}</p>}
      </div>
      {onCreate && (
        <div className="shrink-0">
          <Button
            size="sm"
            variant="secondary"
            icon={Plus}
            aria-label={t('templates.createServerFor', { name: template.name, version: template.version })}
            onClick={onCreate}
          >
            {t('templates.createServer')}
          </Button>
        </div>
      )}
    </article>
  );
}

/** One game template: its picture, what it is and, for those who may, a shortcut to create a server with it. */
export function GameCard({ template, onCreate }: { template: TemplateView; onCreate?: (() => void) | undefined }) {
  const { t } = useTranslation();
  // A bundled game's description in the reader's language; an imported version of it says what its author wrote.
  const description = useGameText(template.source === 'bundled' ? template.key : undefined).description(
    template.description,
  );
  return (
    <article
      aria-labelledby={`game-card-${template.id}`}
      className="flex w-full min-w-0 flex-col overflow-hidden rounded-2xl border border-border bg-surface/90 shadow-lg shadow-black/20"
    >
      <div className="relative aspect-[460/215] overflow-hidden">
        <GameArt templateKey={template.key} kind="header" className="absolute inset-0 size-full" />
        <div
          aria-hidden="true"
          className="absolute inset-x-0 bottom-0 h-1/3 bg-linear-to-t from-surface to-transparent"
        />
        <span
          className={`absolute top-3 left-3 rounded-full border border-white/10 bg-black/60 px-2.5 py-1 text-xs font-medium backdrop-blur-sm ${
            template.source === 'bundled' ? 'text-white' : 'text-accent'
          }`}
        >
          {t(`templates.sources.${template.source}`)}
        </span>
      </div>
      <div className="flex flex-1 flex-col gap-3 px-4 pb-4">
        <div className="min-w-0">
          <h2 id={`game-card-${template.id}`} className="truncate text-base font-semibold">
            {template.name}
          </h2>
          <p className="truncate text-xs text-muted">
            <code>{template.key}</code> · {t('templates.versionValue', { version: template.version })}
          </p>
        </div>
        {description && <p className="line-clamp-3 text-sm text-muted">{description}</p>}
        {onCreate && (
          <div className="mt-auto pt-1">
            <Button
              size="sm"
              variant="secondary"
              icon={Plus}
              aria-label={t('templates.createServerFor', { name: template.name, version: template.version })}
              onClick={onCreate}
            >
              {t('templates.createServer')}
            </Button>
          </div>
        )}
      </div>
    </article>
  );
}
