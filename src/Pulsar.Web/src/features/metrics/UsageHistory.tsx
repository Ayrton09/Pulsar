import { useMemo, useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Alert, Card } from '../../components/ui/Display';
import { errorMessage } from '../../lib/errors';
import { useFormatBytes, useFormatGigabytes } from '../../lib/format';
import { metricRanges, useNodeHistory, useServerHistory, type MetricRange } from './api';
import { HistoryChart } from './HistoryChart';

/** A server's CPU, memory and players over the last hour, day, week or month. */
export function ServerUsageHistory({ serverId }: { serverId: string }) {
  const { t } = useTranslation();
  const [range, setRange] = useState<MetricRange>('hour');
  const history = useServerHistory(serverId, range);
  const formatBytes = useFormatBytes();
  const formats = useTimeFormats(range);
  const percent = usePercent();
  const data = history.data;
  const hasPlayers = data?.points.some((p) => p.playersAverage != null) ?? false;
  return (
    <HistoryCard range={range} onRange={setRange} error={history.error} fetching={history.isPlaceholderData}>
      {data &&
        (data.points.length === 0 ? (
          <p className="text-sm text-muted">{t('history.empty')}</p>
        ) : (
          <div className="grid gap-6 lg:grid-cols-2">
            <HistoryChart
              title={t('servers.usage.cpu')}
              points={data.points.map((p) => ({ at: p.at, average: p.cpuAverage, peak: p.cpuPeak }))}
              {...frame(data)}
              {...formats}
              formatValue={percent}
              minimumMax={1}
            />
            <HistoryChart
              title={t('servers.usage.memory')}
              points={data.points.map((p) => ({ at: p.at, average: p.memoryAverage, peak: p.memoryPeak }))}
              {...frame(data)}
              {...formats}
              formatValue={formatBytes}
              minimumMax={64 * 1024 ** 2}
              unit={byteUnit(Math.max(...data.points.map((p) => p.memoryAverage)))}
            />
            {hasPlayers && (
              <HistoryChart
                title={t('servers.usage.players')}
                points={data.points
                  .filter((p) => p.playersAverage != null)
                  .map((p) => ({ at: p.at, average: p.playersAverage ?? 0, peak: p.playersPeak ?? 0 }))}
                {...frame(data)}
                {...formats}
                formatValue={(value) => t('history.playersValue', { value })}
                minimumMax={1}
              />
            )}
          </div>
        ))}
    </HistoryCard>
  );
}

/** A node's CPU and memory over the last hour, day, week or month. */
export function NodeUsageHistory({ nodeId }: { nodeId: string }) {
  const { t } = useTranslation();
  const [range, setRange] = useState<MetricRange>('hour');
  const history = useNodeHistory(nodeId, range);
  const formatGigabytes = useFormatGigabytes();
  const formats = useTimeFormats(range);
  const percent = usePercent();
  const data = history.data;
  return (
    <HistoryCard range={range} onRange={setRange} error={history.error} fetching={history.isPlaceholderData}>
      {data &&
        (data.points.length === 0 ? (
          <p className="text-sm text-muted">{t('history.emptyNode')}</p>
        ) : (
          <div className="grid gap-6 lg:grid-cols-2">
            <HistoryChart
              title={t('nodes.cpu')}
              points={data.points.map((p) => ({ at: p.at, average: p.cpuAverage, peak: p.cpuPeak }))}
              {...frame(data)}
              {...formats}
              formatValue={percent}
              minimumMax={1}
            />
            <HistoryChart
              title={t('nodes.memory')}
              points={data.points.map((p) => ({ at: p.at, average: p.memoryUsedAverage, peak: p.memoryUsedPeak }))}
              {...frame(data)}
              {...formats}
              formatValue={formatGigabytes}
              // The machine's whole memory is the top.
              max={Math.max(...data.points.map((p) => p.memoryTotal))}
              minimumMax={1024 ** 3}
            />
          </div>
        ))}
    </HistoryCard>
  );
}

function HistoryCard({
  range,
  onRange,
  error,
  fetching,
  children,
}: {
  range: MetricRange;
  onRange: (range: MetricRange) => void;
  error: Error | null;
  fetching: boolean;
  children: ReactNode;
}) {
  const { t } = useTranslation();
  return (
    <Card className="mt-6 p-6">
      <div className="mb-4 flex flex-wrap items-center justify-between gap-3">
        <h2 className="text-lg font-semibold">{t('history.title')}</h2>
        <div role="group" aria-label={t('history.range')} className="flex rounded-md border border-border p-0.5">
          {metricRanges.map((name) => (
            <button
              key={name}
              type="button"
              aria-pressed={range === name}
              onClick={() => onRange(name)}
              className={`rounded px-3 py-1 text-xs ${range === name ? 'bg-accent text-accent-contrast' : 'text-muted hover:text-text'}`}
            >
              {t(`history.ranges.${name}`)}
            </button>
          ))}
        </div>
      </div>
      {error && <Alert>{errorMessage(t, error)}</Alert>}
      <div className={fetching ? 'opacity-60 transition-opacity' : 'transition-opacity'}>{children}</div>
    </Card>
  );
}

/** Times on the axis as the span suggests (hours for a day, dates for a month); the readout always has both. */
function useTimeFormats(range: MetricRange) {
  const { i18n } = useTranslation();
  return useMemo(() => {
    const time = new Intl.DateTimeFormat(i18n.language, { hour: '2-digit', minute: '2-digit' });
    const date = new Intl.DateTimeFormat(i18n.language, { day: 'numeric', month: 'short' });
    const full = new Intl.DateTimeFormat(i18n.language, { dateStyle: 'medium', timeStyle: 'short' });
    const tick = range === 'hour' || range === 'day' ? time : date;
    return {
      formatTick: (iso: string) => tick.format(new Date(iso)),
      formatTime: (iso: string) => full.format(new Date(iso)),
    };
  }, [i18n.language, range]);
}

function usePercent() {
  const { t } = useTranslation();
  return (value: number) => t('servers.usage.cpuValue', { value });
}

/** The window a chart shows, from the history the API returned. */
function frame(history: { from: string; to: string; stepSeconds: number }) {
  return { from: history.from, to: history.to, stepSeconds: history.stepSeconds };
}

/** Megabytes below a gigabyte, gigabytes from there: the axis' top is then a round number in the unit it is shown in. */
function byteUnit(bytes: number): number {
  return bytes < 1024 ** 3 ? 1024 ** 2 : 1024 ** 3;
}
