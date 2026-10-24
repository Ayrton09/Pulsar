import { useTranslation } from 'react-i18next';
import { Checkbox, Field, Input, Select } from '../../components/ui/Form';
import type { TemplateVariable } from './api';
import { isGameOption, type VariableValues } from './variables';
import { WorkshopField } from './WorkshopField';

interface VariableFieldsProps {
  variables: TemplateVariable[];
  values: VariableValues;
  onChange: (key: string, value: string) => void;
  /** Whether the game's options start shown (the Settings tab) or folded (the create dialog). */
  optionsOpen?: boolean | undefined;
  /** Secret variables that already have a value (it is never sent to the browser). */
  secretsSet?: ReadonlySet<string> | undefined;
  errors?: Record<string, string | null> | undefined;
  /** Told when a number field holds text the browser cannot read as a number (it reports the value as empty). */
  onUnreadable?: ((key: string, unreadable: boolean) => void) | undefined;
  /** Variables the user may not change (not user editable, without server.settings.edit). */
  readOnly?: ((variable: TemplateVariable) => boolean) | undefined;
}

/**
 * One field per manifest variable, typed as the manifest says (docs/architecture/04, "Variables"). The game's options
 * (no value of the template's own) come after the rest, in a section of their own: left empty, Pulsar does not change
 * them in the game's files.
 */
export function VariableFields(props: VariableFieldsProps) {
  const { t } = useTranslation();
  const { variables, errors, optionsOpen } = props;
  if (variables.length === 0) {
    return <p className="text-sm text-muted">{t('servers.variables.none')}</p>;
  }

  const options = variables.filter(isGameOption);
  const main = variables.filter((variable) => !isGameOption(variable));
  // A wrong value is never hidden in a folded section.
  const optionError = options.some((variable) => !!errors?.[variable.key]);
  return (
    <div className="space-y-4">
      {main.map((variable) => (
        <VariableField key={variable.key} variable={variable} {...props} />
      ))}
      {options.length > 0 && (
        <details open={optionsOpen || optionError || undefined} className="rounded-md border border-border">
          <summary className="cursor-pointer px-3 py-2 text-sm font-medium">
            {t('servers.variables.options', { count: options.length })}
          </summary>
          <div className="space-y-4 border-t border-border p-3">
            <p className="text-xs text-muted">{t('servers.variables.optionsHint')}</p>
            {options.map((variable) => (
              <VariableField key={variable.key} variable={variable} {...props} />
            ))}
          </div>
        </details>
      )}
    </div>
  );
}

function VariableField({
  variable,
  values,
  onChange,
  secretsSet,
  errors,
  onUnreadable,
  readOnly,
}: VariableFieldsProps & { variable: TemplateVariable }) {
  const { t } = useTranslation();
  const value = values[variable.key] ?? '';
  const disabled = readOnly?.(variable) ?? false;
  const error = errors?.[variable.key];
  const name = variable.label;
  const label = variable.required ? `${name} *` : name;
  const numeric = variable.type === 'integer' || variable.type === 'number';
  const hint = [
    variable.description,
    numeric && variable.min != null && variable.max != null
      ? t('servers.variables.range', { min: variable.min, max: variable.max })
      : null,
    variable.secret && secretsSet?.has(variable.key) ? t('servers.variables.secretSet') : null,
    disabled ? t('servers.variables.protected') : null,
  ]
    .filter(Boolean)
    .join(' ');

  if (variable.type === 'boolean') {
    return (
      <div>
        <Checkbox
          label={name}
          checked={value === 'true'}
          disabled={disabled}
          onChange={(e) => onChange(variable.key, e.target.checked ? 'true' : 'false')}
        />
        {hint && <p className="mt-1 ml-6 text-xs text-muted">{hint}</p>}
      </div>
    );
  }

  return (
    <Field label={label} hint={hint || undefined} error={error ? t(error) : undefined}>
      {(ids) =>
        variable.workshop ? (
          <WorkshopField
            variable={{ ...variable, workshop: variable.workshop }}
            value={value}
            disabled={disabled}
            onChange={(next) => onChange(variable.key, next)}
            control={ids}
          />
        ) : variable.type === 'enum' ? (
          <Select {...ids} value={value} disabled={disabled} onChange={(e) => onChange(variable.key, e.target.value)}>
            {!variable.required && <option value="">{t('servers.variables.unset')}</option>}
            {/* A required choice with no value yet: shown as such, never as its first option, which the browser
                would show chosen while it is not (choosing it then changes nothing: an EULA with one option could
                never be accepted). */}
            {variable.required && value === '' && (
              <option value="" disabled>
                {t('servers.variables.choose')}
              </option>
            )}
            {variable.options.map((option) => (
              <option key={option.value} value={option.value}>
                {option.label}
              </option>
            ))}
          </Select>
        ) : (
          <Input
            {...ids}
            type={variable.secret ? 'password' : numeric ? 'number' : 'text'}
            inputMode={variable.type === 'integer' ? 'numeric' : variable.type === 'number' ? 'decimal' : undefined}
            step={variable.type === 'number' ? 'any' : undefined}
            min={variable.min ?? undefined}
            max={variable.max ?? undefined}
            maxLength={variable.maxLength ?? undefined}
            autoComplete={variable.secret ? 'new-password' : 'off'}
            placeholder={variable.secret && secretsSet?.has(variable.key) ? '••••••••' : (variable.default ?? '')}
            value={value}
            disabled={disabled}
            onChange={(e) => {
              onChange(variable.key, e.target.value);
              if (numeric) {
                onUnreadable?.(variable.key, e.target.validity.badInput);
              }
            }}
          />
        )
      }
    </Field>
  );
}
