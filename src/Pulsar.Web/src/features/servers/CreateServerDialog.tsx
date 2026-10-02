import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useNavigate } from 'react-router';
import { Button, Spinner } from '../../components/ui/Button';
import { Dialog } from '../../components/ui/Dialog';
import { Alert } from '../../components/ui/Display';
import { Field, Input, Select } from '../../components/ui/Form';
import { errorMessage } from '../../lib/errors';
import { can, Permission } from '../../lib/permissions';
import { useCurrentUser } from '../auth/api';
import { useAllocations, useNodes } from '../nodes/api';
import { useCreateServer, useTemplate, useTemplates, type TemplateDetails, type TemplateVariable } from './api';
import { VariableFields } from './VariableFields';
import { initialValue, variableError, type VariableValues } from './variables';

const steps = ['basics', 'settings', 'ports'] as const;
type Step = (typeof steps)[number];

/** "auto" lets the panel pick a free allocation (the port's default first); "" leaves an optional port out. */
const AUTOMATIC = 'auto';

/**
 * Creating a server (docs/architecture/07): name, node and game; then the game's variables; then its ports from the
 * node's free allocations. The panel then provisions and installs it; the server page shows the progress.
 */
export function CreateServerDialog({ onClose }: { onClose: () => void }) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const create = useCreateServer();
  const nodes = useNodes();
  const templates = useTemplates();
  const [step, setStep] = useState<Step>('basics');
  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [nodeId, setNodeId] = useState('');
  const [templateId, setTemplateId] = useState('');
  const [values, setValues] = useState<VariableValues>({});
  const [ports, setPorts] = useState<Record<string, string>>({});
  const [showErrors, setShowErrors] = useState(false);
  const template = useTemplate(templateId || undefined);
  const allocations = useAllocations(nodeId || undefined);
  const connected = nodes.data?.filter((node) => node.connected) ?? [];
  const details = template.data;

  const chooseTemplate = (id: string) => {
    setTemplateId(id);
    setValues({});
    setPorts({});
  };

  const valueOf = (variable: TemplateVariable) => values[variable.key] ?? initialValue(variable);
  const basicsErrors = {
    name: name.trim() === '' ? 'validation.required' : name.trim().length > 64 ? 'validation.tooLong' : null,
    node: nodeId === '' ? 'validation.required' : null,
    template: templateId === '' ? 'validation.required' : null,
    description: description.trim().length > 512 ? 'validation.tooLong' : null,
  };
  const settingsErrors = Object.fromEntries(
    (details?.variables ?? []).map((v) => [v.key, variableError(v, valueOf(v))]),
  ) as Record<string, string | null>;
  const stepValid = (current: Step) =>
    current === 'basics'
      ? Object.values(basicsErrors).every((e) => e === null) && !!details
      : current === 'settings'
        ? Object.values(settingsErrors).every((e) => e === null)
        : true;

  const next = () => {
    if (!stepValid(step)) {
      setShowErrors(true);
      return;
    }
    setShowErrors(false);
    setStep(steps[steps.indexOf(step) + 1] ?? step);
  };

  const submit = () => {
    if (!details) {
      return;
    }
    // Only what differs from the manifest: defaults stay defaults (a later manifest default applies on reinstall).
    const variables = Object.fromEntries(
      details.variables
        .filter((v) => valueOf(v) !== '' && valueOf(v) !== initialValue(v))
        .map((v) => [v.key, valueOf(v)]),
    );
    const chosen = Object.fromEntries(
      details.ports
        .map((port) => [port.name, ports[port.name] ?? (port.required ? AUTOMATIC : '')] as const)
        .filter(([, value]) => value !== AUTOMATIC && value !== '')
        .map(([key, value]) => [key, value]),
    );
    create.mutate(
      { name: name.trim(), description: description.trim() || null, nodeId, templateId, variables, ports: chosen },
      { onSuccess: (created) => void navigate(`/servers/${created.server.id}`) },
    );
  };

  return (
    <Dialog open onOpenChange={(open) => !open && onClose()} title={t('servers.create')} size="lg">
      <ol className="mb-6 flex gap-2 text-xs" aria-label={t('servers.wizard.steps')}>
        {steps.map((s, index) => (
          <li
            key={s}
            aria-current={s === step ? 'step' : undefined}
            className={`flex-1 rounded-md border px-2 py-1.5 ${s === step ? 'border-accent text-accent' : 'border-border text-muted'}`}
          >
            {index + 1}. {t(`servers.wizard.${s}`)}
          </li>
        ))}
      </ol>
      <div className="space-y-4">
        {create.isError && <Alert>{errorMessage(t, create.error)}</Alert>}
        {(nodes.isError || templates.isError) && <Alert>{errorMessage(t, nodes.error ?? templates.error)}</Alert>}

        {step === 'basics' && (
          <>
            <Field label={t('servers.name')} error={showErrors && basicsErrors.name ? t(basicsErrors.name) : undefined}>
              {(ids) => <Input {...ids} value={name} onChange={(e) => setName(e.target.value)} autoComplete="off" />}
            </Field>
            <Field
              label={t('servers.node')}
              hint={t('servers.nodeHint')}
              error={showErrors && basicsErrors.node ? t(basicsErrors.node) : undefined}
            >
              {(ids) => (
                <Select {...ids} value={nodeId} onChange={(e) => setNodeId(e.target.value)}>
                  <option value="">{t('servers.chooseNode')}</option>
                  {connected.map((node) => (
                    <option key={node.id} value={node.id}>
                      {node.name}
                    </option>
                  ))}
                </Select>
              )}
            </Field>
            <Field
              label={t('servers.game')}
              error={showErrors && basicsErrors.template ? t(basicsErrors.template) : undefined}
            >
              {(ids) => (
                <Select {...ids} value={templateId} onChange={(e) => chooseTemplate(e.target.value)}>
                  <option value="">{t('servers.chooseGame')}</option>
                  {templates.data?.map((option) => (
                    <option key={option.id} value={option.id}>
                      {option.name} {option.version}
                    </option>
                  ))}
                </Select>
              )}
            </Field>
            {template.isPending && templateId && <Spinner label={t('common.loading')} />}
            {details && <Requirements details={details} />}
            <Field
              label={t('servers.descriptionLabel')}
              error={showErrors && basicsErrors.description ? t(basicsErrors.description) : undefined}
            >
              {(ids) => (
                <Input
                  {...ids}
                  value={description}
                  onChange={(e) => setDescription(e.target.value)}
                  autoComplete="off"
                />
              )}
            </Field>
          </>
        )}

        {step === 'settings' && details && (
          <VariableFields
            variables={details.variables}
            values={Object.fromEntries(details.variables.map((v) => [v.key, valueOf(v)]))}
            onChange={(key, value) => setValues((current) => ({ ...current, [key]: value }))}
            errors={showErrors ? settingsErrors : undefined}
          />
        )}

        {step === 'ports' && details && (
          <PortsStep
            details={details}
            nodeId={nodeId}
            allocations={allocations.data ?? []}
            loading={allocations.isPending}
            ports={ports}
            onChange={(port, value) => setPorts((current) => ({ ...current, [port]: value }))}
          />
        )}

        <div className="flex justify-between gap-2 pt-2">
          <Button
            variant="secondary"
            onClick={() => (step === 'basics' ? onClose() : setStep(steps[steps.indexOf(step) - 1] ?? 'basics'))}
          >
            {step === 'basics' ? t('actions.cancel') : t('servers.wizard.back')}
          </Button>
          {step === 'ports' ? (
            <Button busy={create.isPending} onClick={submit}>
              {t('actions.create')}
            </Button>
          ) : (
            // The next steps come from the chosen game's details: wait for them instead of ignoring the click.
            <Button onClick={next} busy={!!templateId && template.isPending}>
              {t('servers.wizard.next')}
            </Button>
          )}
        </div>
      </div>
    </Dialog>
  );
}

function Requirements({ details }: { details: TemplateDetails }) {
  const { t } = useTranslation();
  if (details.minDiskGb == null && details.minMemoryMb == null) {
    return null;
  }
  return (
    <p className="rounded-md border border-border bg-bg px-3 py-2 text-xs text-muted">
      {t('servers.wizard.requirements', {
        disk: details.minDiskGb ?? '—',
        memory: details.minMemoryMb != null ? Math.round(details.minMemoryMb / 1024) : '—',
      })}
    </p>
  );
}

interface PortsStepProps {
  details: TemplateDetails;
  nodeId: string;
  allocations: { id: string; ipAddress: string; port: number; serverId: string | null }[];
  loading: boolean;
  ports: Record<string, string>;
  onChange: (port: string, value: string) => void;
}

function PortsStep({ details, nodeId, allocations, loading, ports, onChange }: PortsStepProps) {
  const { t } = useTranslation();
  const me = useCurrentUser();
  if (details.ports.length === 0) {
    return <p className="text-sm text-muted">{t('servers.wizard.noPorts')}</p>;
  }
  if (loading) {
    return <Spinner label={t('common.loading')} />;
  }

  const free = allocations.filter((a) => a.serverId === null);
  return (
    <div className="space-y-4">
      {free.length === 0 && (
        <Alert>
          {t('servers.wizard.noAllocations')}{' '}
          {can(me, Permission.NodeManage) && (
            <Link to={`/nodes/${nodeId}`} className="underline">
              {t('servers.wizard.addAllocations')}
            </Link>
          )}
        </Alert>
      )}
      {details.ports.map((port) => (
        <Field
          key={port.name}
          label={port.required ? `${port.label} *` : port.label}
          hint={[
            port.description,
            t('servers.wizard.portDefault', { port: port.default, protocols: port.protocols.join('/').toUpperCase() }),
          ]
            .filter(Boolean)
            .join(' ')}
        >
          {(ids) => (
            <Select
              {...ids}
              value={ports[port.name] ?? (port.required ? AUTOMATIC : '')}
              onChange={(e) => onChange(port.name, e.target.value)}
            >
              {port.required ? (
                <option value={AUTOMATIC}>{t('servers.wizard.automatic')}</option>
              ) : (
                <option value="">{t('servers.wizard.noPort')}</option>
              )}
              {free.map((allocation) => (
                <option key={allocation.id} value={allocation.id}>
                  {allocation.ipAddress}:{allocation.port}
                </option>
              ))}
            </Select>
          )}
        </Field>
      ))}
    </div>
  );
}
