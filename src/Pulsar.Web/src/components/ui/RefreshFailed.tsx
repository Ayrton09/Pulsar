import { useTranslation } from 'react-i18next';
import { errorMessage } from '../../lib/errors';
import { Button } from './Button';

/**
 * Fetching again what a page already shows failed: the page keeps its last data (and whatever is being edited on it)
 * and says so, with a retry.
 */
export function RefreshFailed({ error, retry }: { error: unknown; retry: () => void }) {
  const { t } = useTranslation();
  return (
    <div
      role="status"
      className="mb-4 flex flex-wrap items-center justify-between gap-2 rounded-md border border-danger/40 bg-danger/10 px-3 py-2 text-sm text-danger"
    >
      <span>{t('common.refreshFailed', { error: errorMessage(t, error) })}</span>
      <Button variant="secondary" size="sm" onClick={retry}>
        {t('actions.retry')}
      </Button>
    </div>
  );
}
