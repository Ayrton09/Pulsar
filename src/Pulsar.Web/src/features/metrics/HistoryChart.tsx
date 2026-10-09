import { useId, useState, type KeyboardEvent, type PointerEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { linePath, niceMax, splitRuns, type ChartPoint } from './chartMath';

interface HistoryChartProps {
  title: string;
  points: ChartPoint[];
  /** The window shown (ISO), and how much time each point covers. */
  from: string;
  to: string;
  stepSeconds: number;
  formatValue: (value: number) => string;
  formatTime: (iso: string) => string;
  /** Labels on the time axis. */
  formatTick: (iso: string) => string;
  /** The axis never ends below this (e.g. 1 % of CPU), so an idle server is not drawn as a busy one. */
  minimumMax: number;
  /** Rounds the axis' top in this unit (bytes: a megabyte or a gigabyte), so its labels are round numbers. */
  unit?: number;
  /** A fixed top instead (a node's whole memory). */
  max?: number;
}

const width = 600;
const height = 140;

/**
 * One measure over time: the average as a line over a light area, gaps where nothing was recorded (the server stopped,
 * the node was away). A crosshair follows the pointer or the arrow keys and reads out the average and the peak; the
 * same points are in a table for screen readers.
 */
export function HistoryChart({
  title,
  points,
  from,
  to,
  stepSeconds,
  formatValue,
  formatTime,
  formatTick,
  minimumMax,
  unit = 1,
  max,
}: HistoryChartProps) {
  const { t } = useTranslation();
  const readoutId = useId();
  const [active, setActive] = useState<number | null>(null);
  const start = Date.parse(from);
  const span = Math.max(1, Date.parse(to) - start);
  const top =
    max != null && max > 0 ? max : niceMax(Math.max(minimumMax, ...points.map((p) => p.average)) / unit) * unit;
  const x = (iso: string) => ((Date.parse(iso) - start) / span) * width;
  const y = (value: number) => height - (Math.min(value, top) / top) * height;
  const runs = splitRuns(points, stepSeconds);
  const current = active == null ? undefined : points[Math.min(active, points.length - 1)];

  const nearest = (event: PointerEvent<SVGSVGElement>) => {
    const box = event.currentTarget.getBoundingClientRect();
    const at = start + ((event.clientX - box.left) / Math.max(1, box.width)) * span;
    let best: number | null = null;
    let distance = Infinity;
    points.forEach((point, index) => {
      const away = Math.abs(Date.parse(point.at) - at);
      if (away < distance) {
        best = index;
        distance = away;
      }
    });
    setActive(best);
  };

  const onKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    if (points.length === 0) {
      return;
    }
    const moves: Partial<Record<string, number>> = {
      ArrowLeft: (active ?? points.length) - 1,
      ArrowRight: (active ?? -1) + 1,
      Home: 0,
      End: points.length - 1,
    };
    const move = moves[event.key];
    if (move != null) {
      event.preventDefault();
      setActive(Math.min(points.length - 1, Math.max(0, move)));
    } else if (event.key === 'Escape') {
      setActive(null);
    }
  };

  return (
    <figure className="min-w-0">
      <div className="mb-1 flex items-baseline justify-between gap-4 text-sm">
        <figcaption className="font-medium">{title}</figcaption>
        <p id={readoutId} aria-live="polite" className="text-xs text-muted tabular-nums">
          {current
            ? t('history.readout', {
                time: formatTime(current.at),
                average: formatValue(current.average),
                peak: formatValue(current.peak),
              })
            : null}
        </p>
      </div>
      <div className="flex gap-2">
        <div className="flex w-16 shrink-0 flex-col justify-between text-right text-xs text-muted tabular-nums">
          <span>{formatValue(top)}</span>
          <span>{formatValue(top / 2)}</span>
          <span>{formatValue(0)}</span>
        </div>
        <div
          tabIndex={points.length > 0 ? 0 : -1}
          role="group"
          aria-label={t('history.chartLabel', { chart: title })}
          aria-describedby={readoutId}
          onKeyDown={onKeyDown}
          onBlur={() => setActive(null)}
          className="min-w-0 flex-1 rounded focus-visible:outline-2 focus-visible:outline-accent"
        >
          <svg
            viewBox={`0 0 ${width} ${height}`}
            preserveAspectRatio="none"
            className="block h-36 w-full touch-pan-y"
            aria-hidden="true"
            onPointerMove={nearest}
            onPointerLeave={() => setActive(null)}
          >
            {[0, 0.5, 1].map((share) => (
              <line
                key={share}
                x1={0}
                x2={width}
                y1={height * share}
                y2={height * share}
                className="stroke-border"
                strokeWidth={1}
                vectorEffect="non-scaling-stroke"
              />
            ))}
            {runs.map(({ first, last, points: run }) =>
              run.length === 1 ? (
                <line
                  key={first.at}
                  x1={x(first.at)}
                  x2={x(first.at)}
                  y1={height}
                  y2={y(first.average)}
                  className="stroke-accent"
                  strokeWidth={2}
                  strokeLinecap="round"
                  vectorEffect="non-scaling-stroke"
                />
              ) : (
                <g key={first.at}>
                  <path
                    d={`${linePath(run, x, y)} L${x(last.at)},${height} L${x(first.at)},${height} Z`}
                    className="fill-accent/15"
                  />
                  <path
                    d={linePath(run, x, y)}
                    className="fill-none stroke-accent"
                    strokeWidth={2}
                    strokeLinejoin="round"
                    strokeLinecap="round"
                    vectorEffect="non-scaling-stroke"
                  />
                </g>
              ),
            )}
            {current && (
              <line
                x1={x(current.at)}
                x2={x(current.at)}
                y1={0}
                y2={height}
                className="stroke-muted"
                strokeWidth={1}
                vectorEffect="non-scaling-stroke"
              />
            )}
          </svg>
          <div className="mt-1 flex justify-between text-xs text-muted tabular-nums">
            <span>{formatTick(from)}</span>
            <span>{formatTick(new Date(start + span / 2).toISOString())}</span>
            <span>{formatTick(to)}</span>
          </div>
        </div>
      </div>
      <table className="sr-only">
        <caption>{t('history.tableLabel', { chart: title })}</caption>
        <thead>
          <tr>
            <th scope="col">{t('history.time')}</th>
            <th scope="col">{t('history.average')}</th>
            <th scope="col">{t('history.peak')}</th>
          </tr>
        </thead>
        <tbody>
          {points.map((point) => (
            <tr key={point.at}>
              <td>{formatTime(point.at)}</td>
              <td>{formatValue(point.average)}</td>
              <td>{formatValue(point.peak)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </figure>
  );
}
