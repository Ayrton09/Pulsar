import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { api, unwrap, type Schemas } from '../../api/client';

export type ServerMetricHistory = Schemas['ServerMetricHistory'];
export type NodeMetricHistory = Schemas['NodeMetricHistory'];
export type MetricRange = Schemas['MetricRange'];

export const metricRanges: readonly MetricRange[] = ['hour', 'day', 'week', 'month'];

/** Minutes are written once they are over: a chart showing them is fetched again every minute. */
const refreshEvery = 60_000;

/** Under the server's key: the live channel refetches it when the server changes (it started or stopped). */
export function useServerHistory(serverId: string, range: MetricRange) {
  return useQuery({
    queryKey: ['servers', serverId, 'metrics', range],
    queryFn: () =>
      unwrap(api.GET('/api/v1/servers/{id}/metrics', { params: { path: { id: serverId }, query: { range } } })),
    refetchInterval: range === 'hour' || range === 'day' ? refreshEvery : false,
    // The previous chart stays (dimmed) while another range loads: no flash, no layout jump.
    placeholderData: keepPreviousData,
  });
}

export function useNodeHistory(nodeId: string, range: MetricRange) {
  return useQuery({
    queryKey: ['nodes', nodeId, 'metrics', range],
    queryFn: () =>
      unwrap(api.GET('/api/v1/nodes/{id}/metrics', { params: { path: { id: nodeId }, query: { range } } })),
    refetchInterval: range === 'hour' || range === 'day' ? refreshEvery : false,
    placeholderData: keepPreviousData,
  });
}
