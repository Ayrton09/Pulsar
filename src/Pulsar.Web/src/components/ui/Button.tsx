import type { LucideIcon } from 'lucide-react';
import type { ButtonHTMLAttributes } from 'react';

type Variant = 'primary' | 'secondary' | 'success' | 'danger' | 'dangerSubtle' | 'ghost';

const variants: Record<Variant, string> = {
  primary: 'bg-accent text-accent-contrast shadow-sm shadow-accent/20 hover:bg-accent/90',
  secondary: 'border border-border bg-surface hover:bg-surface-raised',
  success: 'bg-success text-accent-contrast shadow-sm shadow-success/20 hover:bg-success/90',
  danger: 'bg-danger text-white hover:bg-danger/90',
  dangerSubtle: 'border border-danger/40 text-danger hover:bg-danger/10',
  ghost: 'hover:bg-surface-raised',
};

// A button that cannot be used loses its variant's fill: a faded accent still reads as "click me".
const unavailable: Record<Variant, string> = {
  primary: 'bg-surface-raised text-muted',
  secondary: 'border border-border bg-surface text-muted',
  success: 'bg-surface-raised text-muted',
  danger: 'bg-surface-raised text-muted',
  dangerSubtle: 'border border-border text-muted',
  ghost: 'text-muted',
};

const sizes = {
  sm: 'h-8 px-3 text-sm',
  md: 'h-10 px-4 text-sm',
  icon: 'size-8 text-sm',
} as const;

export interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: Variant;
  /** icon: a square button with only its icon; give it an aria-label. */
  size?: 'sm' | 'md' | 'icon';
  /** Disables the button and shows that the action is running. */
  busy?: boolean;
  /** Shown before the label; the label stays the button's name. */
  icon?: LucideIcon;
}

export function Button({
  variant = 'primary',
  size = 'md',
  busy = false,
  icon: Icon,
  className = '',
  disabled,
  children,
  ...props
}: ButtonProps) {
  // While its action runs it keeps its look, with a spinner: it is busy, not unavailable.
  const inactive = disabled === true && !busy;
  return (
    <button
      type="button"
      {...props}
      disabled={disabled === true || busy}
      aria-busy={busy || undefined}
      className={[
        'inline-flex items-center justify-center gap-2 rounded-lg font-medium transition-colors',
        'focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-accent',
        busy ? 'cursor-wait' : 'disabled:cursor-not-allowed',
        sizes[size],
        inactive ? unavailable[variant] : variants[variant],
        className,
      ].join(' ')}
    >
      {busy ? <Spinner /> : Icon && <Icon className="size-4 shrink-0" />}
      {children}
    </button>
  );
}

export function Spinner({ label }: { label?: string }) {
  return (
    <span
      role={label ? 'status' : undefined}
      aria-label={label}
      className="inline-block size-4 animate-spin rounded-full border-2 border-current border-t-transparent"
    />
  );
}
