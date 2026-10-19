import { useTranslation } from 'react-i18next';
import { Alert, Card } from '../../components/ui/Display';
import { Checkbox } from '../../components/ui/Form';
import { errorMessage } from '../../lib/errors';
import { useAppearance, useUpdateAppearance } from './api';

/**
 * Whether the panel shows Steam's game artwork (settings.manage). Off, every game gets its drawn tile and no visitor's
 * browser contacts Steam. Turning it on takes effect on pages loaded afterwards: an open page keeps the security
 * policy it was loaded with.
 */
export function ArtworkSetting() {
  const { t } = useTranslation();
  const appearance = useAppearance();
  const update = useUpdateAppearance();
  if (!appearance.data) {
    return appearance.isError ? <Alert>{errorMessage(t, appearance.error)}</Alert> : null;
  }

  return (
    <Card className="mb-6 space-y-3 p-5">
      <div>
        <h2 className="font-semibold">{t('appearance.title')}</h2>
        <p className="mt-1 text-sm text-muted">{t('appearance.hint')}</p>
      </div>
      <Checkbox
        label={t('appearance.steamArtwork')}
        checked={appearance.data.steamArtwork}
        disabled={update.isPending}
        onChange={(event) => update.mutate(event.target.checked)}
      />
      {update.isError && <Alert>{errorMessage(t, update.error)}</Alert>}
      {update.isSuccess && (
        <p role="status" className="text-sm text-muted">
          {update.data.steamArtwork ? t('appearance.turnedOn') : t('appearance.turnedOff')}
        </p>
      )}
    </Card>
  );
}
