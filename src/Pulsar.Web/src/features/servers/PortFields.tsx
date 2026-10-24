import { useTranslation } from 'react-i18next';
import { Field, Input } from '../../components/ui/Form';
import { checkPorts, type NodeAllocation, type PortCheck, type TemplatePort } from './portChoices';

/** What a port's check says: where it lands (a note) or why it cannot (an error). */
function usePortCheckText() {
  const { t } = useTranslation();
  return (check: PortCheck, port: number | null): { error?: string; note?: string } => {
    switch (check.kind) {
      case 'existing':
        return { note: t('servers.ports.existing', { address: `${check.address}:${port}` }) };
      case 'new':
        return { note: t('servers.ports.added', { address: `${check.address}:${port}` }) };
      case 'off':
        return { note: t('servers.ports.off') };
      case 'taken':
        return {
          error: check.serverName
            ? t('servers.ports.taken', { server: check.serverName, port })
            : t('servers.ports.takenByOther', { port }),
        };
      case 'otherAddress':
        return { error: t('servers.ports.otherAddress', { port }) };
      case 'notAllocated':
        return { error: t('servers.ports.notAllocated', { port }) };
      case 'invalid':
        return { error: t('servers.ports.invalid') };
      case 'duplicate':
        return { error: t('servers.ports.duplicate', { port }) };
      case 'required':
        return { error: t('validation.required') };
    }
  };
}

interface PortNumberFieldsProps {
  ports: TemplatePort[];
  /** Typed numbers by port name; empty leaves an optional port off. */
  values: Record<string, string>;
  onChange: (port: string, value: string) => void;
  allocations: NodeAllocation[];
  /** The server whose ports these are, when it exists: its own allocations are free for it. */
  serverId?: string;
  disabled?: boolean;
}

/**
 * A game's ports typed as numbers, for whoever manages the node: each says at once whether it uses one of the node's
 * allocations, is added to them, or cannot be had (another server's). The ports that follow one show theirs.
 */
export function PortNumberFields({ ports, values, onChange, allocations, serverId, disabled }: PortNumberFieldsProps) {
  const { t } = useTranslation();
  const text = usePortCheckText();
  return (
    <div className="space-y-4">
      {checkPorts(ports, values, allocations, true, serverId).map((choice) => {
        const { port } = choice;
        const status = text(choice.check, choice.number);
        const usual = t('servers.wizard.portDefault', {
          port: port.default,
          protocols: port.protocols.join('/').toUpperCase(),
        });
        return (
          <div key={port.name} className="space-y-1.5">
            <Field
              label={port.required ? `${port.label} *` : port.label}
              error={status.error}
              hint={[status.note, port.description, usual].filter(Boolean).join(' ')}
            >
              {(ids) => (
                <Input
                  {...ids}
                  inputMode="numeric"
                  autoComplete="off"
                  className="max-w-40"
                  value={values[port.name] ?? ''}
                  placeholder={String(port.default)}
                  disabled={disabled}
                  onChange={(e) => onChange(port.name, e.target.value)}
                />
              )}
            </Field>
            {choice.followers.map((follower) => {
              const followerStatus = text(follower.check, follower.number);
              return (
                <p key={follower.port.name} className="text-xs text-muted">
                  {choice.number === null ? (
                    <>
                      <span className="font-medium text-text">{follower.port.label}</span>{' '}
                      {t('servers.wizard.followsPort', { port: port.label, offset: follower.port.offset ?? 1 })}
                    </>
                  ) : (
                    <>
                      <span className="font-medium text-text">
                        {t('servers.ports.follower', { label: follower.port.label, port: follower.number })}
                      </span>{' '}
                      <span className={followerStatus.error ? 'text-danger' : undefined}>
                        {followerStatus.error ?? followerStatus.note}
                      </span>
                    </>
                  )}
                </p>
              );
            })}
          </div>
        );
      })}
    </div>
  );
}
