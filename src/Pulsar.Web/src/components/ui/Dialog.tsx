import * as RadixDialog from '@radix-ui/react-dialog';
import { X } from 'lucide-react';
import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';

export interface DialogProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: string;
  description?: string;
  children: ReactNode;
  /** Wider dialogs for forms with long lists (e.g. permissions). */
  size?: 'md' | 'lg';
}

/** Modal dialog on Radix primitives: focus trap, Escape to close, labelled for screen readers. */
export function Dialog({ open, onOpenChange, title, description, children, size = 'md' }: DialogProps) {
  const { t } = useTranslation();
  return (
    <RadixDialog.Root open={open} onOpenChange={onOpenChange}>
      <RadixDialog.Portal>
        <RadixDialog.Overlay className="fixed inset-0 z-40 bg-black/60" />
        <RadixDialog.Content
          className={[
            'fixed top-1/2 left-1/2 z-50 max-h-[90vh] w-[calc(100%-2rem)] -translate-x-1/2 -translate-y-1/2 overflow-y-auto',
            'rounded-xl border border-border bg-surface p-6 shadow-xl',
            size === 'lg' ? 'max-w-2xl' : 'max-w-md',
          ].join(' ')}
          {...(description ? {} : { 'aria-describedby': undefined })}
        >
          <div className="mb-4 flex items-start justify-between gap-4">
            <div>
              <RadixDialog.Title className="text-lg font-semibold">{title}</RadixDialog.Title>
              {description && (
                <RadixDialog.Description className="mt-1 text-sm text-muted">{description}</RadixDialog.Description>
              )}
            </div>
            <RadixDialog.Close
              className="-mt-1 -mr-2 grid size-8 shrink-0 place-items-center rounded-md text-muted hover:bg-surface-raised hover:text-text"
              aria-label={t('actions.close')}
            >
              <X className="size-4" />
            </RadixDialog.Close>
          </div>
          {children}
        </RadixDialog.Content>
      </RadixDialog.Portal>
    </RadixDialog.Root>
  );
}
