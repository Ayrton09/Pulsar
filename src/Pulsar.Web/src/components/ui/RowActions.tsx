import * as RadixDialog from '@radix-ui/react-dialog';
import { MoreHorizontal } from 'lucide-react';
import { Children, useState, useSyncExternalStore, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';

// Tailwind's md breakpoint, as the sidebar's: below it a row's actions do not fit beside its name.
const wideScreen = '(min-width: 48rem)';

function subscribe(listener: () => void) {
  if (typeof window.matchMedia !== 'function') {
    return () => undefined;
  }
  const query = window.matchMedia(wideScreen);
  query.addEventListener('change', listener);
  return () => query.removeEventListener('change', listener);
}

/** Whether the screen is wide enough for the sidebar and a table's row actions; true where it cannot be told. */
function useWideScreen(): boolean {
  return useSyncExternalStore(subscribe, () =>
    typeof window.matchMedia === 'function' ? window.matchMedia(wideScreen).matches : true,
  );
}

/**
 * A table row's actions: in a row on wide screens; on a phone, behind one button that opens them as a sheet from the
 * bottom, one under the other and large enough for a finger. Choosing one closes the sheet (a dialog it opens is the
 * page's).
 */
export function RowActions({ name, children }: { name: string; children: ReactNode }) {
  const { t } = useTranslation();
  const wide = useWideScreen();
  const [open, setOpen] = useState(false);
  // A row without actions for this user (no permission, nothing that applies) gets no button opening an empty sheet.
  if (Children.toArray(children).length === 0) {
    return null;
  }
  if (wide) {
    return <span className="inline-flex flex-wrap justify-end gap-1">{children}</span>;
  }
  return (
    <RadixDialog.Root open={open} onOpenChange={setOpen}>
      <RadixDialog.Trigger
        aria-label={t('common.actionsOf', { name })}
        className="ml-auto grid size-9 place-items-center rounded-md text-muted hover:bg-surface-raised hover:text-text focus-visible:outline-2 focus-visible:outline-accent"
      >
        <MoreHorizontal className="size-5" />
      </RadixDialog.Trigger>
      <RadixDialog.Portal>
        <RadixDialog.Overlay className="fixed inset-0 z-40 bg-black/60 motion-safe:animate-[pulsar-fade-in_150ms_ease-out]" />
        <RadixDialog.Content
          aria-describedby={undefined}
          className="fixed inset-x-0 bottom-0 z-50 max-h-[80vh] overflow-y-auto rounded-t-2xl border-t border-border bg-surface p-4 pb-[max(1rem,env(safe-area-inset-bottom))] shadow-xl motion-safe:animate-[pulsar-sheet-in_200ms_ease-out]"
        >
          <RadixDialog.Title className="mb-3 truncate px-2 font-mono text-sm text-muted">{name}</RadixDialog.Title>
          {/* Each action as a full-width line; a click on any of them closes the sheet after it ran. */}
          <div
            className="flex flex-col gap-1 [&>*]:h-11 [&>*]:w-full [&>*]:justify-start [&>*]:text-base"
            onClick={(event) => {
              if ((event.target as HTMLElement).closest('button:not(:disabled), a[href]')) {
                setOpen(false);
              }
            }}
          >
            {children}
          </div>
        </RadixDialog.Content>
      </RadixDialog.Portal>
    </RadixDialog.Root>
  );
}
