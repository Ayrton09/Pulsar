import { useTranslation } from 'react-i18next';
import { Checkbox, Field, Input, Select } from '../../components/ui/Form';
import type { TemplateVariable } from './api';
import type { VariableValues } from './variables';

interface VariableFieldsProps {
  variables: TemplateVariable[];
  values: VariableValues;
  onChange: (key: string, value: string) => void;
  /** Secret variables that already have a value (it is never sent to the browser). */
  secretsSet?: ReadonlySet<string> | undefined;
  errors?: Record<string, string | null> | undefined;
  /** Variables the user may not change (not user editable, without server.settings.edit). */
  readOnly?: ((variable: TemplateVariable) => boolean) | undefined;
}

/** One field per manifest variable, typed as the manifest says (docs/architecture/04, "Variables"). */
export function VariableFields({ variables, values, onChange, secretsSet, errors, readOnly }: VariableFieldsProps) {
  const { t } = useTranslation();
  if (variables.length === 0) {
    return <p className="text-sm text-muted">{t('servers.variables.none')}</p>;
  }

  return (
    <div className="space-y-4">
      {variables.map((variable) => {
        const value = values[variable.key] ?? '';
        const disabled = readOnly?.(variable) ?? false;
        const error = errors?.[variable.key];
        const label = variable.required ? `${variable.label} *` : variable.label;
        const hint = [
          variable.description,
          variable.type === 'integer' && variable.min != null && variable.max != null
            ? t('servers.variables.range', { min: variable.min, max: variable.max })
            : null,
          variable.secret && secretsSet?.has(variable.key) ? t('servers.variables.secretSet') : null,
          disabled ? t('servers.variables.protected') : null,
        ]
          .filter(Boolean)
          .join(' ');

        if (variable.type === 'boolean') {
          return (
            <div key={variable.key}>
              <Checkbox
                label={variable.label}
                checked={value === 'true'}
                disabled={disabled}
                onChange={(e) => onChange(variable.key, e.target.checked ? 'true' : 'false')}
              />
              {hint && <p className="mt-1 ml-6 text-xs text-muted">{hint}</p>}
            </div>
          );
        }

        return (
          <Field key={variable.key} label={label} hint={hint || undefined} error={error ? t(error) : undefined}>
            {(ids) =>
              variable.type === 'enum' ? (
                <Select
                  {...ids}
                  value={value}
                  disabled={disabled}
                  onChange={(e) => onChange(variable.key, e.target.value)}
                >
                  {!variable.required && <option value="">{t('servers.variables.unset')}</option>}
                  {variable.options.map((option) => (
                    <option key={option.value} value={option.value}>
                      {option.label}
                    </option>
                  ))}
                </Select>
              ) : (
                <Input
                  {...ids}
                  type={variable.secret ? 'password' : variable.type === 'integer' ? 'number' : 'text'}
                  inputMode={variable.type === 'integer' ? 'numeric' : undefined}
                  min={variable.min ?? undefined}
                  max={variable.max ?? undefined}
                  maxLength={variable.maxLength ?? undefined}
                  autoComplete={variable.secret ? 'new-password' : 'off'}
                  placeholder={variable.secret && secretsSet?.has(variable.key) ? '••••••••' : (variable.default ?? '')}
                  value={value}
                  disabled={disabled}
                  onChange={(e) => onChange(variable.key, e.target.value)}
                />
              )
            }
          </Field>
        );
      })}
    </div>
  );
}
