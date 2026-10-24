import { Upload } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, Spinner } from '../../components/ui/Button';
import { Dialog } from '../../components/ui/Dialog';
import { Alert, Card, PageHeader } from '../../components/ui/Display';
import { Field } from '../../components/ui/Form';
import { errorMessage } from '../../lib/errors';
import { can, canAnywhere, Permission } from '../../lib/permissions';
import { useCurrentUser } from '../auth/api';
import { useImportTemplate, useTemplatePreview, useTemplates } from './api';
import { CreateServerDialog } from './CreateServerDialog';
import { GameCard } from './GameCard';
import { newestOfEachGame } from './templateVersions';
import { ArtworkSetting } from '../games/ArtworkSetting';

/**
 * Game templates (docs/architecture/04): the ones shipped with Pulsar and the ones administrators imported, the newest
 * version of each game (older ones stay for the servers made from them, which their overview offers to move).
 */
export function TemplatesPage() {
  const { t } = useTranslation();
  const me = useCurrentUser();
  const templates = useTemplates();
  const [importing, setImporting] = useState(false);
  const canImport = can(me, Permission.TemplateManage);
  const canCreateServer = canAnywhere(me, Permission.ServerCreate);
  // The game a server is being created with, from its card.
  const [creatingWith, setCreatingWith] = useState<string | null>(null);

  return (
    <>
      <PageHeader
        title={t('templates.title')}
        description={t('templates.description')}
        actions={
          canImport && (
            <Button icon={Upload} onClick={() => setImporting(true)}>
              {t('templates.import')}
            </Button>
          )
        }
      />
      {can(me, Permission.SettingsManage) && <ArtworkSetting />}
      {templates.isPending && <Spinner label={t('common.loading')} />}
      {templates.isError && <Alert>{errorMessage(t, templates.error)}</Alert>}
      {templates.data && (
        <ul
          aria-label={t('templates.title')}
          className="grid grid-cols-1 gap-5 sm:grid-cols-2 xl:grid-cols-3 2xl:grid-cols-4"
        >
          {newestOfEachGame(templates.data).map((template) => (
            <li key={template.id} className="flex min-w-0">
              <GameCard
                template={template}
                onCreate={canCreateServer ? () => setCreatingWith(template.id) : undefined}
              />
            </li>
          ))}
        </ul>
      )}
      {importing && <ImportDialog onClose={() => setImporting(false)} />}
      {creatingWith && <CreateServerDialog templateId={creatingWith} onClose={() => setCreatingWith(null)} />}
    </>
  );
}

/**
 * Importing a manifest is installing software on every node: it is checked first, and the preview shows what it would
 * run and download before anything is stored.
 */
function ImportDialog({ onClose }: { onClose: () => void }) {
  const { t } = useTranslation();
  const [content, setContent] = useState('');
  const preview = useTemplatePreview();
  const importTemplate = useImportTemplate();
  const result = preview.data;
  const details = result?.details;

  return (
    <Dialog
      open
      onOpenChange={(open) => !open && onClose()}
      title={t('templates.import')}
      description={t('templates.importHint')}
      size="lg"
    >
      <div className="space-y-4">
        <Field label={t('templates.manifest')}>
          {(ids) => (
            <textarea
              {...ids}
              rows={10}
              value={content}
              spellCheck={false}
              onChange={(e) => {
                setContent(e.target.value);
                preview.reset();
              }}
              className="w-full rounded-md border border-border bg-bg px-3 py-2 font-mono text-xs"
            />
          )}
        </Field>
        {preview.isError && <Alert>{errorMessage(t, preview.error)}</Alert>}
        {importTemplate.isError && <Alert>{errorMessage(t, importTemplate.error)}</Alert>}
        {result && !result.valid && (
          <Alert>
            <p className="font-medium">{t('templates.invalid')}</p>
            <ul className="mt-2 list-disc pl-5 text-xs">
              {result.errors.map((error) => (
                <li key={`${error.path}:${error.message}`}>
                  <code>{error.path}</code>: {error.message}
                </li>
              ))}
            </ul>
          </Alert>
        )}
        {details && (
          <Card className="space-y-2 p-4 text-sm">
            <p className="font-medium">
              {details.template.name} <code className="text-muted">{details.template.key}</code>{' '}
              {details.template.version}
            </p>
            {result.alreadyImported && <p className="text-danger">{t('templates.alreadyImported')}</p>}
            {result.previousVersion && (
              <p>
                {t('templates.previousVersion', { version: result.previousVersion })}{' '}
                {result.changedSections.length > 0
                  ? t('templates.changed', { sections: result.changedSections.join(', ') })
                  : t('templates.unchanged')}
              </p>
            )}
            <dl className="grid grid-cols-[minmax(8rem,auto)_1fr] gap-x-4 gap-y-1">
              <dt className="text-muted">{t('templates.executable')}</dt>
              <dd>
                <code>{details.capabilities.executable}</code>
              </dd>
              <dt className="text-muted">{t('templates.steamApps')}</dt>
              <dd>
                {details.capabilities.steamAppIds.length > 0
                  ? details.capabilities.steamAppIds.join(', ')
                  : t('common.none')}
              </dd>
              <dt className="text-muted">{t('templates.addonSources')}</dt>
              <dd>
                {details.capabilities.addonSources?.length
                  ? details.capabilities.addonSources.join(', ')
                  : t('common.none')}
              </dd>
              <dt className="text-muted">{t('templates.configFiles')}</dt>
              <dd>
                {details.capabilities.configFiles.length > 0
                  ? details.capabilities.configFiles.join(', ')
                  : t('common.none')}
              </dd>
              <dt className="text-muted">{t('templates.ports')}</dt>
              <dd>
                {details.ports.length > 0
                  ? details.ports.map((p) => `${p.label} ${p.default}/${p.protocols.join('+')}`).join(', ')
                  : t('common.none')}
              </dd>
              <dt className="text-muted">{t('templates.variables')}</dt>
              <dd>{details.variables.map((v) => v.label).join(', ') || t('common.none')}</dd>
            </dl>
          </Card>
        )}
        {importTemplate.isSuccess && (
          <p role="status" className="text-sm text-success">
            {t('templates.imported')}
          </p>
        )}
        <div className="flex justify-end gap-2">
          <Button variant="secondary" onClick={onClose}>
            {importTemplate.isSuccess ? t('actions.close') : t('actions.cancel')}
          </Button>
          {!details || result.alreadyImported || importTemplate.isSuccess ? (
            <Button
              busy={preview.isPending}
              disabled={content.trim() === '' || importTemplate.isSuccess}
              onClick={() => preview.mutate(content)}
            >
              {t('templates.check')}
            </Button>
          ) : (
            <Button busy={importTemplate.isPending} onClick={() => importTemplate.mutate(content)}>
              {t('templates.confirmImport')}
            </Button>
          )}
        </div>
      </div>
    </Dialog>
  );
}
