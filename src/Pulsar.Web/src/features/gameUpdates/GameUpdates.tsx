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
import { useUpgradeTemplate, type ServerDetails } from '../servers/api';
import { serverActions } from '../servers/serverStatus';
import {
  gameUpdatePolicies,
  useCancelGameUpdate,
  useChangeGameUpdates,
  useChangeSteamBranch,
  useCheckGameUpdate,
  useGameUpdates,
  useInstallGameUpdate,
  type GameBranch,
  type GameUpdatePolicy,
  type GameUpdateView,
} from './api';

/**
 * What the last check of the server's game found (Pulsar 1.0.9): up to date, an update and what it brings, a check that
 * failed; the automatic update while it runs or waits, and since 1.0.10 the automatic move to a newer template of the
 * game. Games whose updates cannot be checked show only that move.
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
  const running = view?.running?.status === 'running' ? view.running : null;
  // A move to a newer template installs the server again: it is shown while it does.
  const checks = view?.supported === true && server.installState === 'installed';
  if (!view || !(checks || running?.template === true || view.templatePending)) {
    return null;
  }

  const canInstall = canOnServer(me, server, Permission.ServerInstall);
  const stopped = server.runtimeState === 'offline' || server.runtimeState === 'crashed';
  // Updating the way an automatic update does stops and starts the server.
  const canRestart =
    canInstall && canOnServer(me, server, Permission.ServerStop) && canOnServer(me, server, Permission.ServerStart);
  const error = check.error ?? install.error ?? cancel.error;
  const last = !running && view.running && view.running.status !== 'succeeded' ? view.running : null;
  return (
    <Card className="p-6">
      <div role="region" aria-labelledby="game-updates" className="space-y-3">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <h2 id="game-updates" className="text-lg font-semibold">
            {t('gameUpdates.title')}
          </h2>
          {checks && <Status view={view} />}
        </div>
        {error && <Alert>{errorMessage(t, error)}</Alert>}
        {checks && <Builds view={view} />}
        {checks && view.checkErrorCode && (
          <p className="text-sm text-danger">
            {t('gameUpdates.checkFailed', { error: view.checkError ?? view.checkErrorCode })}
          </p>
        )}
        {running && (
          <p role="status" className="text-sm">
            {t(
              running.steamBranch
                ? 'gameUpdates.runningBranch'
                : running.template
                  ? 'gameUpdates.runningTemplate'
                  : 'gameUpdates.running',
              {
                step: running.step + 1,
                steps: running.steps,
                what: t(`gameUpdates.steps.${running.stepKind ?? 'wait'}`),
              },
            )}
          </p>
        )}
        {!running && view.templatePending && view.autoTemplate && (
          <p role="status" className="text-sm">
            {view.whenEmpty && view.playersOnline != null && view.playersOnline > 0
              ? t('gameUpdates.templateWaitingForPlayers', { version: view.autoTemplate, players: view.playersOnline })
              : t('gameUpdates.templateWaiting', { version: view.autoTemplate })}
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
        {last && (
          <p className="text-sm text-muted">
            {t(
              `gameUpdates.${last.steamBranch ? 'lastBranchRun' : last.template ? 'lastTemplateRun' : 'lastRun'}.${last.status}`,
              {
                error: last.message ?? last.errorCode,
              },
            )}
          </p>
        )}
        {checks && view.branchesSupported && (
          // Keyed by the branch it follows: changed elsewhere (another tab, a change that did not end well), the picker starts over.
          <SteamBranchPicker
            key={view.currentBranch ?? 'public'}
            server={server}
            view={view}
            stopped={stopped}
            running={!!running}
          />
        )}
        {checks && (
          <p className="text-xs text-muted">
            {view.policy === 'off'
              ? t('gameUpdates.notChecked')
              : view.checkedAt
                ? t('gameUpdates.checkedAt', { when: formatDate(view.checkedAt) })
                : t('gameUpdates.neverChecked')}
          </p>
        )}
        {canInstall && (
          <div className="flex flex-wrap gap-2">
            {checks && (
              <Button
                variant="secondary"
                icon={RefreshCw}
                busy={check.isPending}
                disabled={view.checking}
                onClick={() => check.mutate()}
              >
                {view.checking ? t('gameUpdates.checking') : t('gameUpdates.checkNow')}
              </Button>
            )}
            {checks && view.available && !running && canRestart && (
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
            {(running || view.pending || view.templatePending) && (
              <Button
                variant="secondary"
                busy={cancel.isPending}
                onClick={() =>
                  window.confirm(
                    t(running?.template ? 'gameUpdates.cancelTemplateConfirm' : 'gameUpdates.cancelConfirm'),
                  ) && cancel.mutate()
                }
              >
                {running
                  ? t(running.template ? 'gameUpdates.cancelTemplate' : 'gameUpdates.cancel')
                  : t(view.pending ? 'gameUpdates.skip' : 'gameUpdates.skipTemplate')}
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

/**
 * The Steam branch the game follows (Pulsar 1.0.14), among those the last check listed: changing it backs the stopped
 * server up, then installs the branch. A branch with a password cannot be chosen (Pulsar does not ask for it).
 */
function SteamBranchPicker({
  server,
  view,
  stopped,
  running,
}: {
  server: ServerDetails['server'];
  view: GameUpdateView;
  stopped: boolean;
  running: boolean;
}) {
  const { t } = useTranslation();
  const me = useCurrentUser();
  const formatDate = useFormatDate();
  const change = useChangeSteamBranch(server.id);
  const current = view.currentBranch ?? view.steamBranch ?? 'public';
  const branches: GameBranch[] = view.branches?.length
    ? view.branches
    : [{ name: 'public', build: null, description: null, passwordRequired: false, updated: null }];
  const [chosen, setChosen] = useState(current);
  const may =
    canOnServer(me, server, Permission.ServerInstall) && canOnServer(me, server, Permission.ServerBackupsCreate);
  const label = (branch: GameBranch) =>
    [
      branch.name,
      branch.description,
      branch.build ? t('gameUpdates.branch.build', { build: branch.build }) : null,
      branch.updated ? formatDate(branch.updated) : null,
      branch.passwordRequired ? t('gameUpdates.branch.password') : null,
    ]
      .filter(Boolean)
      .join(' · ');
  return (
    <div className="space-y-2 border-t border-border pt-3">
      <Field label={t('gameUpdates.branch.label')} hint={t('gameUpdates.branch.hint')}>
        {(ids) => (
          <Select {...ids} value={chosen} disabled={!may || running} onChange={(e) => setChosen(e.target.value)}>
            {branches.map((branch) => (
              <option key={branch.name} value={branch.name} disabled={branch.passwordRequired}>
                {label(branch)}
              </option>
            ))}
          </Select>
        )}
      </Field>
      {!view.branches?.length && <p className="text-xs text-muted">{t('gameUpdates.branch.unknown')}</p>}
      {change.error && <Alert>{errorMessage(t, change.error)}</Alert>}
      {may && (
        <div className="flex flex-wrap items-center gap-2">
          <Button
            variant="secondary"
            busy={change.isPending}
            disabled={chosen === current || !stopped || running}
            onClick={() =>
              window.confirm(t('gameUpdates.branch.confirm', { name: server.name, branch: chosen })) &&
              change.mutate(chosen)
            }
          >
            {t('gameUpdates.branch.change')}
          </Button>
          {!stopped && <p className="text-xs text-muted">{t('gameUpdates.branch.stopFirst')}</p>}
        </div>
      )}
    </div>
  );
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
      {view.currentBranch && view.currentBranch !== 'public' && (
        <>
          <dt className="text-muted">{t('gameUpdates.branch.label')}</dt>
          <dd className="font-mono break-all">{view.currentBranch}</dd>
        </>
      )}
    </dl>
  );
}

/**
 * How the server follows its game's updates and, since 1.0.10, the newer versions of its game's template: not at all,
 * shown (and notified), or installed by themselves (warning the players first, when nobody plays if asked). Every game
 * has templates; only some have updates Pulsar can check. Changing it takes the right to update the server; installing
 * by itself also the rights to stop and start it, and to send console commands to warn.
 */
export function GameUpdateSettingsCard({ details }: { details: ServerDetails }) {
  const updates = useGameUpdates(details.server.id);
  const save = useChangeGameUpdates(details.server.id);
  if (!updates.data) {
    return null;
  }
  // Keyed on what is stored: a save starts the form again from what the panel kept.
  const { policy, templatePolicy, whenEmpty, warningMinutes, warningMessage } = updates.data;
  return (
    <GameUpdateSettingsForm
      key={JSON.stringify({ policy, templatePolicy, whenEmpty, warningMinutes, warningMessage })}
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
  const [templatePolicy, setTemplatePolicy] = useState<GameUpdatePolicy>(view.templatePolicy ?? 'notify');
  const [whenEmpty, setWhenEmpty] = useState(view.whenEmpty);
  const [minutes, setMinutes] = useState(view.warningMinutes.toString());
  const [message, setMessage] = useState(view.warningMessage);
  const [invalid, setInvalid] = useState(false);
  const installs = (view.supported && policy === 'install') || templatePolicy === 'install';
  return (
    <Card className="p-6">
      <div role="region" aria-labelledby="game-update-settings" className="space-y-4">
        <div>
          <h2 id="game-update-settings" className="text-lg font-semibold">
            {t('gameUpdates.settings.title')}
          </h2>
          <p className="mt-1 text-sm text-muted">
            {view.supported
              ? `${t('gameUpdates.settings.hint')} ${t('gameUpdates.settings.templateHint')}`
              : t('gameUpdates.settings.templateHint')}
          </p>
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
                save.mutate({ policy, templatePolicy, whenEmpty, warningMinutes: warning, warningMessage: message });
              }
            }}
          >
            {view.supported && (
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
            )}
            <Field label={t('gameUpdates.settings.templatePolicy')}>
              {(ids) => (
                <Select
                  {...ids}
                  value={templatePolicy}
                  onChange={(e) => setTemplatePolicy(e.target.value as GameUpdatePolicy)}
                >
                  {gameUpdatePolicies.map((p) => (
                    <option key={p} value={p}>
                      {t(`gameUpdates.templatePolicies.${p}`)}
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
                {t('gameUpdates.settings.save')}
              </Button>
            </div>
          </form>
        ) : (
          <div className="space-y-1 text-sm">
            {view.supported && <p>{t(`gameUpdates.policies.${view.policy}`)}</p>}
            <p>{t(`gameUpdates.templatePolicies.${view.templatePolicy ?? 'notify'}`)}</p>
          </div>
        )}
      </div>
    </Card>
  );
}

/**
 * What can be updated, where the server's actions are (its page's header): an update of its game the last check
 * found, and a newer version of its game's template. Each with its action for whoever may take it; the cards below
 * still say how updates are followed.
 */
export function UpdateBanner({ details }: { details: ServerDetails }) {
  const { t } = useTranslation();
  const me = useCurrentUser();
  const { server } = details;
  const updates = useGameUpdates(server.id);
  const install = useInstallGameUpdate(server.id);
  const upgrade = useUpgradeTemplate();
  const view = updates.data;
  const canInstall = canOnServer(me, server, Permission.ServerInstall);
  const running = view?.running?.status === 'running';
  const game =
    view?.supported === true &&
    server.installState === 'installed' &&
    view.available === true &&
    !running &&
    !view.pending;
  const next = canInstall && !running ? details.templateUpgrade : null;
  if (!game && !next) {
    return null;
  }

  // Updating the way an automatic update does stops and starts the server.
  const canRestart =
    canInstall && canOnServer(me, server, Permission.ServerStop) && canOnServer(me, server, Permission.ServerStart);
  const stopped = server.runtimeState === 'offline' || server.runtimeState === 'crashed';
  const idle = server.activeOperation === null && server.nodeConnected;
  // Moving to a newer template installs the server again: only when it could be reinstalled.
  const canMove = serverActions(server).reinstall;
  const error = install.error ?? upgrade.error;
  return (
    <div role="region" aria-label={t('gameUpdates.banner.label')} className="space-y-2">
      {game && (
        <div className="flex flex-wrap items-center justify-between gap-3 rounded-lg border border-accent/40 bg-accent/10 px-4 py-3">
          <p className="inline-flex min-w-0 items-center gap-2 text-sm">
            <Download aria-hidden="true" className="size-4 shrink-0 text-accent" />
            <span className="break-words">
              {view.installed && view.latest
                ? t('gameUpdates.banner.game', { installed: view.installed, latest: view.latest })
                : t('gameUpdates.banner.gameNoBuilds')}
            </span>
          </p>
          {canRestart && (
            <Button
              size="sm"
              icon={Download}
              disabled={!idle}
              busy={install.isPending}
              onClick={() => window.confirm(t('gameUpdates.installConfirm', { name: server.name })) && install.mutate()}
            >
              {t('gameUpdates.installNow')}
            </Button>
          )}
        </div>
      )}
      {next && (
        <div className="flex flex-wrap items-center justify-between gap-3 rounded-lg border border-accent/40 bg-accent/10 px-4 py-3">
          <p className="min-w-0 text-sm break-words">
            {t('servers.templateUpgrade', { current: details.templateVersion, next: next.version })}
            {view?.templatePolicy === 'install' && (
              <>
                {' '}
                {view.autoTemplate === next.version
                  ? t('servers.templateUpgradeAuto')
                  : t('servers.templateUpgradeImported')}
              </>
            )}
            {!stopped && <span className="text-muted"> {t('gameUpdates.banner.stopForTemplate')}</span>}
          </p>
          <Button
            size="sm"
            variant="secondary"
            disabled={!canMove}
            busy={upgrade.isPending}
            onClick={() =>
              window.confirm(t('servers.templateUpgradeConfirm', { name: server.name, version: next.version })) &&
              upgrade.mutate({ id: server.id, templateId: next.templateId })
            }
          >
            {t('servers.templateUpgradeAction', { version: next.version })}
          </Button>
        </div>
      )}
      {error && <Alert>{errorMessage(t, error)}</Alert>}
    </div>
  );
}
