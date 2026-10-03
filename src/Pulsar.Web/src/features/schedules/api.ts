import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api, unwrap, type Schemas } from '../../api/client';
import { adminKeys } from '../admin/api';
import { serverKeys } from '../servers/api';

export type ScheduledTaskView = Schemas['ScheduledTaskView'];
export type ScheduledRunView = Schemas['ScheduledRunView'];
export type ScheduledTaskRequest = Schemas['ScheduledTaskRequest'];
export type ScheduledAction = Schemas['ScheduledAction'];

/** Under the server's keys: the live channel refreshes them whenever a task or one of its runs changes. */
export const scheduleKeys = {
  list: (serverId: string) => ['servers', serverId, 'schedules'] as const,
  runs: (serverId: string, taskId: string) => ['servers', serverId, 'schedules', taskId, 'runs'] as const,
  preview: (serverId: string, cron: string, timeZone: string) => ['schedulePreview', serverId, cron, timeZone] as const,
};

export function useSchedules(serverId: string) {
  return useQuery({
    queryKey: scheduleKeys.list(serverId),
    queryFn: () => unwrap(api.GET('/api/v1/servers/{id}/schedules', { params: { path: { id: serverId } } })),
  });
}

export function useScheduleRuns(serverId: string, taskId: string) {
  return useQuery({
    queryKey: scheduleKeys.runs(serverId, taskId),
    queryFn: () =>
      unwrap(
        api.GET('/api/v1/servers/{id}/schedules/{taskId}/runs', {
          params: { path: { id: serverId, taskId }, query: { limit: 50 } },
        }),
      ),
  });
}

/** The next runs of a schedule being edited, as the panel computes them (its time zones and daylight saving time). */
export function useSchedulePreview(serverId: string, cron: string, timeZone: string) {
  return useQuery({
    queryKey: scheduleKeys.preview(serverId, cron, timeZone),
    queryFn: () =>
      unwrap(
        api.GET('/api/v1/servers/{id}/schedules/preview', {
          params: { path: { id: serverId }, query: { cron, timeZone } },
        }),
      ),
    enabled: cron.trim() !== '' && timeZone !== '',
    placeholderData: keepPreviousData,
    retry: false,
  });
}

/** Tasks change what the scheduler does to the server, and the audit log. */
function useSchedulesChanged(serverId: string) {
  const queryClient = useQueryClient();
  return () =>
    Promise.all([
      queryClient.invalidateQueries({ queryKey: scheduleKeys.list(serverId) }),
      queryClient.invalidateQueries({ queryKey: serverKeys.detail(serverId) }),
      queryClient.invalidateQueries({ queryKey: adminKeys.audit }),
    ]);
}

export function useSaveSchedule(serverId: string) {
  const changed = useSchedulesChanged(serverId);
  return useMutation({
    mutationFn: ({ taskId, request }: { taskId: string | null; request: ScheduledTaskRequest }) =>
      taskId === null
        ? unwrap(api.POST('/api/v1/servers/{id}/schedules', { params: { path: { id: serverId } }, body: request }))
        : unwrap(
            api.PUT('/api/v1/servers/{id}/schedules/{taskId}', {
              params: { path: { id: serverId, taskId } },
              body: request,
            }),
          ),
    onSettled: changed,
  });
}

export function useDeleteSchedule(serverId: string) {
  const changed = useSchedulesChanged(serverId);
  return useMutation({
    mutationFn: (taskId: string) =>
      unwrap(api.DELETE('/api/v1/servers/{id}/schedules/{taskId}', { params: { path: { id: serverId, taskId } } })),
    onSettled: changed,
  });
}

export function useRunSchedule(serverId: string) {
  const changed = useSchedulesChanged(serverId);
  return useMutation({
    mutationFn: (taskId: string) =>
      unwrap(api.POST('/api/v1/servers/{id}/schedules/{taskId}/run', { params: { path: { id: serverId, taskId } } })),
    onSettled: changed,
  });
}
