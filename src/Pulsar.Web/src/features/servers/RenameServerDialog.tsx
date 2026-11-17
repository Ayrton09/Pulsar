import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from '../../components/ui/Button';
import { Dialog } from '../../components/ui/Dialog';
import { Alert } from '../../components/ui/Display';
import { Field, Input } from '../../components/ui/Form';
import { errorMessage } from '../../lib/errors';
import { useRenameServer, type ServerSummary } from './api';

/** Changes the panel label without touching the game's configuration or restarting it. */
export function RenameServerDialog({ server, onClose }: { server: ServerSummary; onClose: () => void }) {
  const { t } = useTranslation();
  const rename = useRenameServer();
  const [name, setName] = useState(server.name);
  const [showErrors, setShowErrors] = useState(false);
  const trimmed = name.trim();
  const nameError = trimmed === '' ? 'validation.required' : trimmed.length > 64 ? 'validation.tooLong' : null;

  return (
    <Dialog open onOpenChange={(open) => !open && onClose()} title={t('servers.rename.title')}>
      <form
        className="space-y-4"
        onSubmit={(event) => {
          event.preventDefault();
          if (rename.isPending) {
            return;
          }
          if (nameError) {
            setShowErrors(true);
            return;
          }
          rename.mutate({ id: server.id, name: trimmed }, { onSuccess: onClose });
        }}
      >
        <p className="text-sm text-muted">{t('servers.rename.hint')}</p>
        {rename.isError && <Alert>{errorMessage(t, rename.error)}</Alert>}
        <Field label={t('servers.name')} error={showErrors && nameError ? t(nameError) : undefined}>
          {(ids) => (
            <Input
              {...ids}
              value={name}
              onChange={(event) => setName(event.target.value)}
              maxLength={64}
              autoComplete="off"
            />
          )}
        </Field>
        <div className="flex justify-end gap-2">
          <Button variant="secondary" onClick={onClose} disabled={rename.isPending}>
            {t('actions.cancel')}
          </Button>
          <Button type="submit" busy={rename.isPending} disabled={trimmed === server.name}>
            {t('actions.save')}
          </Button>
        </div>
      </form>
    </Dialog>
  );
}
