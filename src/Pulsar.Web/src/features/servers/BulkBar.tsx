import { Archive, Download, Play, RotateCw, Square, type LucideIcon } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from '../../components/ui/Button';
import { Dialog } from '../../components/ui/Dialog';
import { Badge } from '../../components/ui/Display';
import { useCurrentUser } from '../auth/api';
import { useServersChanged, type ServerSummary } from './api';
import { bulkActions, runBulk, skipReason, type BulkAction, type BulkOutcome, type BulkResult } from './bulk';

const icons: Record<BulkAction, LucideIcon> = {
  start: Play,
  restart: RotateCw,
  stop: Square,
  backup: Archive,
  update: Download,
};

/** Stopping, restarting or updating several servers interrupts their players: asked first. */
const confirmed = new Set<BulkAction>(['restart', 'stop', 'update']);

const tones: Record<BulkResult, 'success' | 'neutral' | 'danger'> = {
  done: 'success',
  skipped: 'neutral',
  failed: 'danger',
};

/**
 * The servers picked in the list, and what can be done to all of them at once (gap point 14). Each button says to how
 * many it applies; the others are skipped and said why once it ran, with each server's outcome.
 */
export function BulkBar({
  selected,
  onClear,
  onBusyChange,
}: {
  selected: ServerSummary[];
  onClear: () => void;
  /** Whether an action is running on them (the page keeps its selection meanwhile). */
  onBusyChange: (busy: boolean) => void;
}) {
  const { t } = useTranslation();
  const me = useCurrentUser();
  const changed = useServersChanged();
  const [running, setRunning] = useState<BulkAction | null>(null);
  const [outcomes, setOutcomes] = useState<{ action: BulkAction; list: BulkOutcome[] } | null>(null);

  const applicable = (action: BulkAction) =>
    selected.filter((server) => skipReason(action, server, me) === null).length;
  const run = async (action: BulkAction) => {
    const count = applicable(action);
    if (confirmed.has(action) && !window.confirm(t(`servers.bulk.confirm.${action}`, { count }))) {
      return;
    }
    setRunning(action);
    onBusyChange(true);
    const list: BulkOutcome[] = [];
    try {
      await runBulk(t, action, selected, me, (outcome) => list.push(outcome));
    } finally {
      setRunning(null);
      onBusyChange(false);
      changed();
    }
    const order = new Map(selected.map((server, index) => [server.id, index]));
    setOutcomes({ action, list: list.sort((a, b) => (order.get(a.id) ?? 0) - (order.get(b.id) ?? 0)) });
  };

  return (
    <>
      <div
        role="region"
        aria-label={t('servers.bulk.label')}
        className="sticky bottom-4 z-20 mt-6 flex flex-wrap items-center gap-2 rounded-2xl border border-border bg-surface p-3 shadow-xl shadow-black/30"
      >
        <p className="mr-auto px-1 text-sm font-medium" aria-live="polite">
          {t('servers.bulk.selected', { count: selected.length })}
        </p>
        {bulkActions.map((action) => {
          const count = applicable(action);
          return (
            <Button
              key={action}
              size="sm"
              variant={action === 'stop' ? 'dangerSubtle' : 'secondary'}
              icon={icons[action]}
              disabled={count === 0 || running !== null}
              // On a phone, only what applies to some of them: the bar would cover the list otherwise.
              className={count === 0 ? 'max-md:hidden' : ''}
              busy={running === action}
              onClick={() => void run(action)}
            >
              {t(`servers.bulk.actions.${action}`, { count })}
            </Button>
          );
        })}
        <Button size="sm" variant="ghost" disabled={running !== null} onClick={onClear}>
          {t('servers.bulk.clear')}
        </Button>
      </div>
      {outcomes && (
        <Dialog
          open
          onOpenChange={(open) => !open && setOutcomes(null)}
          title={t(`servers.bulk.results.${outcomes.action}`)}
        >
          <ul className="max-h-80 space-y-2 overflow-y-auto">
            {outcomes.list.map((outcome) => (
              <li key={outcome.id} className="flex items-start justify-between gap-3 text-sm">
                <span className="min-w-0">
                  <span className="block truncate font-medium">{outcome.name}</span>
                  {outcome.message && <span className="block text-xs text-muted">{outcome.message}</span>}
                </span>
                <Badge tone={tones[outcome.result]}>{t(`servers.bulk.outcomes.${outcome.result}`)}</Badge>
              </li>
            ))}
          </ul>
          <div className="mt-5 flex justify-end">
            <Button variant="secondary" onClick={() => setOutcomes(null)}>
              {t('actions.close')}
            </Button>
          </div>
        </Dialog>
      )}
    </>
  );
}
