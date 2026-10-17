import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from '../../components/ui/Button';
import { Badge } from '../../components/ui/Display';
import { copyText } from '../../lib/clipboard';
import { useFormatDate } from '../../lib/format';
import type { EnrollmentInstructions, NodeSummary } from './api';
import { agentSetupPath, enrollCommand, nodeState } from './nodeStatus';

const stateTones = {
  online: 'success',
  offline: 'danger',
  pending: 'neutral',
  awaiting: 'accent',
  disabled: 'neutral',
} as const;

export function NodeStateBadge({ node }: { node: Pick<NodeSummary, 'status' | 'connected'> }) {
  const { t } = useTranslation();
  const state = nodeState(node);
  return <Badge tone={stateTones[state]}>{t(`nodes.states.${state}`)}</Badge>;
}

/**
 * The one-time enrollment: the node code for PulsarAgentSetup.exe first, the installer script's command as the advanced
 * path (the only one when the panel gave no code). The token is not stored in readable form: neither can be shown again.
 */
export function EnrollmentCommand({ instructions }: { instructions: EnrollmentInstructions }) {
  const { t } = useTranslation();
  const formatDate = useFormatDate();
  const [showCommand, setShowCommand] = useState(false);
  const origin = window.location.origin;
  const command = enrollCommand(origin, instructions.token, instructions.panelKeyFingerprint);
  const code = instructions.nodeCode;
  const expires = formatDate(instructions.expiresAt);

  const script = (
    <div className="space-y-3">
      <p className="text-sm">{t('nodes.enroll.steps')}</p>
      <CopyBlock label={t('nodes.enroll.command')} text={command} />
      <p className="text-xs text-muted">{t('nodes.enroll.trust')}</p>
    </div>
  );

  return (
    <div className="space-y-4">
      {code ? (
        <>
          <section aria-labelledby="enroll-setup" className="space-y-3">
            <h3 id="enroll-setup" className="text-sm font-semibold">
              {t('nodes.enroll.setupTitle')}
            </h3>
            <p className="text-sm">{t('nodes.enroll.setupSteps')}</p>
            {instructions.agentSetupAvailable ? (
              <a
                href={agentSetupPath}
                download
                className="inline-flex h-8 items-center rounded-md border border-border bg-surface px-3 text-sm font-medium hover:bg-surface-raised focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-accent"
              >
                {t('nodes.enroll.download')}
              </a>
            ) : (
              <p className="text-xs text-muted">{t('nodes.enroll.noSetup')}</p>
            )}
            <CopyBlock label={t('nodes.enroll.address')} text={origin} />
            <CopyBlock label={t('nodes.enroll.code')} text={code} />
            <p className="text-xs text-muted">{t('nodes.enroll.setupTrust')}</p>
          </section>
          <div className="space-y-3">
            <Button variant="ghost" size="sm" aria-expanded={showCommand} onClick={() => setShowCommand(!showCommand)}>
              {t('nodes.enroll.advanced')}
            </Button>
            {showCommand && script}
          </div>
        </>
      ) : (
        script
      )}
      <ul className="list-disc space-y-1 pl-5 text-xs text-muted">
        <li>{code ? t('nodes.enroll.codeOnce', { expires }) : t('nodes.enroll.once', { expires })}</li>
        <li>{t('nodes.enroll.fingerprint', { fingerprint: instructions.panelKeyFingerprint })}</li>
      </ul>
    </div>
  );
}

/** A one-time value with its label and a copy button; says when the browser refuses to copy. */
export function CopyBlock({ label, text }: { label: string; text: string }) {
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
          aria-label={t('nodes.enroll.copyNamed', { name: label })}
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
