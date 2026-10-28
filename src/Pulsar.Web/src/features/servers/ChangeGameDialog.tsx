import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';
import { Button, Spinner } from '../../components/ui/Button';
import { Dialog } from '../../components/ui/Dialog';
import { Alert } from '../../components/ui/Display';
import { Checkbox, Field, Select } from '../../components/ui/Form';
import { errorMessage } from '../../lib/errors';
import { useChangeGame, useTemplate, useTemplates, type ServerDetails, type TemplateVariable } from './api';
import { useGameText } from './gameText';
import { newestOfEachGame } from './templateVersions';
import { VariableFields } from './VariableFields';
import { initialValue, useUnreadableNumbers, variableError, type VariableValues } from './variables';

/** As at a server's creation: only what the game cannot do without. */
const needed = (variable: TemplateVariable) => variable.required && variable.default == null;

/**
 * Changing a stopped server's game (Pulsar 1.0.16): the game it becomes (its own too, to start again from nothing) and
 * what that game cannot do without; its files are deleted, with no backup, so the user says they know. Its name,
 * access, limits and backups stay; the new game starts with its own defaults.
 */
export function ChangeGameDialog({ details, onClose }: { details: ServerDetails; onClose: () => void }) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const change = useChangeGame();
  const templates = useTemplates();
  const [templateId, setTemplateId] = useState('');
  const [values, setValues] = useState<VariableValues>({});
  const [understood, setUnderstood] = useState(false);
  const [showErrors, setShowErrors] = useState(false);
  const unreadable = useUnreadableNumbers();
  const template = useTemplate(templateId || undefined);
  const gameText = useGameText(template.data?.template.key);
  const variables = useMemo(
    () => (template.data ? gameText.variables(template.data.variables).filter(needed) : []),
    [template.data, gameText],
  );
  const valueOf = (variable: TemplateVariable) => values[variable.key] ?? initialValue(variable);
  const errors = Object.fromEntries(
    variables.map((v) => [v.key, variableError(v, valueOf(v), false, unreadable.keys.has(v.key))]),
  ) as Record<string, string | null>;
  const valid = templateId !== '' && !!template.data && understood && Object.values(errors).every((e) => e === null);

  const submit = () => {
    if (!valid) {
      setShowErrors(true);
      return;
    }
    change.mutate(
      {
        id: details.server.id,
        templateId,
        variables: Object.fromEntries(
          variables.filter((v) => valueOf(v) !== '' && valueOf(v) !== initialValue(v)).map((v) => [v.key, valueOf(v)]),
        ),
      },
      // To its console, which shows the new install as it runs.
      { onSuccess: () => void navigate(`/servers/${details.server.id}?tab=console`) },
    );
  };

  return (
    <Dialog open onOpenChange={(open) => !open && onClose()} title={t('servers.changeGame.title')} size="lg">
      <div className="space-y-4">
        <Alert>{t('servers.changeGame.warning', { name: details.server.name })}</Alert>
        {change.isError && <Alert>{errorMessage(t, change.error)}</Alert>}
        {templates.isError && <Alert>{errorMessage(t, templates.error)}</Alert>}
        <Field label={t('servers.game')} error={showErrors && templateId === '' ? t('validation.required') : undefined}>
          {(ids) => (
            <Select
              {...ids}
              value={templateId}
              onChange={(e) => {
                setTemplateId(e.target.value);
                setValues({});
                unreadable.reset();
              }}
            >
              <option value="">{t('servers.chooseGame')}</option>
              {newestOfEachGame(templates.data ?? []).map((option, _, all) => (
                <option key={option.id} value={option.id}>
                  {all.some((other) => other.id !== option.id && other.name === option.name)
                    ? t('servers.gameWithTemplateVersion', { name: option.name, version: option.version })
                    : option.name}
                </option>
              ))}
            </Select>
          )}
        </Field>
        {template.isPending && templateId && <Spinner label={t('common.loading')} />}
        {template.isError && <Alert>{errorMessage(t, template.error)}</Alert>}
        {variables.length > 0 && (
          <>
            <p className="text-sm text-muted">{t('servers.wizard.neededHint')}</p>
            <VariableFields
              variables={variables}
              values={Object.fromEntries(variables.map((v) => [v.key, valueOf(v)]))}
              onChange={(key, value) => setValues((current) => ({ ...current, [key]: value }))}
              errors={showErrors ? errors : undefined}
              onUnreadable={unreadable.mark}
            />
          </>
        )}
        <Checkbox
          label={t('servers.changeGame.understood')}
          checked={understood}
          onChange={(e) => setUnderstood(e.target.checked)}
        />
        {showErrors && !understood && (
          <p className="text-xs text-danger">{t('servers.changeGame.understoodRequired')}</p>
        )}
        <div className="flex justify-end gap-2">
          <Button variant="secondary" onClick={onClose}>
            {t('actions.cancel')}
          </Button>
          <Button variant="danger" busy={change.isPending} onClick={submit}>
            {t('servers.changeGame.action')}
          </Button>
        </div>
      </div>
    </Dialog>
  );
}
