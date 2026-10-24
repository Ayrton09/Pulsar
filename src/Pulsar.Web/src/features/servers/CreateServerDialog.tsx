import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';
import { Button, Spinner } from '../../components/ui/Button';
import { Dialog } from '../../components/ui/Dialog';
import { Alert } from '../../components/ui/Display';
import { Field, Input, Select } from '../../components/ui/Form';
import { errorMessage } from '../../lib/errors';
import { canOnNode, Permission } from '../../lib/permissions';
import { useCurrentUser } from '../auth/api';
import { useWatchNodes } from '../live/hooks';
import { useAllocations, useNodePortsInUse, useNodes } from '../nodes/api';
import { useCreateServer, useTemplate, useTemplates, type TemplateDetails, type TemplateVariable } from './api';
import { useGameText } from './gameText';
import { PortNumberFields } from './PortFields';
import {
  checkPorts,
  chosenPorts,
  parsePort,
  portsReady,
  suggestAllocations,
  suggestNumbers,
  type NodeAllocation,
  type PortInUse,
} from './portChoices';
import { newestOfEachGame } from './templateVersions';
import { VariableFields } from './VariableFields';
import { initialValue, useUnreadableNumbers, variableError, type VariableValues } from './variables';

const steps = ['basics', 'settings', 'ports'] as const;
type Step = (typeof steps)[number];

/**
 * Creating a server (docs/architecture/07): name, node and game; then the game's variables; then its ports, which
 * start on the game's own (or the next free ones): whoever manages the node types any number, added to the node's
 * allocations when it has none; others choose among its free allocations. The panel then provisions and installs it;
 * the server page shows the progress. `templateId` starts it with that game chosen (the Games page's cards).
 */
export function CreateServerDialog({
  onClose,
  templateId: initialTemplate,
}: {
  onClose: () => void;
  templateId?: string;
}) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const create = useCreateServer();
  const nodes = useNodes();
  // Nodes that connect or drop while the dialog is open come and go from the list.
  useWatchNodes();
  const templates = useTemplates();
  const [step, setStep] = useState<Step>('basics');
  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [nodeId, setNodeId] = useState('');
  const [templateId, setTemplateId] = useState(initialTemplate ?? '');
  const [values, setValues] = useState<VariableValues>({});
  // Null until the user changes one: the suggestion follows the node's allocations meanwhile.
  const [ports, setPorts] = useState<Record<string, string> | null>(null);
  const [showErrors, setShowErrors] = useState(false);
  const unreadable = useUnreadableNumbers();
  const template = useTemplate(templateId || undefined);
  const allocations = useAllocations(nodeId || undefined);
  const me = useCurrentUser();
  // The nodes listed include those the user only sees: they may create servers on some of them.
  const connected =
    nodes.data?.filter((node) => node.connected && canOnNode(me, node.id, Permission.ServerCreate)) ?? [];
  // The game's labels in the reader's language (the template's own English for an imported game).
  const gameText = useGameText(template.data?.template.key);
  const details = useMemo(
    () =>
      template.data && {
        ...template.data,
        variables: gameText.variables(template.data.variables),
        ports: gameText.ports(template.data.ports),
      },
    [template.data, gameText],
  );
  // Whoever manages the node types port numbers (added to its allocations when missing); others choose allocations.
  const canAddPorts = nodeId !== '' && canOnNode(me, nodeId, Permission.NodeManage);
  const nodeAllocations: NodeAllocation[] = useMemo(() => allocations.data ?? [], [allocations.data]);
  // The ports other programs hold on the node's machine: the numbers proposed skip them.
  const portsInUse = useNodePortsInUse(nodeId || undefined, canAddPorts);
  const inUse = useMemo(() => portsInUse.data?.ports ?? [], [portsInUse.data]);
  const suggested = useMemo(
    () =>
      !details
        ? {}
        : canAddPorts
          ? suggestNumbers(details.ports, nodeAllocations, inUse)
          : suggestAllocations(details.ports, nodeAllocations),
    [details, canAddPorts, nodeAllocations, inUse],
  );
  const portValues = ports ?? suggested;

  const chooseTemplate = (id: string) => {
    setTemplateId(id);
    setValues({});
    unreadable.reset();
    setPorts(null);
  };

  const valueOf = (variable: TemplateVariable) => values[variable.key] ?? initialValue(variable);
  const basicsErrors = {
    name: name.trim() === '' ? 'validation.required' : name.trim().length > 64 ? 'validation.tooLong' : null,
    node: nodeId === '' ? 'validation.required' : null,
    template: templateId === '' ? 'validation.required' : null,
    description: description.trim().length > 512 ? 'validation.tooLong' : null,
  };
  const settingsErrors = Object.fromEntries(
    (details?.variables ?? []).map((v) => [v.key, variableError(v, valueOf(v), false, unreadable.keys.has(v.key))]),
  ) as Record<string, string | null>;
  const portsValid =
    !!details &&
    (canAddPorts
      ? portsReady(checkPorts(details.ports, portValues, nodeAllocations, true, undefined, inUse))
      : chosenPorts(details.ports).every((port) => !port.required || (portValues[port.name] ?? '') !== ''));
  const stepValid = (current: Step) =>
    current === 'basics'
      ? Object.values(basicsErrors).every((e) => e === null) && !!details
      : current === 'settings'
        ? Object.values(settingsErrors).every((e) => e === null)
        : portsValid;

  const next = () => {
    if (!stepValid(step)) {
      setShowErrors(true);
      return;
    }
    setShowErrors(false);
    setStep(steps[steps.indexOf(step) + 1] ?? step);
  };

  const submit = () => {
    if (!details || !stepValid('ports')) {
      setShowErrors(true);
      return;
    }
    // Only what differs from the manifest: defaults stay defaults (a later manifest default applies on reinstall).
    const variables = Object.fromEntries(
      details.variables
        .filter((v) => valueOf(v) !== '' && valueOf(v) !== initialValue(v))
        .map((v) => [v.key, valueOf(v)]),
    );
    // Numbers for whoever manages the node (null: an optional port off), allocations for the others.
    const portChoice = canAddPorts
      ? {
          portNumbers: Object.fromEntries(
            chosenPorts(details.ports).map((port) => [port.name, parsePort(portValues[port.name] ?? '')]),
          ),
        }
      : {
          ports: Object.fromEntries(
            chosenPorts(details.ports)
              .map((port) => [port.name, portValues[port.name] ?? ''] as const)
              .filter(([, value]) => value !== ''),
          ),
        };
    create.mutate(
      { name: name.trim(), description: description.trim() || null, nodeId, templateId, variables, ...portChoice },
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
                <Select
                  {...ids}
                  value={nodeId}
                  onChange={(e) => {
                    setNodeId(e.target.value);
                    // The ports chosen were for the node that was.
                    setPorts(null);
                  }}
                >
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
                  {/* The newest version of each game, by its name alone: a template's version read as the game's. Two
                      games of one name (imported under another key) say which template they are. */}
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
            {/* Without the game's details there is no next step: say why, and let the user ask again. */}
            {template.isError && (
              <Alert>
                <p>{errorMessage(t, template.error)}</p>
                <Button
                  variant="secondary"
                  size="sm"
                  className="mt-2"
                  busy={template.isFetching}
                  onClick={() => void template.refetch()}
                >
                  {t('actions.retry')}
                </Button>
              </Alert>
            )}
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
            onUnreadable={unreadable.mark}
          />
        )}

        {step === 'ports' && details && (
          <PortsStep
            details={details}
            canAdd={canAddPorts}
            allocations={nodeAllocations}
            inUse={inUse}
            loading={allocations.isPending || (canAddPorts && portsInUse.isPending)}
            error={allocations.error}
            ports={portValues}
            showErrors={showErrors}
            onChange={(port, value) => setPorts({ ...portValues, [port]: value })}
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
  canAdd: boolean;
  allocations: NodeAllocation[];
  inUse: PortInUse[];
  loading: boolean;
  error: Error | null;
  ports: Record<string, string>;
  showErrors: boolean;
  onChange: (port: string, value: string) => void;
}

function PortsStep({
  details,
  canAdd,
  allocations,
  inUse,
  loading,
  error,
  ports,
  showErrors,
  onChange,
}: PortsStepProps) {
  const { t } = useTranslation();
  if (details.ports.length === 0) {
    return <p className="text-sm text-muted">{t('servers.wizard.noPorts')}</p>;
  }
  if (error) {
    return <Alert>{errorMessage(t, error)}</Alert>;
  }
  if (loading) {
    return <Spinner label={t('common.loading')} />;
  }
  if (canAdd) {
    return (
      <PortNumberFields
        ports={details.ports}
        values={ports}
        onChange={onChange}
        allocations={allocations}
        inUse={inUse}
      />
    );
  }

  const free = allocations.filter((a) => !a.inUse);
  return (
    <div className="space-y-4">
      <p className="text-sm text-muted">{t('servers.ports.fromAllocations')}</p>
      {free.length === 0 && <Alert>{t('servers.wizard.noAllocations')}</Alert>}
      {details.ports.map((port) =>
        port.follows ? (
          <p key={port.name} className="text-sm">
            <span className="font-medium">{port.label}</span>{' '}
            <span className="text-muted">
              {t('servers.wizard.followsPort', {
                port: details.ports.find((p) => p.name === port.follows)?.label ?? port.follows,
                offset: port.offset ?? 1,
              })}
            </span>
          </p>
        ) : (
          <Field
            key={port.name}
            label={port.required ? `${port.label} *` : port.label}
            error={
              showErrors && port.required && (ports[port.name] ?? '') === '' ? t('validation.required') : undefined
            }
            hint={[
              port.description,
              t('servers.wizard.portDefault', {
                port: port.default,
                protocols: port.protocols.join('/').toUpperCase(),
              }),
            ]
              .filter(Boolean)
              .join(' ')}
          >
            {(ids) => (
              <Select {...ids} value={ports[port.name] ?? ''} onChange={(e) => onChange(port.name, e.target.value)}>
                <option value="">{port.required ? '—' : t('servers.wizard.noPort')}</option>
                {free.map((allocation) => (
                  <option key={allocation.id} value={allocation.id}>
                    {allocation.ipAddress}:{allocation.port}
                  </option>
                ))}
              </Select>
            )}
          </Field>
        ),
      )}
    </div>
  );
}
