import { useTranslation } from 'react-i18next';

export function App() {
  const { t } = useTranslation();

  return (
    <main className="flex min-h-screen items-center justify-center p-6">
      <section className="w-full max-w-md rounded-xl border border-border bg-surface p-8 text-center shadow-sm">
        <h1 className="text-3xl font-semibold tracking-tight">Pulsar</h1>
        <p className="mt-2 text-muted">{t('home.tagline')}</p>
        <p className="mt-6 inline-block rounded-full border border-accent/40 px-3 py-1 text-sm text-accent">
          {t('home.status')}
        </p>
      </section>
    </main>
  );
}
