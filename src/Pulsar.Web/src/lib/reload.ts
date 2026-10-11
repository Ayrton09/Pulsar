/**
 * After a panel update, a tab opened before it still runs the old build: the pages it has not loaded yet are files
 * the panel no longer serves (their names carry a hash of their content). Reloading gets the new build.
 */
const reloadedAtKey = 'pulsar:reloadedForNewBuild';

/** The browsers' messages for a module (a lazy page or its styles) that could not be loaded. */
const staleBuildMessage = /dynamically imported module|Importing a module script failed|Unable to preload CSS/i;

export function isStaleBuildError(error: unknown): boolean {
  return error instanceof Error && staleBuildMessage.test(error.message);
}

export function reloadPage(): void {
  window.location.reload();
}

/**
 * Whether the panel answers now. A module that cannot be loaded is also what a browser offline, or a panel restarting
 * (or a proxy in front of it answering for it), gets; reloading then would show the browser's own error page.
 */
export async function panelAnswers(): Promise<boolean> {
  if (!navigator.onLine) {
    return false;
  }
  try {
    return (await fetch('/health/live', { cache: 'no-store' })).ok;
  } catch {
    return false;
  }
}

/** What reloadForNewBuild did: reload, or not because it already did in the last minute, or the panel is not there. */
export type NewBuildReload = 'reloading' | 'reloaded' | 'unreachable';

/**
 * Reloads the page for a newer build, when the panel answers and unless it did so in the last minute: if the new
 * build fails too, the user gets the error page instead of a reload loop. Without session storage it cannot tell, so
 * it does not reload.
 */
export async function reloadForNewBuild(
  reload: () => void = reloadPage,
  answers: () => Promise<boolean> = panelAnswers,
): Promise<NewBuildReload> {
  if (!(await answers())) {
    return 'unreachable';
  }
  try {
    const last = Number(sessionStorage.getItem(reloadedAtKey) ?? 0);
    if (Date.now() - last < 60_000) {
      return 'reloaded';
    }
    sessionStorage.setItem(reloadedAtKey, String(Date.now()));
  } catch {
    return 'reloaded';
  }
  reload();
  return 'reloading';
}
