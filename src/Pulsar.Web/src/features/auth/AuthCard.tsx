import type { ReactNode } from 'react';
import { Logo } from '../../components/ui/Logo';
import { InsecureConnectionBanner } from './InsecureConnectionBanner';

/** Centered card used by the login and setup pages. */
export function AuthCard({ title, subtitle, children }: { title: string; subtitle: string; children: ReactNode }) {
  return (
    <div className="flex min-h-screen flex-col">
      <InsecureConnectionBanner />
      <main className="flex flex-1 items-center justify-center p-6">
        <section className="w-full max-w-md rounded-xl border border-border bg-surface p-8 shadow-sm">
          <Logo />
          <h1 className="mt-6 text-2xl font-semibold tracking-tight">{title}</h1>
          <p className="mt-1 text-sm text-muted">{subtitle}</p>
          <div className="mt-6">{children}</div>
        </section>
      </main>
    </div>
  );
}
