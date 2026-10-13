import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { ApiError, api, readCookie, unwrap, XSRF_COOKIES, XSRF_HEADER, type Schemas } from '../../api/client';

export type FileEntry = Schemas['FileEntry'];
export type FileListing = Schemas['FileListing'];
export type FileDetails = Schemas['FileDetails'];

/** Largest file the editor opens or saves (the panel refuses larger ones with files.too_large). */
export const MAX_EDITABLE_BYTES = 1024 * 1024;

// Not under the servers' keys, which the live channel refreshes at every change of any server: listing a folder asks
// the node. The channel invalidates these only for the servers a ServersChanged notice names.
export const fileKeys = {
  all: (serverId: string) => ['serverFiles', serverId] as const,
  listing: (serverId: string, path: string) => [...fileKeys.all(serverId), path] as const,
};

/** A directory of the server ('' is the server directory). Not live: refreshed after changes made here, or on demand. */
export function useFileListing(serverId: string, path: string, enabled = true) {
  return useQuery({
    queryKey: fileKeys.listing(serverId, path),
    queryFn: () =>
      unwrap(api.GET('/api/v1/servers/{id}/files', { params: { path: { id: serverId }, query: { path } } })),
    enabled,
    retry: false,
  });
}

function useInvalidateFiles(serverId: string) {
  const queryClient = useQueryClient();
  return () => queryClient.invalidateQueries({ queryKey: fileKeys.all(serverId) });
}

export function useCreateDirectory(serverId: string) {
  const invalidate = useInvalidateFiles(serverId);
  return useMutation({
    mutationFn: (path: string) =>
      unwrap(
        api.POST('/api/v1/servers/{id}/files/directories', { params: { path: { id: serverId } }, body: { path } }),
      ),
    onSettled: invalidate,
  });
}

export function useMoveFile(serverId: string) {
  const invalidate = useInvalidateFiles(serverId);
  return useMutation({
    mutationFn: ({ path, destination }: { path: string; destination: string }) =>
      unwrap(
        api.POST('/api/v1/servers/{id}/files/move', {
          params: { path: { id: serverId } },
          body: { path, destination },
        }),
      ),
    onSettled: invalidate,
  });
}

export function useDeleteFile(serverId: string) {
  const invalidate = useInvalidateFiles(serverId);
  return useMutation({
    mutationFn: (path: string) =>
      unwrap(api.DELETE('/api/v1/servers/{id}/files', { params: { path: { id: serverId }, query: { path } } })),
    onSettled: invalidate,
  });
}

/** Where the browser downloads a file from (a plain link: the panel streams it from the node). */
export function downloadUrl(serverId: string, path: string): string {
  return `/api/v1/servers/${encodeURIComponent(serverId)}/files/download?path=${encodeURIComponent(path)}`;
}

function contentUrl(serverId: string, path: string): string {
  return `/api/v1/servers/${encodeURIComponent(serverId)}/files/content?path=${encodeURIComponent(path)}`;
}

/** A file opened in the editor: its bytes and the version to save against. */
export interface OpenedFile {
  bytes: Uint8Array;
  version: string;
}

export async function readFile(serverId: string, path: string, signal?: AbortSignal): Promise<OpenedFile> {
  const url = new URL(contentUrl(serverId, path), globalThis.location.origin);
  const response = await fetch(url, { credentials: 'same-origin', signal: signal ?? null });
  if (!response.ok) {
    throw await problem(response);
  }
  const version = (response.headers.get('ETag') ?? '').replace(/^"|"$/g, '');
  return { bytes: new Uint8Array(await response.arrayBuffer()), version };
}

export interface WriteOptions {
  /** The version the editor opened: the save fails with files.changed if the file changed since. */
  expectedVersion?: string;
  /** Fail with files.exists instead of replacing an existing file. */
  createOnly?: boolean;
  /** Upload progress, 0 to 1. */
  onProgress?: (fraction: number) => void;
  signal?: AbortSignal;
}

/**
 * Writes a file through the panel (editor saves, new files, uploads). XMLHttpRequest rather than fetch: it reports
 * upload progress.
 */
export function writeFile(
  serverId: string,
  path: string,
  body: Blob,
  options: WriteOptions = {},
): Promise<FileDetails> {
  return new Promise((resolve, reject) => {
    const request = new XMLHttpRequest();
    request.open('PUT', contentUrl(serverId, path));
    request.withCredentials = true;
    request.setRequestHeader('Content-Type', 'application/octet-stream');
    const token = XSRF_COOKIES.map((name) => readCookie(name)).find(Boolean);
    if (token) {
      request.setRequestHeader(XSRF_HEADER, token);
    }
    if (options.expectedVersion) {
      request.setRequestHeader('If-Match', `"${options.expectedVersion}"`);
    } else if (options.createOnly) {
      request.setRequestHeader('If-None-Match', '*');
    }
    request.upload.onprogress = (event) => {
      if (event.lengthComputable && options.onProgress) {
        options.onProgress(event.total === 0 ? 1 : event.loaded / event.total);
      }
    };
    request.onload = () => {
      const json = parseJson(request.responseText);
      if (request.status >= 200 && request.status < 300) {
        resolve(json as FileDetails);
      } else {
        reject(new ApiError(request.status, json as ConstructorParameters<typeof ApiError>[1]));
      }
    };
    request.onerror = () => reject(new ApiError(0, { title: 'Network error', code: 'http.network' }));
    request.onabort = () => reject(new DOMException('Upload cancelled', 'AbortError'));
    options.signal?.addEventListener('abort', () => request.abort(), { once: true });
    request.send(body);
  });
}

async function problem(response: Response): Promise<ApiError> {
  return new ApiError(response.status, parseJson(await response.text()) as ConstructorParameters<typeof ApiError>[1]);
}

function parseJson(text: string): unknown {
  try {
    return text ? JSON.parse(text) : undefined;
  } catch {
    return undefined;
  }
}
