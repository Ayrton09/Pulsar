import { useId, type InputHTMLAttributes, type ReactNode, type SelectHTMLAttributes } from 'react';

const control =
  'h-10 w-full rounded-md border border-border bg-bg px-3 text-sm placeholder:text-muted ' +
  'focus-visible:outline-2 focus-visible:outline-offset-1 focus-visible:outline-accent ' +
  'aria-invalid:border-danger disabled:opacity-60';

export function Input({ className = '', ...props }: InputHTMLAttributes<HTMLInputElement>) {
  return <input {...props} className={`${control} ${className}`} />;
}

export function Select({ className = '', ...props }: SelectHTMLAttributes<HTMLSelectElement>) {
  return <select {...props} className={`${control} ${className}`} />;
}

export interface FieldProps {
  label: string;
  /** Shown under the control and announced to screen readers. */
  error?: string | undefined;
  hint?: string | undefined;
  /** Receives the ids to wire the control: id, aria-describedby and aria-invalid. */
  children: (control: { id: string; 'aria-describedby'?: string; 'aria-invalid'?: true }) => ReactNode;
}

/** Label + control + hint/error, with the accessibility attributes connected. */
export function Field({ label, error, hint, children }: FieldProps) {
  const id = useId();
  const messageId = `${id}-message`;
  const message = error ?? hint;
  return (
    <div className="space-y-1.5">
      <label htmlFor={id} className="block text-sm font-medium">
        {label}
      </label>
      {children({
        id,
        ...(message ? { 'aria-describedby': messageId } : {}),
        ...(error ? { 'aria-invalid': true as const } : {}),
      })}
      {message && (
        <p id={messageId} className={`text-xs ${error ? 'text-danger' : 'text-muted'}`}>
          {message}
        </p>
      )}
    </div>
  );
}

export function Checkbox({ label, ...props }: InputHTMLAttributes<HTMLInputElement> & { label: ReactNode }) {
  return (
    <label className="flex cursor-pointer items-start gap-2 text-sm">
      <input type="checkbox" {...props} className="mt-0.5 size-4 accent-[var(--pulsar-accent)]" />
      <span>{label}</span>
    </label>
  );
}
