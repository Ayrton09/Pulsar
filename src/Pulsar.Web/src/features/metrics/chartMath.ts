export interface ChartPoint {
  at: string;
  average: number;
  peak: number;
}

export interface ChartRun {
  first: ChartPoint;
  last: ChartPoint;
  points: ChartPoint[];
}

/** Consecutive points, split where one or more are missing (nothing was recorded then). */
export function splitRuns(points: ChartPoint[], stepSeconds: number): ChartRun[] {
  const runs: ChartRun[] = [];
  let current: ChartRun | undefined;
  for (const point of points) {
    if (!current || Date.parse(point.at) - Date.parse(current.last.at) > stepSeconds * 1000 * 1.5) {
      current = { first: point, last: point, points: [] };
      runs.push(current);
    }
    current.points.push(point);
    current.last = point;
  }
  return runs;
}

export function linePath(run: ChartPoint[], x: (iso: string) => number, y: (value: number) => number): string {
  return run.map((point, index) => `${index === 0 ? 'M' : 'L'}${x(point.at)},${y(point.average)}`).join(' ');
}

/** The smallest of 1, 2, 2.5 and 5 times a power of ten that is at least the value. */
export function niceMax(value: number): number {
  if (!(value > 0)) {
    return 1;
  }
  const magnitude = 10 ** Math.floor(Math.log10(value));
  return ([1, 2, 2.5, 5, 10].find((step) => step * magnitude >= value) ?? 10) * magnitude;
}
