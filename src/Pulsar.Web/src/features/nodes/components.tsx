import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from '../../components/ui/Button';
import { Badge } from '../../components/ui/Display';
import { copyText } from '../../lib/clipboard';
import { useFormatDate } from '../../lib/format';
import type { EnrollmentInstructions, NodeSummary } from './api';
import { enrollCommand, nodeState } from './nodeStatus';

const stateTones = { online: 'success', offline: 'danger', pending: 'neutral', disabled: 'neutral' } as const;

export function NodeStateBadge({ node }: { node: Pick<NodeSummary, 'status' | 'connected'> }) {
  const { t } = useTranslation();
  const state = nodeState(node);
  return <Badge tone={stateTones[state]}>{t(`nodes.states.${state}`)}</Badge>;
}

/** The one-time enrollment command. The token is not stored in readable form: it cannot be shown again. */
export function EnrollmentCommand({ instructions }: { instructions: EnrollmentInstructions }) {
  const { t } = useTranslation();
  const formatDate = useFormatDate();
  const [copied, setCopied] = useState<boolean | null>(null);
  const command = enrollCommand(window.location.origin, instructions.token, instructions.panelKeyFingerprint);

  return (
    <div className="space-y-3">
      <p className="text-sm">{t('nodes.enroll.steps')}</p>
      <div className="relative">
        <pre
          aria-label={t('nodes.enroll.command')}
          className="overflow-x-auto rounded-md border border-border bg-bg p-3 pr-20 font-mono text-xs break-all whitespace-pre-wrap"
        >
          {command}
        </pre>
        <Button
          variant="secondary"
          size="sm"
          className="absolute top-2 right-2"
          onClick={() => void copyText(command).then(setCopied)}
        >
          {t('nodes.enroll.copy')}
        </Button>
      </div>
      {copied !== null && (
        <p role="status" className={`text-xs ${copied ? 'text-success' : 'text-danger'}`}>
          {copied ? t('nodes.enroll.copied') : t('nodes.enroll.copyFailed')}
        </p>
      )}
      <ul className="list-disc space-y-1 pl-5 text-xs text-muted">
        <li>{t('nodes.enroll.once', { expires: formatDate(instructions.expiresAt) })}</li>
        <li>{t('nodes.enroll.fingerprint', { fingerprint: instructions.panelKeyFingerprint })}</li>
      </ul>
    </div>
  );
}
