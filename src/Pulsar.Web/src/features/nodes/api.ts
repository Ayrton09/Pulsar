import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api, unwrap, type Schemas } from '../../api/client';
import { adminKeys } from '../admin/api';

export type NodeSummary = Schemas['NodeSummary'];
export type NodeDetails = Schemas['NodeDetails'];
export type NodeCredential = Schemas['NodeCredentialView'];
export type EnrollmentInstructions = Schemas['EnrollmentInstructions'];
export type CreatedNode = Schemas['CreatedNode'];

export type EnrollmentTokenView = Schemas['EnrollmentTokenView'];
export type CreatedEnrollmentToken = Schemas['CreatedEnrollmentToken'];

export const nodeKeys = {
  all: ['nodes'] as const,
  enrollmentTokens: ['enrollment-tokens'] as const,
  detail: (id: string) => ['nodes', id] as const,
  allocations: (id: string) => ['nodes', id, 'allocations'] as const,
};

export type AllocationView = Schemas['AllocationView'];

/** The node's ip:port pairs that servers can be given. */
export function useAllocations(nodeId: string | undefined) {
  return useQuery({
    queryKey: nodeKeys.allocations(nodeId ?? ''),
    queryFn: () => unwrap(api.GET('/api/v1/nodes/{id}/allocations', { params: { path: { id: nodeId ?? '' } } })),
    enabled: !!nodeId,
  });
}

export function useCreateAllocations() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ nodeId, body }: { nodeId: string; body: Schemas['CreateAllocationsRequest'] }) =>
      unwrap(api.POST('/api/v1/nodes/{id}/allocations', { params: { path: { id: nodeId } }, body })),
    onSuccess: (_, { nodeId }) => queryClient.invalidateQueries({ queryKey: nodeKeys.allocations(nodeId) }),
  });
}

export function useDeleteAllocation() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ nodeId, allocationId }: { nodeId: string; allocationId: string }) =>
      unwrap(
        api.DELETE('/api/v1/nodes/{id}/allocations/{allocationId}', { params: { path: { id: nodeId, allocationId } } }),
      ),
    onSuccess: (_, { nodeId }) => queryClient.invalidateQueries({ queryKey: nodeKeys.allocations(nodeId) }),
  });
}

/** Kept fresh by the live channel (useWatchNodes): connections and usage (every 15 s) are pushed. */
export function useNodes(enabled = true) {
  return useQuery({
    queryKey: nodeKeys.all,
    queryFn: () => unwrap(api.GET('/api/v1/nodes')),
    enabled,
  });
}

/** Kept fresh by the live channel (useWatchNodes). */
export function useNode(id: string) {
  return useQuery({
    queryKey: nodeKeys.detail(id),
    queryFn: () => unwrap(api.GET('/api/v1/nodes/{id}', { params: { path: { id } } })),
  });
}

/** Node changes show up in the list, the node page and the audit log. */
function useNodesChanged() {
  const queryClient = useQueryClient();
  return () =>
    Promise.all([
      queryClient.invalidateQueries({ queryKey: nodeKeys.all }),
      queryClient.invalidateQueries({ queryKey: adminKeys.audit }),
    ]);
}

export function useCreateNode() {
  const changed = useNodesChanged();
  return useMutation({
    mutationFn: (body: Schemas['CreateNodeRequest']) => unwrap(api.POST('/api/v1/nodes', { body })),
    onSuccess: changed,
  });
}

export function useUpdateNode() {
  const changed = useNodesChanged();
  return useMutation({
    mutationFn: ({ id, body }: { id: string; body: Schemas['UpdateNodeRequest'] }) =>
      unwrap(api.PUT('/api/v1/nodes/{id}', { params: { path: { id } }, body })),
    onSuccess: changed,
  });
}

export function useSetNodeEnabled() {
  const changed = useNodesChanged();
  return useMutation({
    mutationFn: ({ id, enabled }: { id: string; enabled: boolean }) =>
      enabled
        ? unwrap(api.POST('/api/v1/nodes/{id}/enable', { params: { path: { id } } }))
        : unwrap(api.POST('/api/v1/nodes/{id}/disable', { params: { path: { id } } })),
    onSuccess: changed,
  });
}

export function useDeleteNode() {
  const changed = useNodesChanged();
  return useMutation({
    mutationFn: (id: string) => unwrap(api.DELETE('/api/v1/nodes/{id}', { params: { path: { id } } })),
    onSuccess: changed,
  });
}

export function usePingNode() {
  return useMutation({
    mutationFn: (id: string) => unwrap(api.POST('/api/v1/nodes/{id}/ping', { params: { path: { id } } })),
  });
}

export function useCreateEnrollmentToken() {
  const changed = useNodesChanged();
  return useMutation({
    mutationFn: (id: string) => unwrap(api.POST('/api/v1/nodes/{id}/enrollment-tokens', { params: { path: { id } } })),
    onSuccess: changed,
  });
}

export function useRevokeCredential() {
  const changed = useNodesChanged();
  return useMutation({
    mutationFn: ({ id, credentialId, reason }: { id: string; credentialId: string; reason: string | null }) =>
      unwrap(
        api.POST('/api/v1/nodes/{id}/credentials/{credentialId}/revoke', {
          params: { path: { id, credentialId } },
          body: { reason },
        }),
      ),
    onSuccess: changed,
  });
}

export type PanelKeysOverview = Schemas['PanelKeysOverview'];

export function useRotateNodeKey() {
  const changed = useNodesChanged();
  return useMutation({
    mutationFn: (id: string) => unwrap(api.POST('/api/v1/nodes/{id}/credentials/rotate', { params: { path: { id } } })),
    onSuccess: changed,
  });
}

export const panelKeysKey = ['panel-keys'] as const;

export function usePanelKeys() {
  return useQuery({
    queryKey: panelKeysKey,
    queryFn: () => unwrap(api.GET('/api/v1/panel-keys')),
  });
}

function usePanelKeysChanged() {
  const queryClient = useQueryClient();
  const nodesChanged = useNodesChanged();
  return (overview: PanelKeysOverview) => {
    queryClient.setQueryData(panelKeysKey, overview);
    return nodesChanged();
  };
}

export function useRotatePanelKey() {
  const changed = usePanelKeysChanged();
  return useMutation({
    mutationFn: () => unwrap(api.POST('/api/v1/panel-keys/rotate')),
    onSuccess: changed,
  });
}

export function useRetirePanelKey() {
  const changed = usePanelKeysChanged();
  return useMutation({
    mutationFn: (keyId: string) =>
      unwrap(api.POST('/api/v1/panel-keys/{keyId}/retire', { params: { path: { keyId } } })),
    onSuccess: changed,
  });
}

/** Lets the agent of a node that waited for approval connect. */
export function useApproveNode() {
  const changed = useNodesChanged();
  return useMutation({
    mutationFn: (id: string) => unwrap(api.POST('/api/v1/nodes/{id}/approve', { params: { path: { id } } })),
    onSuccess: changed,
  });
}

/** Lets the agents of nodes that waited for approval connect (several at once from the list). */
export function useApproveNodes() {
  const changed = useNodesChanged();
  return useMutation({
    mutationFn: (nodeIds: string[]) => unwrap(api.POST('/api/v1/nodes/approve', { body: { nodeIds } })),
    onSuccess: changed,
  });
}

/** Reusable enrollment tokens: each use enrolls a new node (Intune, group policy, scripts). */
export function useEnrollmentTokens(enabled = true) {
  return useQuery({
    queryKey: nodeKeys.enrollmentTokens,
    queryFn: () => unwrap(api.GET('/api/v1/enrollment-tokens')),
    enabled,
  });
}

function useEnrollmentTokensChanged() {
  const queryClient = useQueryClient();
  return () =>
    Promise.all([
      queryClient.invalidateQueries({ queryKey: nodeKeys.enrollmentTokens }),
      queryClient.invalidateQueries({ queryKey: adminKeys.audit }),
    ]);
}

export function useCreateReusableToken() {
  const changed = useEnrollmentTokensChanged();
  return useMutation({
    mutationFn: (body: Schemas['CreateEnrollmentTokenRequest']) =>
      unwrap(api.POST('/api/v1/enrollment-tokens', { body })),
    onSuccess: changed,
  });
}

export function useRevokeReusableToken() {
  const changed = useEnrollmentTokensChanged();
  return useMutation({
    mutationFn: (id: string) => unwrap(api.POST('/api/v1/enrollment-tokens/{id}/revoke', { params: { path: { id } } })),
    onSuccess: changed,
  });
}
