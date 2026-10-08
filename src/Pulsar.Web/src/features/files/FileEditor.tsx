import { lazy, Suspense, useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ApiError } from '../../api/client';
import { Button, Spinner } from '../../components/ui/Button';
import { Alert, Card } from '../../components/ui/Display';
import { errorMessage } from '../../lib/errors';
import { downloadUrl, readFile, writeFile, type OpenedFile } from './api';
import { baseName } from './paths';
import { decodeText, lineEndings } from './text';

// CodeMirror is loaded only when a file is opened.
const CodeEditor = lazy(() => import('./CodeEditor'));

const BOM = new Uint8Array([0xef, 0xbb, 0xbf]);

export interface FileEditorProps {
  serverId: string;
  path: string;
  canEdit: boolean;
  onClose: () => void;
}

type Loaded = { request: number } & ({ file: OpenedFile } | { error: unknown });

/**
 * A text file of the server in the editor (docs/architecture/07, "Files"): opened with its version and saved against
 * it, so a save never overwrites a change made meanwhile (by someone else, or by the game) without the user choosing
 * to. Line endings and a byte order mark are kept as the file had them. The content is read once and again only when
 * the user asks: not a cached query, which the live channel would refresh under the user's edits.
 */
export function FileEditor({ serverId, path, canEdit, onClose }: FileEditorProps) {
  const { t } = useTranslation();
  const [request, setRequest] = useState(0);
  const [loaded, setLoaded] = useState<Loaded | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    readFile(serverId, path, controller.signal).then(
      (file) => setLoaded({ request, file }),
      (error: unknown) => {
        if (!controller.signal.aborted) {
          setLoaded({ request, error });
        }
      },
    );
    return () => controller.abort();
  }, [serverId, path, request]);

  if (loaded?.request !== request) {
    return <Spinner label={t('common.loading')} />;
  }
  if ('error' in loaded) {
    const tooLarge = loaded.error instanceof ApiError && loaded.error.code === 'files.too_large';
    return (
      <Card className="space-y-4 p-6">
        <Alert>{tooLarge ? t('files.editor.tooLarge') : errorMessage(t, loaded.error)}</Alert>
        <div className="flex gap-2">
          {tooLarge && (
            <a href={downloadUrl(serverId, path)} className="text-sm text-accent hover:underline">
              {t('files.download')}
            </a>
          )}
          <Button variant="secondary" onClick={onClose}>
            {t('files.editor.back')}
          </Button>
        </div>
      </Card>
    );
  }

  const decoded = decodeText(loaded.file.bytes);
  if (!decoded) {
    return (
      <Card className="space-y-4 p-6">
        <Alert>{t('files.editor.binary')}</Alert>
        <div className="flex items-center gap-4">
          <a href={downloadUrl(serverId, path)} className="text-sm text-accent hover:underline">
            {t('files.download')}
          </a>
          <Button variant="secondary" onClick={onClose}>
            {t('files.editor.back')}
          </Button>
        </div>
      </Card>
    );
  }

  return (
    <LoadedEditor
      key={request}
      serverId={serverId}
      path={path}
      canEdit={canEdit}
      initial={decoded.text}
      bom={decoded.bom}
      version={loaded.file.version}
      onReload={() => setRequest((current) => current + 1)}
      onClose={onClose}
    />
  );
}

interface LoadedEditorProps extends FileEditorProps {
  initial: string;
  bom: boolean;
  version: string;
  onReload: () => void;
}

function LoadedEditor({
  serverId,
  path,
  canEdit: mayEdit,
  initial,
  bom,
  version: opened,
  onReload,
  onClose,
}: LoadedEditorProps) {
  const { t } = useTranslation();
  // The editor writes every line with one ending: a file that mixes them would be changed, so it is only shown.
  const endings = lineEndings(initial);
  const canEdit = mayEdit && endings !== 'mixed';
  const [text, setText] = useState(initial);
  const [saved, setSaved] = useState(initial);
  const [version, setVersion] = useState(opened);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [conflict, setConflict] = useState(false);
  const dirty = text !== saved;

  useEffect(() => {
    if (!dirty) {
      return;
    }
    const warn = (event: BeforeUnloadEvent) => event.preventDefault();
    window.addEventListener('beforeunload', warn);
    return () => window.removeEventListener('beforeunload', warn);
  }, [dirty]);

  const save = async (overwrite = false) => {
    if (!canEdit || saving) {
      return;
    }
    setSaving(true);
    setError(null);
    const content = text;
    try {
      const parts: BlobPart[] = bom ? [BOM, content] : [content];
      const result = await writeFile(serverId, path, new Blob(parts), overwrite ? {} : { expectedVersion: version });
      setVersion(result.version);
      setSaved(content);
      setConflict(false);
    } catch (caught) {
      if (caught instanceof ApiError && caught.code === 'files.changed') {
        setConflict(true);
      } else {
        setError(caught);
      }
    } finally {
      setSaving(false);
    }
  };

  const close = () => {
    if (!dirty || window.confirm(t('files.editor.discard'))) {
      onClose();
    }
  };

  return (
    <div className="space-y-3">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div className="min-w-0">
          <h2 className="truncate font-mono text-sm font-semibold" title={path}>
            {path}
          </h2>
          <p role="status" className="text-xs text-muted">
            {!canEdit ? t('files.editor.readOnly') : dirty ? t('files.editor.unsaved') : t('files.editor.saved')}
          </p>
        </div>
        <div className="flex items-center gap-2">
          <a href={downloadUrl(serverId, path)} className="px-2 text-sm text-accent hover:underline">
            {t('files.download')}
          </a>
          <Button variant="secondary" onClick={close}>
            {t('actions.close')}
          </Button>
          {canEdit && (
            <Button busy={saving} disabled={!dirty} onClick={() => void save()}>
              {t('actions.save')}
            </Button>
          )}
        </div>
      </div>
      {conflict && (
        <Alert>
          <p>{t('files.editor.conflict')}</p>
          <div className="mt-2 flex gap-2">
            <Button variant="secondary" onClick={onReload}>
              {t('files.editor.reload')}
            </Button>
            <Button variant="danger" busy={saving} onClick={() => void save(true)}>
              {t('files.editor.overwrite')}
            </Button>
          </div>
        </Alert>
      )}
      {mayEdit && endings === 'mixed' && <Alert>{t('files.editor.mixedEndings')}</Alert>}
      {error !== null && <Alert>{errorMessage(t, error)}</Alert>}
      <Suspense fallback={<Spinner label={t('common.loading')} />}>
        <CodeEditor
          label={t('files.editor.label', { name: baseName(path) })}
          fileName={baseName(path)}
          initial={initial}
          lineSeparator={endings === '\r\n' || endings === '\r' ? endings : undefined}
          readOnly={!canEdit}
          onChange={setText}
          onSave={() => void save()}
        />
      </Suspense>
    </div>
  );
}
