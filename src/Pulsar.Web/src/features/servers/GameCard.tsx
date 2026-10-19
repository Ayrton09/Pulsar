import { Plus } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Button } from '../../components/ui/Button';
import { GameArt } from '../games/GameArt';
import type { Schemas } from '../../api/client';

type TemplateView = Schemas['TemplateView'];

/** One game template: its picture, what it is and, for those who may, a shortcut to create a server with it. */
export function GameCard({ template, onCreate }: { template: TemplateView; onCreate?: (() => void) | undefined }) {
  const { t } = useTranslation();
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
        {template.description && <p className="line-clamp-3 text-sm text-muted">{template.description}</p>}
        {onCreate && (
          <div className="mt-auto pt-1">
            <Button
              size="sm"
              variant="secondary"
              icon={Plus}
              aria-label={t('templates.createServerFor', { name: template.name })}
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
