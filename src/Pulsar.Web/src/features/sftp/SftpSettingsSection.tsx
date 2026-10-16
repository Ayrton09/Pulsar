import { useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, Spinner } from '../../components/ui/Button';
import { Alert, Badge, Card } from '../../components/ui/Display';
import { Checkbox, Field, Input } from '../../components/ui/Form';
import { errorMessage } from '../../lib/errors';
import { useSftpSettings, useUpdateSftpSettings, type SftpSettings } from './api';
import { CopyButton } from './CopyButton';

/**
 * The SFTP server's settings (settings.manage): whether it runs, its port and the host users are told to connect to.
 * Turning it off or changing its port restarts it and ends every SFTP connection.
 */
export function SftpSettingsSection() {
  const { t } = useTranslation();
  const settings = useSftpSettings();
  const data = settings.data;

  return (
    <section className="mt-10">
      <h2 className="mb-1 text-lg font-semibold">{t('sftpSettings.title')}</h2>
      <p className="mb-4 text-sm text-muted">{t('sftpSettings.description')}</p>
      {settings.isPending && <Spinner label={t('common.loading')} />}
      {settings.isError && <Alert>{errorMessage(t, settings.error)}</Alert>}
      {data && <SftpSettingsForm saved={data} />}
    </section>
  );
}

function SftpSettingsForm({ saved }: { saved: SftpSettings }) {
  const { t } = useTranslation();
  const update = useUpdateSftpSettings();
  const [enabled, setEnabled] = useState(saved.enabled);
  const [port, setPort] = useState(String(saved.port));
  const [publicHost, setPublicHost] = useState(saved.publicHost ?? '');
  const [portProblem, setPortProblem] = useState(false);
  const [done, setDone] = useState(false);

  const submit = (event: FormEvent) => {
    event.preventDefault();
    setDone(false);
    const number = Number(port);
    if (!/^\d+$/.test(port.trim()) || number < 1 || number > 65535) {
      setPortProblem(true);
      return;
    }
    setPortProblem(false);

    // Turning it off or moving it ends every SFTP connection.
    const ends = saved.enabled && (!enabled || number !== saved.port);
    if (ends && !window.confirm(t(enabled ? 'sftpSettings.portConfirm' : 'sftpSettings.offConfirm'))) {
      return;
    }
    update.mutate({ enabled, port: number, publicHost: publicHost.trim() || null }, { onSuccess: () => setDone(true) });
  };

  return (
    <Card className="p-6">
      <form onSubmit={submit} className="space-y-4" noValidate>
        {update.isError && <Alert>{errorMessage(t, update.error)}</Alert>}
        {done && (
          <p role="status" className="text-sm text-success">
            {t('users.saved')}
          </p>
        )}
        <Checkbox label={t('sftpSettings.enabled')} checked={enabled} onChange={(e) => setEnabled(e.target.checked)} />
        <Field
          label={t('sftpSettings.port')}
          hint={t('sftpSettings.portHint')}
          error={portProblem ? t('errors.sftp.invalid_port') : undefined}
        >
          {(ids) => (
            <Input {...ids} value={port} onChange={(e) => setPort(e.target.value)} inputMode="numeric" maxLength={5} />
          )}
        </Field>
        <Field label={t('sftpSettings.publicHost')} hint={t('sftpSettings.publicHostHint')}>
          {(ids) => (
            <Input
              {...ids}
              value={publicHost}
              onChange={(e) => setPublicHost(e.target.value)}
              maxLength={253}
              autoComplete="off"
            />
          )}
        </Field>
        <div className="space-y-1">
          <p className="text-sm font-medium">{t('sftp.fingerprint')}</p>
          <div className="flex flex-wrap items-center gap-2">
            <code className="min-w-0 font-mono text-xs break-all">{saved.hostKeyFingerprint}</code>
            <CopyButton text={saved.hostKeyFingerprint} label={t('sftp.copy', { what: t('sftp.fingerprint') })} />
          </div>
        </div>
        <div className="flex flex-wrap items-center justify-between gap-3">
          {saved.enabled ? (
            <Badge tone={saved.listening ? 'success' : 'danger'}>
              {t(saved.listening ? 'sftpSettings.listening' : 'sftpSettings.notListening')}
            </Badge>
          ) : (
            <Badge tone="neutral">{t('sftpSettings.off')}</Badge>
          )}
          <Button type="submit" variant="secondary" busy={update.isPending}>
            {t('sftpSettings.save')}
          </Button>
        </div>
        {saved.enabled && !saved.listening && (
          <p className="text-xs text-danger">{t('sftpSettings.notListeningHint')}</p>
        )}
      </form>
    </Card>
  );
}
