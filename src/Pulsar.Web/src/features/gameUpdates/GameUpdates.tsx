import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Download, RefreshCw } from 'lucide-react';
import { Button } from '../../components/ui/Button';
import { Alert, Badge, Card } from '../../components/ui/Display';
import { Checkbox, Field, Input, Select } from '../../components/ui/Form';
import { errorMessage } from '../../lib/errors';
import { useFormatDate } from '../../lib/format';
import { canOnServer, Permission } from '../../lib/permissions';
import { useCurrentUser } from '../auth/api';
import type { ServerDetails } from '../servers/api';
import {
  gameUpdatePolicies,
  useCancelGameUpdate,
  useChangeGameUpdates,
  useCheckGameUpdate,
  useGameUpdates,
  useInstallGameUpdate,
  type GameUpdatePolicy,
  type GameUpdateView,
} from './api';

/**
 * What the last check of the server's game found (Pulsar 1.0.9): up to date, an update and what it brings, a check that
 * failed; the automatic update while it runs or waits. Games whose updates cannot be checked show nothing.
 */
export function GameUpdatesCard({ details }: { details: ServerDetails }) {
  const { t } = useTranslation();
  const me = useCurrentUser();
  const formatDate = useFormatDate();
  const { server } = details;
  const updates = useGameUpdates(server.id);
  const check = useCheckGameUpdate(server.id);
  const install = useInstallGameUpdate(server.id);
  const cancel = useCancelGameUpdate(server.id);
  const view = updates.data;
  if (!view?.supported || server.installState !== 'installed') {
    return null;
  }

  const canInstall = canOnServer(me, server, Permission.ServerInstall);
  // Updating the way an automatic update does stops and starts the server.
  const canRestart =
    canInstall && canOnServer(me, server, Permission.ServerStop) && canOnServer(me, server, Permission.ServerStart);
  const running = view.running?.status === 'running' ? view.running : null;
  const error = check.error ?? install.error ?? cancel.error;
  return (
    <Card className="p-6">
      <div role="region" aria-labelledby="game-updates" className="space-y-3">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <h2 id="game-updates" className="text-lg font-semibold">
            {t('gameUpdates.title')}
          </h2>
          <Status view={view} />
        </div>
        {error && <Alert>{errorMessage(t, error)}</Alert>}
        <Builds view={view} />
        {view.checkErrorCode && (
          <p className="text-sm text-danger">
            {t('gameUpdates.checkFailed', { error: view.checkError ?? view.checkErrorCode })}
          </p>
        )}
        {running && (
          <p role="status" className="text-sm">
            {t('gameUpdates.running', {
              step: running.step + 1,
              steps: running.steps,
              what: t(`gameUpdates.steps.${running.stepKind ?? 'wait'}`),
            })}
          </p>
        )}
        {!running && view.pending && (
          <p role="status" className="text-sm">
            {view.whenEmpty && view.playersOnline != null && view.playersOnline > 0
              ? t('gameUpdates.waitingForPlayers', { players: view.playersOnline })
              : t('gameUpdates.waiting')}
          </p>
        )}
        {!running && view.notBefore && (
          <p className="text-sm text-muted">{t('gameUpdates.notBefore', { when: formatDate(view.notBefore) })}</p>
        )}
        {!running && view.running && view.running.status !== 'succeeded' && (
          <p className="text-sm text-muted">
            {t(`gameUpdates.lastRun.${view.running.status}`, { error: view.running.message ?? view.running.errorCode })}
          </p>
        )}
        <p className="text-xs text-muted">
          {view.policy === 'off'
            ? t('gameUpdates.notChecked')
            : view.checkedAt
              ? t('gameUpdates.checkedAt', { when: formatDate(view.checkedAt) })
              : t('gameUpdates.neverChecked')}
        </p>
        {canInstall && (
          <div className="flex flex-wrap gap-2">
            <Button
              variant="secondary"
              icon={RefreshCw}
              busy={check.isPending}
              disabled={view.checking}
              onClick={() => check.mutate()}
            >
              {view.checking ? t('gameUpdates.checking') : t('gameUpdates.checkNow')}
            </Button>
            {view.available && !running && canRestart && (
              <Button
                icon={Download}
                busy={install.isPending}
                onClick={() =>
                  window.confirm(t('gameUpdates.installConfirm', { name: server.name })) && install.mutate()
                }
              >
                {t('gameUpdates.installNow')}
              </Button>
            )}
            {(running || view.pending) && (
              <Button
                variant="secondary"
                busy={cancel.isPending}
                onClick={() => window.confirm(t('gameUpdates.cancelConfirm')) && cancel.mutate()}
              >
                {running ? t('gameUpdates.cancel') : t('gameUpdates.skip')}
              </Button>
            )}
          </div>
        )}
      </div>
    </Card>
  );
}

function Status({ view }: { view: GameUpdateView }) {
  const { t } = useTranslation();
  if (view.policy === 'off') {
    return <Badge>{t('gameUpdates.status.off')}</Badge>;
  }
  if (view.available === true) {
    return <Badge tone="accent">{t('gameUpdates.status.available')}</Badge>;
  }
  if (view.available === false) {
    return <Badge tone="success">{t('gameUpdates.status.upToDate')}</Badge>;
  }
  return <Badge>{t('gameUpdates.status.unknown')}</Badge>;
}

/** Installed and latest builds, as the game names them (a Steam build id, a Minecraft version). */
function Builds({ view }: { view: GameUpdateView }) {
  const { t } = useTranslation();
  if (view.installed == null && view.latest == null) {
    return null;
  }
  return (
    <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1 text-sm">
      <dt className="text-muted">{t('gameUpdates.installed')}</dt>
      <dd className="font-mono break-all">{view.installed ?? t('gameUpdates.unknownBuild')}</dd>
      <dt className="text-muted">{t('gameUpdates.latest')}</dt>
      <dd className="font-mono break-all">{view.latest ?? t('gameUpdates.unknownBuild')}</dd>
    </dl>
  );
}

/**
 * How the server follows its game's updates: not at all, shown (and notified), or installed by themselves (warning the
 * players first, when nobody plays if asked). Changing it takes the right to update the server; installing by itself
 * also the rights to stop and start it, and to send console commands to warn.
 */
export function GameUpdateSettingsCard({ details }: { details: ServerDetails }) {
  const updates = useGameUpdates(details.server.id);
  const save = useChangeGameUpdates(details.server.id);
  if (!updates.data?.supported) {
    return null;
  }
  // Keyed on what is stored: a save starts the form again from what the panel kept.
  const { policy, whenEmpty, warningMinutes, warningMessage } = updates.data;
  return (
    <GameUpdateSettingsForm
      key={JSON.stringify({ policy, whenEmpty, warningMinutes, warningMessage })}
      details={details}
      view={updates.data}
      save={save}
    />
  );
}

function GameUpdateSettingsForm({
  details,
  view,
  save,
}: {
  details: ServerDetails;
  view: GameUpdateView;
  save: ReturnType<typeof useChangeGameUpdates>;
}) {
  const { t } = useTranslation();
  const me = useCurrentUser();
  const { server } = details;
  const canEdit = canOnServer(me, server, Permission.ServerInstall);
  const [policy, setPolicy] = useState<GameUpdatePolicy>(view.policy);
  const [whenEmpty, setWhenEmpty] = useState(view.whenEmpty);
  const [minutes, setMinutes] = useState(view.warningMinutes.toString());
  const [message, setMessage] = useState(view.warningMessage);
  const [invalid, setInvalid] = useState(false);
  const installs = policy === 'install';
  return (
    <Card className="p-6">
      <div role="region" aria-labelledby="game-update-settings" className="space-y-4">
        <div>
          <h2 id="game-update-settings" className="text-lg font-semibold">
            {t('gameUpdates.settings.title')}
          </h2>
          <p className="mt-1 text-sm text-muted">{t('gameUpdates.settings.hint')}</p>
        </div>
        {save.isError && <Alert>{errorMessage(t, save.error)}</Alert>}
        {save.isSuccess && (
          <p role="status" className="text-sm text-success">
            {t('gameUpdates.settings.saved')}
          </p>
        )}
        {canEdit ? (
          <form
            className="space-y-4"
            noValidate
            onSubmit={(e) => {
              e.preventDefault();
              const warning = Number(minutes);
              const bad = !Number.isInteger(warning) || warning < 0 || warning > 60 || message.length > 100;
              setInvalid(bad);
              if (!bad) {
                save.mutate({ policy, whenEmpty, warningMinutes: warning, warningMessage: message });
              }
            }}
          >
            <Field label={t('gameUpdates.settings.policy')}>
              {(ids) => (
                <Select {...ids} value={policy} onChange={(e) => setPolicy(e.target.value as GameUpdatePolicy)}>
                  {gameUpdatePolicies.map((p) => (
                    <option key={p} value={p}>
                      {t(`gameUpdates.policies.${p}`)}
                    </option>
                  ))}
                </Select>
              )}
            </Field>
            {installs && (
              <>
                <Checkbox
                  label={t('gameUpdates.settings.whenEmpty')}
                  checked={whenEmpty && view.canWaitForEmpty}
                  disabled={!view.canWaitForEmpty}
                  onChange={(e) => setWhenEmpty(e.target.checked)}
                />
                {!view.canWaitForEmpty && <p className="text-xs text-muted">{t('gameUpdates.settings.noQuery')}</p>}
                <div className="grid gap-4 sm:grid-cols-[10rem_1fr]">
                  <Field
                    label={t('gameUpdates.settings.warningMinutes')}
                    error={invalid ? t('gameUpdates.settings.invalid') : undefined}
                  >
                    {(ids) => (
                      <Input
                        {...ids}
                        type="number"
                        min={0}
                        max={60}
                        disabled={!view.canWarn}
                        value={view.canWarn ? minutes : '0'}
                        onChange={(e) => setMinutes(e.target.value)}
                      />
                    )}
                  </Field>
                  <Field
                    label={t('gameUpdates.settings.warningMessage')}
                    hint={t('gameUpdates.settings.warningMessageHint')}
                  >
                    {(ids) => (
                      <Input
                        {...ids}
                        maxLength={100}
                        disabled={!view.canWarn || minutes === '0'}
                        value={message}
                        onChange={(e) => setMessage(e.target.value)}
                      />
                    )}
                  </Field>
                </div>
                {!view.canWarn && <p className="text-xs text-muted">{t('gameUpdates.settings.noBroadcast')}</p>}
              </>
            )}
            <div className="flex justify-end">
              <Button type="submit" busy={save.isPending}>
                {t('actions.save')}
              </Button>
            </div>
          </form>
        ) : (
          <p className="text-sm">{t(`gameUpdates.policies.${view.policy}`)}</p>
        )}
      </div>
    </Card>
  );
}
