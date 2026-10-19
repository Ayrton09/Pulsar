import type { ReactNode } from 'react';

export function PageHeader({
  title,
  description,
  status,
  actions,
}: {
  title: string;
  description?: string | undefined;
  /** Badges shown next to the title (what state the thing is in). */
  status?: ReactNode;
  actions?: ReactNode;
}) {
  return (
    <header className="mb-6 flex flex-wrap items-end justify-between gap-4">
      <div className="min-w-0">
        <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
          <h1 className="text-2xl font-semibold tracking-tight break-words">{title}</h1>
          {status}
        </div>
        {description && <p className="mt-1 text-sm text-muted">{description}</p>}
      </div>
      {actions && <div className="flex flex-wrap gap-2">{actions}</div>}
    </header>
  );
}

export function Card({ children, className = '' }: { children: ReactNode; className?: string }) {
  return (
    <section
      className={`min-w-0 rounded-2xl border border-border bg-surface/90 shadow-lg shadow-black/20 ${className}`}
    >
      {children}
    </section>
  );
}

/**
 * How full a resource is. The value is written next to it, so the bar adds no text of its own; it turns amber and then
 * red as the resource runs out.
 */
export function Meter({ percent, className = '' }: { percent: number; className?: string }) {
  const width = Math.min(100, Math.max(0, percent));
  const tone = width >= 90 ? 'bg-danger' : width >= 75 ? 'bg-warning' : 'bg-linear-to-r from-accent-strong to-accent';
  return (
    <div aria-hidden="true" className={`h-1.5 overflow-hidden rounded-full bg-surface-raised ${className}`}>
      <div className={`h-full rounded-full transition-[width] duration-500 ${tone}`} style={{ width: `${width}%` }} />
    </div>
  );
}

type Tone = 'neutral' | 'accent' | 'success' | 'danger';

const tones: Record<Tone, string> = {
  neutral: 'border-border text-muted',
  accent: 'border-accent/40 text-accent',
  success: 'border-success/40 text-success',
  danger: 'border-danger/40 text-danger',
};

/** A short status label. Always carries text, never color alone. */
export function Badge({ tone = 'neutral', children }: { tone?: Tone; children: ReactNode }) {
  return (
    <span className={`inline-flex items-center rounded-full border px-2 py-0.5 text-xs font-medium ${tones[tone]}`}>
      {children}
    </span>
  );
}

export function Alert({ children }: { children: ReactNode }) {
  return (
    <div role="alert" className="rounded-md border border-danger/40 bg-danger/10 px-3 py-2 text-sm text-danger">
      {children}
    </div>
  );
}

export function Table({ children, label }: { children: ReactNode; label: string }) {
  return (
    <div className="relative overflow-x-auto rounded-2xl border border-border bg-surface/90 shadow-lg shadow-black/20">
      <table aria-label={label} className="w-full text-left text-sm">
        {children}
      </table>
    </div>
  );
}

export function Th({ children, className = '' }: { children?: ReactNode; className?: string }) {
  return (
    <th
      className={`border-b border-border px-4 py-2.5 text-xs font-medium tracking-wide text-muted uppercase ${className}`}
    >
      {children}
    </th>
  );
}

export function Td({ children, className = '' }: { children?: ReactNode; className?: string }) {
  return <td className={`border-b border-border px-4 py-3 align-top ${className}`}>{children}</td>;
}
