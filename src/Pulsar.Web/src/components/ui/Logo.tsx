import { Activity } from 'lucide-react';

/** The panel's mark and name, as the sidebar, the phone's top bar and the sign-in pages show them. */
export function Logo() {
  return (
    <span className="flex items-center gap-2.5">
      <span className="grid size-8 place-items-center rounded-lg bg-linear-to-br from-accent to-accent-strong text-accent-contrast shadow-lg shadow-accent/30">
        <Activity className="size-4.5" />
      </span>
      <span className="text-lg font-semibold tracking-tight">Pulsar</span>
    </span>
  );
}
