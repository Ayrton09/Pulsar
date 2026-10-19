import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api, unwrap } from '../../api/client';
import { adminKeys } from '../admin/api';

export const appearanceKeys = {
  settings: ['appearance'] as const,
};

/**
 * Whether the panel shows Steam's game artwork. An administrator can turn it off, so that no visitor's browser contacts
 * Steam (docs/architecture/08, T41); the panel's Content-Security-Policy then allows no Steam images either.
 */
export function useAppearance() {
  return useQuery({
    queryKey: appearanceKeys.settings,
    queryFn: () => unwrap(api.GET('/api/v1/appearance')),
    staleTime: 5 * 60_000,
  });
}

export function useUpdateAppearance() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (steamArtwork: boolean) => unwrap(api.PUT('/api/v1/appearance', { body: { steamArtwork } })),
    onSuccess: async (appearance) => {
      queryClient.setQueryData(appearanceKeys.settings, appearance);
      await queryClient.invalidateQueries({ queryKey: adminKeys.audit });
    },
  });
}
