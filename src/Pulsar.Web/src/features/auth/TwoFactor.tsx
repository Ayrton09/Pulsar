import { useMemo, useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { encode } from 'uqr';
import { Button } from '../../components/ui/Button';
import { Alert, Badge, Card } from '../../components/ui/Display';
import { Dialog } from '../../components/ui/Dialog';
import { Field, Input } from '../../components/ui/Form';
import { errorMessage } from '../../lib/errors';
import {
  useDisableTwoFactor,
  useEnableTwoFactor,
  useRenewRecoveryCodes,
  useSessionRefresh,
  useTwoFactorSetup,
  useTwoFactorStatus,
} from './api';

/** A QR code drawn as SVG (dark modules on white, also in the dark theme, so phone cameras read it). */
export function QrCode({ value, label }: { value: string; label: string }) {
  const { path, size } = useMemo(() => {
    const qr = encode(value, { ecc: 'M', border: 2 });
    let d = '';
    qr.data.forEach((row, y) =>
      row.forEach((dark, x) => {
        if (dark) {
          d += `M${x} ${y}h1v1h-1z`;
        }
      }),
    );
    return { path: d, size: qr.size };
  }, [value]);
  return (
    <svg
      role="img"
      aria-label={label}
      viewBox={`0 0 ${size} ${size}`}
      className="h-48 w-48"
      shapeRendering="crispEdges"
    >
      <rect width={size} height={size} fill="#fff" />
      <path d={path} fill="#000" />
    </svg>
  );
}

/** Recovery codes, shown once; the user confirms they saved them. */
function RecoveryCodes({ codes, onDone }: { codes: readonly string[]; onDone: () => void }) {
  const { t } = useTranslation();
  const [copied, setCopied] = useState(false);
  const copy = () => {
    void navigator.clipboard?.writeText(codes.join('\n')).then(() => setCopied(true));
  };
  return (
    <div className="space-y-4">
      <p className="text-sm">{t('twoFactor.codesIntro')}</p>
      <ul
        aria-label={t('twoFactor.codesLabel')}
        className="grid grid-cols-2 gap-2 rounded-md border border-border p-3 font-mono text-sm"
      >
        {codes.map((code) => (
          <li key={code}>{code}</li>
        ))}
      </ul>
      <div className="flex flex-wrap justify-end gap-2">
        <Button variant="secondary" onClick={copy}>
          {copied ? t('twoFactor.copied') : t('twoFactor.copy')}
        </Button>
        <Button onClick={onDone}>{t('twoFactor.saved')}</Button>
      </div>
    </div>
  );
}

/**
 * From a new key to two-factor authentication on: QR code, the key as text, a code from the app, recovery codes.
 * `onStarted` tells the parent a setup is under way, so a refetched status saying "on" does not hide
 * the recovery codes before the user saved them.
 */
export function TwoFactorSetup({ onStarted, onDone }: { onStarted?: () => void; onDone: () => void }) {
  const { t } = useTranslation();
  const setup = useTwoFactorSetup();
  const enable = useEnableTwoFactor();
  const [code, setCode] = useState('');

  if (enable.data) {
    return <RecoveryCodes codes={enable.data.recoveryCodes} onDone={onDone} />;
  }

  if (!setup.data) {
    return (
      <div className="space-y-4">
        {setup.isError && <Alert>{errorMessage(t, setup.error)}</Alert>}
        <p className="text-sm text-muted">{t('twoFactor.intro')}</p>
        <Button
          onClick={() => {
            onStarted?.();
            setup.mutate();
          }}
          busy={setup.isPending}
        >
          {t('twoFactor.setUp')}
        </Button>
      </div>
    );
  }

  const submit = (event: FormEvent) => {
    event.preventDefault();
    enable.mutate({ code: code.replace(/[\s-]/g, '') });
  };

  return (
    <form onSubmit={submit} className="space-y-4" noValidate>
      <p className="text-sm">{t('twoFactor.scan')}</p>
      <div className="flex justify-center">
        <QrCode value={setup.data.authenticatorUri} label={t('twoFactor.qrLabel')} />
      </div>
      <p className="text-sm text-muted">{t('twoFactor.manual')}</p>
      <p className="rounded-md border border-border px-3 py-2 text-center font-mono text-sm break-all select-all">
        {setup.data.sharedKey}
      </p>
      {enable.isError && <Alert>{errorMessage(t, enable.error)}</Alert>}
      <Field label={t('twoFactor.appCode')}>
        {(ids) => (
          <Input
            {...ids}
            value={code}
            onChange={(e) => setCode(e.target.value)}
            inputMode="numeric"
            autoComplete="one-time-code"
            maxLength={9}
          />
        )}
      </Field>
      <div className="flex justify-end">
        <Button type="submit" busy={enable.isPending} disabled={code.replace(/[\s-]/g, '').length !== 6}>
          {t('twoFactor.turnOn')}
        </Button>
      </div>
    </form>
  );
}

type PasswordAction = 'disable' | 'renew';

/** Two-factor authentication on the account page: status, setup, new recovery codes, turning it off. */
export function TwoFactorSection() {
  const { t } = useTranslation();
  const status = useTwoFactorStatus();
  const refresh = useSessionRefresh();
  const [settingUp, setSettingUp] = useState(false);
  const [action, setAction] = useState<PasswordAction | null>(null);
  const [newCodes, setNewCodes] = useState<readonly string[] | null>(null);

  return (
    <Card className="p-6">
      <div className="mb-4 flex items-center justify-between gap-4">
        <h2 className="text-lg font-semibold">{t('twoFactor.title')}</h2>
        {status.data && (
          <Badge tone={status.data.enabled ? 'success' : 'neutral'}>
            {status.data.enabled ? t('twoFactor.on') : t('twoFactor.off')}
          </Badge>
        )}
      </div>
      {status.isError && <Alert>{errorMessage(t, status.error)}</Alert>}
      {status.data && (settingUp || !status.data.enabled) && (
        <TwoFactorSetup
          onStarted={() => setSettingUp(true)}
          onDone={() => {
            setSettingUp(false);
            void refresh();
          }}
        />
      )}
      {status.data?.enabled &&
        !settingUp &&
        (newCodes ? (
          <RecoveryCodes codes={newCodes} onDone={() => setNewCodes(null)} />
        ) : (
          <div className="space-y-4">
            <p className="text-sm text-muted">{t('twoFactor.codesLeft', { count: status.data.recoveryCodesLeft })}</p>
            {status.data.required && <p className="text-sm text-muted">{t('twoFactor.requiredByRole')}</p>}
            <div className="flex flex-wrap justify-end gap-2">
              <Button variant="secondary" onClick={() => setAction('renew')}>
                {t('twoFactor.newCodes')}
              </Button>
              {!status.data.required && (
                <Button variant="danger" onClick={() => setAction('disable')}>
                  {t('twoFactor.turnOff')}
                </Button>
              )}
            </div>
          </div>
        ))}
      {action && (
        <PasswordDialog
          action={action}
          onClose={() => setAction(null)}
          onCodes={(codes) => {
            setAction(null);
            setNewCodes(codes);
          }}
        />
      )}
    </Card>
  );
}

function PasswordDialog({
  action,
  onClose,
  onCodes,
}: {
  action: PasswordAction;
  onClose: () => void;
  onCodes: (codes: readonly string[]) => void;
}) {
  const { t } = useTranslation();
  const disable = useDisableTwoFactor();
  const renew = useRenewRecoveryCodes();
  const [password, setPassword] = useState('');
  const mutation = action === 'disable' ? disable : renew;

  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (action === 'disable') {
      disable.mutate({ password }, { onSuccess: onClose });
    } else {
      renew.mutate({ password }, { onSuccess: (result) => onCodes(result.recoveryCodes) });
    }
  };

  return (
    <Dialog
      open
      onOpenChange={(open) => !open && onClose()}
      title={action === 'disable' ? t('twoFactor.turnOffTitle') : t('twoFactor.newCodesTitle')}
      description={action === 'disable' ? t('twoFactor.turnOffDescription') : t('twoFactor.newCodesDescription')}
    >
      <form onSubmit={submit} className="space-y-4" noValidate>
        {mutation.isError && <Alert>{errorMessage(t, mutation.error)}</Alert>}
        <Field label={t('account.currentPassword')}>
          {(ids) => (
            <Input
              {...ids}
              type="password"
              autoComplete="current-password"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              autoFocus
            />
          )}
        </Field>
        <div className="flex justify-end gap-2">
          <Button variant="secondary" onClick={onClose}>
            {t('actions.cancel')}
          </Button>
          <Button
            type="submit"
            variant={action === 'disable' ? 'danger' : 'primary'}
            busy={mutation.isPending}
            disabled={password.length === 0}
          >
            {action === 'disable' ? t('twoFactor.turnOff') : t('twoFactor.newCodes')}
          </Button>
        </div>
      </form>
    </Dialog>
  );
}
