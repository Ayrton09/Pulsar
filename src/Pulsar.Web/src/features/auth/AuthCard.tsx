import type { ReactNode } from 'react';
import { InsecureConnectionBanner } from './InsecureConnectionBanner';

/** Centered card used by the login and setup pages. */
export function AuthCard({ title, subtitle, children }: { title: string; subtitle: string; children: ReactNode }) {
  return (
    <div className="flex min-h-screen flex-col">
      <InsecureConnectionBanner />
      <main className="flex flex-1 items-center justify-center p-6">
        <section className="w-full max-w-md rounded-xl border border-border bg-surface p-8 shadow-sm">
          <p className="text-sm font-semibold tracking-wide text-accent">Pulsar</p>
          <h1 className="mt-2 text-2xl font-semibold tracking-tight">{title}</h1>
          <p className="mt-1 text-sm text-muted">{subtitle}</p>
          <div className="mt-6">{children}</div>
        </section>
      </main>
    </div>
  );
}
