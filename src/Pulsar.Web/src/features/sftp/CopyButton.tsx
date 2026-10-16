import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from '../../components/ui/Button';
import { copyText } from '../../lib/clipboard';

/** Copies a text; says when the browser refuses. */
export function CopyButton({ text, label }: { text: string; label: string }) {
  const { t } = useTranslation();
  const [copied, setCopied] = useState<boolean | null>(null);
  return (
    <>
      <Button size="sm" variant="secondary" aria-label={label} onClick={() => void copyText(text).then(setCopied)}>
        {copied ? t('twoFactor.copied') : t('twoFactor.copy')}
      </Button>
      {copied === false && (
        <span role="status" className="text-xs text-danger">
          {t('twoFactor.copyFailed')}
        </span>
      )}
    </>
  );
}
