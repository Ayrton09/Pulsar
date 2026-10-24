/**
 * Templates' versions, ordered as the panel orders them (GameTemplate.CompareVersions): major, minor and patch as numbers,
 * a prerelease before its release, and prereleases by SemVer's rules.
 */
export function compareTemplateVersions(left: string, right: string): number {
  const [leftNumbers, leftPre] = split(left);
  const [rightNumbers, rightPre] = split(right);
  for (let i = 0; i < 3; i++) {
    const compared = (leftNumbers[i] ?? 0) - (rightNumbers[i] ?? 0);
    if (compared !== 0) {
      return Math.sign(compared);
    }
  }
  if (leftPre === null || rightPre === null) {
    return leftPre === rightPre ? 0 : leftPre === null ? 1 : -1;
  }
  return comparePreReleases(leftPre, rightPre);
}

/** The newest version of each game (templates of one key): older ones stay for the servers made from them. */
export function newestOfEachGame<T extends { key: string; version: string }>(templates: readonly T[]): T[] {
  const newest = new Map<string, T>();
  for (const template of templates) {
    const kept = newest.get(template.key);
    if (!kept || compareTemplateVersions(template.version, kept.version) > 0) {
      newest.set(template.key, template);
    }
  }
  return templates.filter((template) => newest.get(template.key) === template);
}

// Build metadata (+...) does not order.
function split(version: string): [number[], string | null] {
  const withoutBuild = version.split('+')[0] ?? '';
  const dash = withoutBuild.indexOf('-');
  const core = dash === -1 ? withoutBuild : withoutBuild.slice(0, dash);
  return [
    core.split('.').map((part) => Number.parseInt(part, 10) || 0),
    dash === -1 ? null : withoutBuild.slice(dash + 1),
  ];
}

// SemVer: identifier by identifier, numeric ones as numbers and before alphanumeric ones, then the longer list.
function comparePreReleases(left: string, right: string): number {
  const leftParts = left.split('.');
  const rightParts = right.split('.');
  for (let i = 0; i < leftParts.length && i < rightParts.length; i++) {
    const a = leftParts[i] ?? '';
    const b = rightParts[i] ?? '';
    const aNumeric = /^\d+$/.test(a);
    const bNumeric = /^\d+$/.test(b);
    const compared =
      aNumeric && bNumeric
        ? Math.sign(Number(a) - Number(b))
        : aNumeric
          ? -1
          : bNumeric
            ? 1
            : a < b
              ? -1
              : a > b
                ? 1
                : 0;
    if (compared !== 0) {
      return compared;
    }
  }
  return Math.sign(leftParts.length - rightParts.length);
}
