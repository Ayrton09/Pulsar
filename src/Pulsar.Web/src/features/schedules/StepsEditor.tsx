import { ArrowDown, ArrowUp, Plus, Trash2 } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Button } from '../../components/ui/Button';
import { Checkbox, Field, Input, Select } from '../../components/ui/Form';
import { commandLine } from '../../lib/commandLine';
import type { SequenceStepKind } from './api';
import { draft, MAX_COMMAND_LENGTH, MAX_MESSAGE_LENGTH, MAX_STEPS, stepError, type StepDraft } from './steps';

interface StepsEditorProps {
  steps: StepDraft[];
  onChange: (steps: StepDraft[]) => void;
  /** The kinds the user may add (their permissions, and what the game can do). */
  kinds: SequenceStepKind[];
  /** Show each step's errors (after a first attempt to save). */
  showErrors: boolean;
}

/** The steps of a task, in order: added, moved and removed one by one. */
export function StepsEditor({ steps, onChange, kinds, showErrors }: StepsEditorProps) {
  const { t } = useTranslation();
  const change = (key: number, changes: Partial<StepDraft>) =>
    onChange(steps.map((step) => (step.key === key ? { ...step, ...changes } : step)));
  const move = (index: number, by: -1 | 1) => {
    const moved = steps[index];
    const other = steps[index + by];
    if (moved && other) {
      onChange(steps.map((step) => (step === moved ? other : step === other ? moved : step)));
    }
  };
  const add = () => {
    const kind = kinds.includes('broadcast') ? 'broadcast' : kinds[0];
    if (kind) {
      onChange([...steps, draft(kind)]);
    }
  };

  return (
    <div className="space-y-3">
      <ol className="space-y-3">
        {steps.map((step, index) => {
          const n = index + 1;
          const error = showErrors ? stepError(t, step) : null;
          // A step the user may no longer add (or the game no longer does) stays visible as it is.
          const options = kinds.includes(step.kind) ? kinds : [...kinds, step.kind];
          return (
            <li key={step.key} className="space-y-3 rounded-md border border-border p-3">
              <div className="flex items-end gap-2">
                <div className="min-w-0 flex-1">
                  <Field label={t('schedules.stepNumber', { n })}>
                    {(control) => (
                      <Select
                        {...control}
                        value={step.kind}
                        onChange={(e) => change(step.key, { kind: e.target.value as SequenceStepKind })}
                      >
                        {options.map((kind) => (
                          <option key={kind} value={kind}>
                            {t(`schedules.stepKinds.${kind}`)}
                          </option>
                        ))}
                      </Select>
                    )}
                  </Field>
                </div>
                <div className="flex shrink-0 gap-1 pb-1">
                  <Button
                    variant="ghost"
                    size="icon"
                    icon={ArrowUp}
                    disabled={index === 0}
                    aria-label={t('schedules.moveUp', { n })}
                    onClick={() => move(index, -1)}
                  />
                  <Button
                    variant="ghost"
                    size="icon"
                    icon={ArrowDown}
                    disabled={index === steps.length - 1}
                    aria-label={t('schedules.moveDown', { n })}
                    onClick={() => move(index, 1)}
                  />
                  <Button
                    variant="ghost"
                    size="icon"
                    icon={Trash2}
                    aria-label={t('schedules.removeStep', { n })}
                    onClick={() => onChange(steps.filter((s) => s.key !== step.key))}
                  />
                </div>
              </div>
              {step.kind === 'command' && (
                <Field label={t('schedules.command')} hint={t('schedules.commandHint')} error={error ?? undefined}>
                  {(control) => (
                    <Input
                      {...control}
                      value={step.text}
                      maxLength={MAX_COMMAND_LENGTH + 1}
                      className="font-mono"
                      onChange={(e) => change(step.key, { text: commandLine(e.target.value) })}
                    />
                  )}
                </Field>
              )}
              {step.kind === 'broadcast' && (
                <Field
                  label={t('schedules.message')}
                  hint={t('schedules.messageHint', { max: MAX_MESSAGE_LENGTH })}
                  error={error ?? undefined}
                >
                  {(control) => (
                    <Input
                      {...control}
                      value={step.text}
                      maxLength={MAX_MESSAGE_LENGTH + 1}
                      onChange={(e) => change(step.key, { text: commandLine(e.target.value) })}
                    />
                  )}
                </Field>
              )}
              {step.kind === 'wait' && (
                <Field label={t('schedules.waitFor')} error={error ?? undefined}>
                  {(control) => (
                    <div className="flex gap-2">
                      <Input
                        {...control}
                        type="number"
                        min={1}
                        inputMode="numeric"
                        value={step.amount}
                        className="w-28"
                        onChange={(e) => change(step.key, { amount: e.target.value })}
                      />
                      <Select
                        aria-label={t('schedules.waitUnit', { n })}
                        value={step.unit}
                        className="w-auto"
                        onChange={(e) => change(step.key, { unit: e.target.value as StepDraft['unit'] })}
                      >
                        <option value="seconds">{t('schedules.units.seconds')}</option>
                        <option value="minutes">{t('schedules.units.minutes')}</option>
                      </Select>
                    </div>
                  )}
                </Field>
              )}
              {step.kind === 'update' && (
                <Checkbox
                  label={t('schedules.validateStep')}
                  checked={step.validate}
                  onChange={(e) => change(step.key, { validate: e.target.checked })}
                />
              )}
              {(step.kind === 'start' || step.kind === 'restart') && (
                <div className="space-y-2">
                  <Checkbox
                    label={t('schedules.onlyIfWasRunning')}
                    checked={step.onlyIfWasRunning}
                    onChange={(e) => change(step.key, { onlyIfWasRunning: e.target.checked })}
                  />
                  <Checkbox
                    label={t('schedules.always')}
                    checked={step.always}
                    onChange={(e) => change(step.key, { always: e.target.checked })}
                  />
                </div>
              )}
            </li>
          );
        })}
      </ol>
      <Button
        variant="secondary"
        size="sm"
        icon={Plus}
        disabled={steps.length >= MAX_STEPS || kinds.length === 0}
        onClick={add}
      >
        {t('schedules.addStep')}
      </Button>
    </div>
  );
}
