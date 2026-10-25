/** The steps of a scheduled task of several steps, as the editor keeps them, and its presets. */
import type { TFunction } from 'i18next';
import { Permission } from '../../lib/permissions';
import type { SequenceStep, SequenceStepKind, SequenceStepRequest } from './api';

export const MAX_STEPS = 32;
export const MAX_COMMAND_LENGTH = 512;
/** What a game's broadcast command carries (the panel cuts there too). */
export const MAX_MESSAGE_LENGTH = 128;
const MAX_WAIT_SECONDS = 24 * 60 * 60;

/** Every step kind, as the editor offers them, and the permission each one needs (the panel checks it too). */
export const stepPermissions: Record<SequenceStepKind, string | null> = {
  broadcast: Permission.ServerConsoleWrite,
  command: Permission.ServerConsoleWrite,
  wait: null,
  stop: Permission.ServerStop,
  start: Permission.ServerStart,
  restart: Permission.ServerRestart,
  backup: Permission.ServerBackupsCreate,
  update: Permission.ServerInstall,
};

export const stepKinds = Object.keys(stepPermissions) as SequenceStepKind[];

/** A step being edited: a wait in seconds or minutes, as typed. */
export interface StepDraft {
  key: number;
  kind: SequenceStepKind;
  text: string;
  amount: string;
  unit: 'seconds' | 'minutes';
  validate: boolean;
  onlyIfWasRunning: boolean;
  always: boolean;
}

let lastKey = 0;

export function draft(kind: SequenceStepKind, changes: Partial<Omit<StepDraft, 'key' | 'kind'>> = {}): StepDraft {
  lastKey += 1;
  return {
    key: lastKey,
    kind,
    text: '',
    amount: '1',
    unit: 'minutes',
    validate: false,
    onlyIfWasRunning: false,
    always: false,
    ...changes,
  };
}

export function toDraft(step: SequenceStep): StepDraft {
  const minutes = step.seconds >= 60 && step.seconds % 60 === 0;
  return draft(step.kind, {
    text: step.text ?? '',
    amount: String(minutes ? step.seconds / 60 : step.seconds || 1),
    unit: minutes || step.kind !== 'wait' ? 'minutes' : 'seconds',
    validate: step.validate,
    onlyIfWasRunning: step.onlyIfWasRunning,
    always: step.always,
  });
}

function waitSeconds(step: StepDraft): number {
  const amount = Number(step.amount);
  return step.unit === 'minutes' ? amount * 60 : amount;
}

/** Only what the step's kind uses (the panel keeps no more). */
export function fromDraft(step: StepDraft): SequenceStepRequest {
  const restarts = step.kind === 'start' || step.kind === 'restart';
  return {
    kind: step.kind,
    text: step.kind === 'command' || step.kind === 'broadcast' ? step.text.trim() : null,
    seconds: step.kind === 'wait' ? waitSeconds(step) : 0,
    validate: step.kind === 'update' && step.validate,
    onlyIfWasRunning: restarts && step.onlyIfWasRunning,
    always: step.always,
  };
}

/** Why the step cannot be saved, in the UI language; null when it can. */
export function stepError(t: TFunction, step: StepDraft): string | null {
  const text = step.text.trim();
  switch (step.kind) {
    case 'command':
      return text === '' || text.length > MAX_COMMAND_LENGTH
        ? t('schedules.commandInvalid', { max: MAX_COMMAND_LENGTH })
        : null;
    case 'broadcast':
      return text === '' || text.length > MAX_MESSAGE_LENGTH
        ? t('schedules.messageInvalid', { max: MAX_MESSAGE_LENGTH })
        : null;
    case 'wait': {
      const seconds = waitSeconds(step);
      return Number.isInteger(seconds) && seconds >= 1 && seconds <= MAX_WAIT_SECONDS
        ? null
        : t('schedules.waitInvalid');
    }
    default:
      return null;
  }
}

export type Preset = 'restart' | 'update';

/** Which presets the user may use: each needs the steps it does (its warnings only when the game and the user can). */
export function presetsFor(may: (kind: SequenceStepKind) => boolean): Preset[] {
  return [
    ...(may('restart') ? (['restart'] as const) : []),
    ...(may('stop') && may('update') && may('start') ? (['update'] as const) : []),
  ];
}

/**
 * The steps of a preset: warnings at 5 minutes, 1 minute and 10 seconds when the game can message its players, then the
 * restart, or the stop, update and start again (even after a failed update, and only when the server was running).
 */
export function presetSteps(t: TFunction, preset: Preset, canWarn: boolean): StepDraft[] {
  const message = (time: string) =>
    t(preset === 'restart' ? 'schedules.presets.restartMessage' : 'schedules.presets.updateMessage', { time });
  const warnings = canWarn
    ? [
        draft('broadcast', { text: message('5 min') }),
        draft('wait', { amount: '4', unit: 'minutes' }),
        draft('broadcast', { text: message('1 min') }),
        draft('wait', { amount: '50', unit: 'seconds' }),
        draft('broadcast', { text: message('10 s') }),
        draft('wait', { amount: '10', unit: 'seconds' }),
      ]
    : [];
  return preset === 'restart'
    ? [...warnings, draft('restart')]
    : [...warnings, draft('stop'), draft('update'), draft('start', { onlyIfWasRunning: true, always: true })];
}
