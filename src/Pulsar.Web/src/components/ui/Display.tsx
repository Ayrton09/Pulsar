import type { ReactNode } from 'react';

export function PageHeader({
  title,
  description,
  actions,
}: {
  title: string;
  description?: string | undefined;
  actions?: ReactNode;
}) {
  return (
    <header className="mb-6 flex flex-wrap items-end justify-between gap-4">
      <div>
        <h1 className="text-2xl font-semibold tracking-tight">{title}</h1>
        {description && <p className="mt-1 text-sm text-muted">{description}</p>}
      </div>
      {actions && <div className="flex gap-2">{actions}</div>}
    </header>
  );
}

export function Card({ children, className = '' }: { children: ReactNode; className?: string }) {
  return <section className={`rounded-xl border border-border bg-surface ${className}`}>{children}</section>;
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
    <div className="relative overflow-x-auto rounded-xl border border-border bg-surface">
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
