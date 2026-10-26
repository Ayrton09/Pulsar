import { useId, useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from '../../components/ui/Button';
import { Alert, Badge, Card } from '../../components/ui/Display';
import { Field, Input } from '../../components/ui/Form';
import { errorMessage } from '../../lib/errors';
import { useCatalogSettings, useRemoveCurseForgeKey, useSetCurseForgeKey } from './api';

/** Where CurseForge gives keys to applications such as Pulsar (its terms forbid sharing one: each panel brings its own). */
const curseForgeConsole = 'https://console.curseforge.com/';

/**
 * The administrator's CurseForge API key (settings.manage; Pulsar 1.0.9): with it, the servers' Mods tabs search
 * CurseForge too. Checked with CurseForge before it is kept, kept encrypted, never shown again: the field is for a new one.
 */
export function CurseForgeKeySetting() {
  const { t } = useTranslation();
  const settings = useCatalogSettings();
  const save = useSetCurseForgeKey();
  const remove = useRemoveCurseForgeKey();
  const [key, setKey] = useState('');
  const noteId = useId();
  if (!settings.data) {
    return settings.isError ? <Alert>{errorMessage(t, settings.error)}</Alert> : null;
  }

  const configured = settings.data.curseForgeConfigured;
  const submit = (event: FormEvent) => {
    event.preventDefault();
    remove.reset();
    save.mutate(key.trim(), { onSuccess: () => setKey('') });
  };
  const forget = () => {
    if (window.confirm(t('mods.curseForgeKey.removeConfirm'))) {
      save.reset();
      remove.mutate();
    }
  };

  return (
    <Card className="mb-6 space-y-3 p-5">
      <div className="flex flex-wrap items-start justify-between gap-2">
        <div>
          <h2 className="font-semibold">{t('mods.curseForgeKey.title')}</h2>
          <p className="mt-1 max-w-2xl text-sm text-muted">
            {t('mods.curseForgeKey.hint')}{' '}
            <a
              href={curseForgeConsole}
              target="_blank"
              rel="noreferrer noopener"
              aria-describedby={noteId}
              className="text-accent hover:underline"
            >
              console.curseforge.com
            </a>
            <span id={noteId} className="sr-only">
              {t('common.opensInNewTab')}
            </span>
          </p>
        </div>
        {configured ? (
          <Badge tone="success">{t('mods.curseForgeKey.configured')}</Badge>
        ) : (
          <Badge>{t('mods.curseForgeKey.missing')}</Badge>
        )}
      </div>
      <form onSubmit={submit} className="flex flex-wrap items-end gap-2" noValidate>
        <div className="min-w-64 flex-1">
          <Field label={configured ? t('mods.curseForgeKey.replace') : t('mods.curseForgeKey.label')}>
            {(control) => (
              <Input
                {...control}
                type="password"
                autoComplete="off"
                spellCheck={false}
                maxLength={256}
                value={key}
                onChange={(e) => setKey(e.target.value)}
              />
            )}
          </Field>
        </div>
        <Button type="submit" busy={save.isPending} disabled={key.trim().length === 0 || remove.isPending}>
          {t('mods.curseForgeKey.save')}
        </Button>
        {configured && (
          <Button variant="dangerSubtle" busy={remove.isPending} disabled={save.isPending} onClick={forget}>
            {t('mods.curseForgeKey.remove')}
          </Button>
        )}
      </form>
      {(save.error ?? remove.error) && <Alert>{errorMessage(t, save.error ?? remove.error)}</Alert>}
      {save.isSuccess && (
        <p role="status" className="text-sm text-muted">
          {t('mods.curseForgeKey.saved')}
        </p>
      )}
    </Card>
  );
}
