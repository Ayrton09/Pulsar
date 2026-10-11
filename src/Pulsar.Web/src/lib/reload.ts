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
 * Reloads the page for a newer build, unless it did so in the last minute: if the new build fails too, the user gets
 * the error page instead of a reload loop. Without session storage it cannot tell, so it does not reload. Returns
 * whether it reloads.
 */
export function reloadForNewBuild(reload: () => void = reloadPage): boolean {
  try {
    const last = Number(sessionStorage.getItem(reloadedAtKey) ?? 0);
    if (Date.now() - last < 60_000) {
      return false;
    }
    sessionStorage.setItem(reloadedAtKey, String(Date.now()));
  } catch {
    return false;
  }
  reload();
  return true;
}
