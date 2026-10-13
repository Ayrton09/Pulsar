import { useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, Spinner } from '../../components/ui/Button';
import { Dialog } from '../../components/ui/Dialog';
import { Alert, Badge, Card, Table, Td, Th } from '../../components/ui/Display';
import { Checkbox, Field, Input } from '../../components/ui/Form';
import { copyText } from '../../lib/clipboard';
import { errorMessage } from '../../lib/errors';
import { useFormatDate } from '../../lib/format';
import {
  useCreateReusableToken,
  useEnrollmentTokens,
  useRevokeReusableToken,
  type CreatedEnrollmentToken,
  type EnrollmentTokenView,
} from './api';
import { enrollCommand, msiCommand } from './nodeStatus';

/**
 * Reusable enrollment tokens (Phase 14): one command, or one MSI deployment through Intune or a group policy, enrolls
 * many machines, each as a new node. By default their nodes wait for approval.
 */
export function EnrollmentTokensSection() {
  const { t } = useTranslation();
  const tokens = useEnrollmentTokens();
  const revoke = useRevokeReusableToken();
  const [creating, setCreating] = useState(false);
  const [showFinished, setShowFinished] = useState(false);
  const error = tokens.error ?? revoke.error;
  // Expired, used up and revoked tokens only tell the history: folded away so the usable ones stay readable.
  const usable = tokens.data?.filter((token) => token.usable) ?? [];
  const finished = tokens.data?.filter((token) => !token.usable) ?? [];

  return (
    <section className="mt-10">
      <div className="mb-4 flex flex-wrap items-end justify-between gap-3">
        <div>
          <h2 className="mb-1 text-lg font-semibold">{t('enrollmentTokens.title')}</h2>
          <p className="text-sm text-muted">{t('enrollmentTokens.description')}</p>
        </div>
        <Button variant="secondary" onClick={() => setCreating(true)}>
          {t('enrollmentTokens.create')}
        </Button>
      </div>
      {tokens.isPending && <Spinner label={t('common.loading')} />}
      {error && <Alert>{errorMessage(t, error)}</Alert>}
      {tokens.data?.length === 0 && (
        <Card className="p-6">
          <p className="text-sm text-muted">{t('enrollmentTokens.empty')}</p>
        </Card>
      )}
      {usable.length > 0 && (
        <TokenTable
          label={t('enrollmentTokens.title')}
          tokens={usable}
          revoking={revoke.isPending ? revoke.variables : undefined}
          onRevoke={(token) =>
            window.confirm(t('enrollmentTokens.revokeConfirm', { name: token.name })) && revoke.mutate(token.id)
          }
        />
      )}
      {usable.length === 0 && finished.length > 0 && (
        <p className="text-sm text-muted">{t('enrollmentTokens.noneUsable')}</p>
      )}
      {finished.length > 0 && (
        <div className="mt-3 space-y-3">
          <Button variant="ghost" size="sm" aria-expanded={showFinished} onClick={() => setShowFinished(!showFinished)}>
            {showFinished
              ? t('enrollmentTokens.hideFinished')
              : t('enrollmentTokens.showFinished', { count: finished.length })}
          </Button>
          {showFinished && <TokenTable label={t('enrollmentTokens.finished')} tokens={finished} />}
        </div>
      )}
      {creating && <CreateTokenDialog onClose={() => setCreating(false)} />}
    </section>
  );
}

function TokenTable({
  label,
  tokens,
  revoking,
  onRevoke,
}: {
  label: string;
  tokens: EnrollmentTokenView[];
  /** The token being revoked. */
  revoking?: string | undefined;
  /** Offered on usable tokens. */
  onRevoke?: (token: EnrollmentTokenView) => void;
}) {
  const { t } = useTranslation();
  const formatDate = useFormatDate();
  return (
    <Table label={label}>
      <thead>
        <tr>
          <Th>{t('enrollmentTokens.name')}</Th>
          <Th>{t('enrollmentTokens.uses')}</Th>
          <Th>{t('enrollmentTokens.expires')}</Th>
          <Th>{t('enrollmentTokens.state')}</Th>
          <Th>
            <span className="sr-only">{t('common.actions')}</span>
          </Th>
        </tr>
      </thead>
      <tbody>
        {tokens.map((token) => (
          <tr key={token.id}>
            <Td>
              <p className="font-medium">{token.name}</p>
              <code className="font-mono text-xs text-muted">{token.tokenPrefix}…</code>
            </Td>
            <Td>
              {t('enrollmentTokens.usesValue', { uses: token.uses, max: token.maxUses })}
              <p className="text-xs text-muted">
                {token.requireApproval ? t('enrollmentTokens.withApproval') : t('enrollmentTokens.withoutApproval')}
              </p>
            </Td>
            <Td>{formatDate(token.expiresAt)}</Td>
            <Td>
              <TokenState token={token} />
            </Td>
            <Td className="text-right">
              {token.usable && onRevoke && (
                <Button variant="secondary" size="sm" busy={revoking === token.id} onClick={() => onRevoke(token)}>
                  {t('enrollmentTokens.revoke')}
                </Button>
              )}
            </Td>
          </tr>
        ))}
      </tbody>
    </Table>
  );
}

function TokenState({ token }: { token: EnrollmentTokenView }) {
  const { t } = useTranslation();
  if (token.revokedAt) {
    return <Badge>{t('enrollmentTokens.states.revoked')}</Badge>;
  }
  if (token.uses >= token.maxUses) {
    return <Badge>{t('enrollmentTokens.states.usedUp')}</Badge>;
  }
  return token.usable ? (
    <Badge tone="success">{t('enrollmentTokens.states.usable')}</Badge>
  ) : (
    <Badge>{t('enrollmentTokens.states.expired')}</Badge>
  );
}

function CreateTokenDialog({ onClose }: { onClose: () => void }) {
  const { t } = useTranslation();
  const create = useCreateReusableToken();
  const [name, setName] = useState('');
  const [maxUses, setMaxUses] = useState('10');
  const [days, setDays] = useState('7');
  const [requireApproval, setRequireApproval] = useState(true);
  const [created, setCreated] = useState<CreatedEnrollmentToken | null>(null);

  if (created) {
    return (
      <Dialog
        open
        onOpenChange={(open) => !open && onClose()}
        title={t('enrollmentTokens.createdTitle', { name: created.token.name })}
        size="lg"
      >
        <CreatedToken created={created} />
        <div className="flex justify-end pt-6">
          <Button onClick={onClose}>{t('actions.close')}</Button>
        </div>
      </Dialog>
    );
  }

  const submit = (event: FormEvent) => {
    event.preventDefault();
    create.mutate(
      { name: name.trim(), maxUses: Number(maxUses), lifetimeHours: Math.round(Number(days) * 24), requireApproval },
      { onSuccess: setCreated },
    );
  };

  return (
    // Not while the token is being created: the dialog going away would lose its one-time value.
    <Dialog open onOpenChange={(open) => !open && !create.isPending && onClose()} title={t('enrollmentTokens.create')}>
      <form onSubmit={submit} className="space-y-4" noValidate>
        {create.isError && <Alert>{errorMessage(t, create.error)}</Alert>}
        <Field label={t('enrollmentTokens.name')} hint={t('enrollmentTokens.nameHint')}>
          {(ids) => <Input {...ids} value={name} onChange={(e) => setName(e.target.value)} autoComplete="off" />}
        </Field>
        <div className="grid gap-4 sm:grid-cols-2">
          <Field label={t('enrollmentTokens.maxUses')} hint={t('enrollmentTokens.maxUsesHint')}>
            {(ids) => (
              <Input
                {...ids}
                type="number"
                min={1}
                max={1000}
                value={maxUses}
                onChange={(e) => setMaxUses(e.target.value)}
              />
            )}
          </Field>
          <Field label={t('enrollmentTokens.days')} hint={t('enrollmentTokens.daysHint')}>
            {(ids) => (
              <Input {...ids} type="number" min={1} max={90} value={days} onChange={(e) => setDays(e.target.value)} />
            )}
          </Field>
        </div>
        <Checkbox
          label={t('enrollmentTokens.requireApproval')}
          checked={requireApproval}
          onChange={(e) => setRequireApproval(e.target.checked)}
        />
        <p className="text-xs text-muted">
          {requireApproval ? t('enrollmentTokens.requireApprovalHint') : t('enrollmentTokens.noApprovalWarning')}
        </p>
        <div className="flex justify-end gap-2 pt-2">
          <Button variant="secondary" disabled={create.isPending} onClick={onClose}>
            {t('actions.cancel')}
          </Button>
          <Button type="submit" busy={create.isPending} disabled={name.trim() === ''}>
            {t('actions.create')}
          </Button>
        </div>
      </form>
    </Dialog>
  );
}

/** The token, shown once, in the two commands that use it. */
function CreatedToken({ created }: { created: CreatedEnrollmentToken }) {
  const { t } = useTranslation();
  const formatDate = useFormatDate();
  const origin = window.location.origin;
  const commands = [
    {
      label: t('enrollmentTokens.msi'),
      text: msiCommand(origin, created.value, created.panelKeyFingerprint, created.agentPackage),
    },
    { label: t('enrollmentTokens.script'), text: enrollCommand(origin, created.value, created.panelKeyFingerprint) },
  ];

  return (
    <div className="space-y-4">
      <p className="text-sm">{t('enrollmentTokens.once')}</p>
      {commands.map((command) => (
        <CopyBlock key={command.label} label={command.label} text={command.text} />
      ))}
      <ul className="list-disc space-y-1 pl-5 text-xs text-muted">
        <li>
          {t('enrollmentTokens.limits', {
            uses: created.token.maxUses,
            expires: formatDate(created.token.expiresAt),
          })}
        </li>
        <li>
          {created.token.requireApproval ? t('enrollmentTokens.approvalNote') : t('enrollmentTokens.noApprovalNote')}
        </li>
        <li>{t('enrollmentTokens.msiNote', { file: created.agentPackage })}</li>
        <li>{t('nodes.enroll.trust')}</li>
      </ul>
    </div>
  );
}

function CopyBlock({ label, text }: { label: string; text: string }) {
  const { t } = useTranslation();
  const [copied, setCopied] = useState<boolean | null>(null);
  return (
    <div className="space-y-1">
      <p className="text-sm font-medium">{label}</p>
      <div className="relative">
        <pre
          aria-label={label}
          className="overflow-x-auto rounded-md border border-border bg-bg p-3 pr-20 font-mono text-xs break-all whitespace-pre-wrap"
        >
          {text}
        </pre>
        <Button
          variant="secondary"
          size="sm"
          className="absolute top-2 right-2"
          onClick={() => void copyText(text).then(setCopied)}
        >
          {t('nodes.enroll.copy')}
        </Button>
      </div>
      {copied !== null && (
        <p role="status" className={`text-xs ${copied ? 'text-success' : 'text-danger'}`}>
          {copied ? t('nodes.enroll.copied') : t('nodes.enroll.copyFailed')}
        </p>
      )}
    </div>
  );
}
