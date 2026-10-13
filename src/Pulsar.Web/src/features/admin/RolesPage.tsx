import { zodResolver } from '@hookform/resolvers/zod';
import { useState } from 'react';
import { useForm, useWatch } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { z } from 'zod';
import { Button, Spinner } from '../../components/ui/Button';
import { Dialog } from '../../components/ui/Dialog';
import { Alert, Badge, PageHeader, Table, Td, Th } from '../../components/ui/Display';
import { Checkbox, Field, Input, Select } from '../../components/ui/Form';
import { errorMessage } from '../../lib/errors';
import { can, Permission } from '../../lib/permissions';
import { useCurrentUser } from '../auth/api';
import { groupPermissions } from './permissionGroups';
import {
  useCreateRole,
  useDeleteRole,
  usePermissionCatalog,
  useRequireTwoFactor,
  useRoles,
  useUpdateRole,
  type PermissionScope,
  type Role,
} from './api';

const scopes: PermissionScope[] = ['global', 'node', 'server'];

export function RolesPage() {
  const { t } = useTranslation();
  const user = useCurrentUser();
  const roles = useRoles();
  const [editing, setEditing] = useState<Role | 'new' | null>(null);
  const canManage = can(user, Permission.RoleManage);
  const requireTwoFactor = useRequireTwoFactor();
  // Set in the change handler itself: the mutation's own pending state arrives a render later, and a controlled
  // checkbox would flip back meanwhile.
  const [saving, setSaving] = useState<{ id: string; required: boolean } | null>(null);

  return (
    <>
      <PageHeader
        title={t('roles.title')}
        description={t('roles.description')}
        actions={canManage && <Button onClick={() => setEditing('new')}>{t('roles.create')}</Button>}
      />
      {roles.isPending && <Spinner label={t('common.loading')} />}
      {roles.isError && <Alert>{errorMessage(t, roles.error)}</Alert>}
      {requireTwoFactor.isError && (
        <div className="mb-4">
          <Alert>{errorMessage(t, requireTwoFactor.error)}</Alert>
        </div>
      )}
      {roles.data && (
        <Table label={t('roles.title')}>
          <thead>
            <tr>
              <Th>{t('roles.name')}</Th>
              <Th>{t('roles.scope')}</Th>
              <Th>{t('roles.permissions')}</Th>
              <Th>{t('roles.assignedUsers')}</Th>
              <Th>{t('roles.twoFactor')}</Th>
              {canManage && (
                <Th>
                  <span className="sr-only">{t('common.actions')}</span>
                </Th>
              )}
            </tr>
          </thead>
          <tbody>
            {roles.data.map((role) => (
              <tr key={role.id}>
                <Td>
                  <div className="flex items-center gap-2 font-medium">
                    {role.name}
                    {role.isSystem && <Badge tone="accent">{t('roles.builtIn')}</Badge>}
                  </div>
                  {role.description && <p className="mt-0.5 text-xs text-muted">{role.description}</p>}
                </Td>
                <Td>{t(`roles.scopes.${role.scope}`)}</Td>
                <Td>
                  {role.isSystem
                    ? t('roles.allPermissions')
                    : t('roles.permissionCount', { count: role.permissions.length })}
                </Td>
                <Td>{role.assignedUsers}</Td>
                <Td>
                  {canManage ? (
                    <Checkbox
                      label={<span className="sr-only">{t('roles.requireTwoFactorFor', { name: role.name })}</span>}
                      // The new value while it is saved; the saved one afterwards (or again, if saving failed).
                      checked={saving?.id === role.id ? saving.required : role.requiresTwoFactor}
                      disabled={saving !== null}
                      onChange={(e) => {
                        const change = { id: role.id, required: e.target.checked };
                        setSaving(change);
                        requireTwoFactor.mutate(change, { onSettled: () => setSaving(null) });
                      }}
                    />
                  ) : role.requiresTwoFactor ? (
                    <Badge tone="accent">{t('roles.required')}</Badge>
                  ) : (
                    <span className="text-muted">{t('common.none')}</span>
                  )}
                </Td>
                {canManage && (
                  <Td className="text-right">
                    {!role.isSystem && (
                      <Button variant="ghost" size="sm" onClick={() => setEditing(role)}>
                        {t('actions.edit')}
                      </Button>
                    )}
                  </Td>
                )}
              </tr>
            ))}
          </tbody>
        </Table>
      )}
      {editing && <RoleDialog role={editing === 'new' ? null : editing} onClose={() => setEditing(null)} />}
    </>
  );
}

const schema = z.object({
  name: z.string().trim().min(1, 'validation.required').max(64, 'validation.tooLong'),
  description: z.string().trim().max(512, 'validation.tooLong'),
  scope: z.enum(['global', 'node', 'server']),
});

type Values = z.infer<typeof schema>;

function RoleDialog({ role, onClose }: { role: Role | null; onClose: () => void }) {
  const { t } = useTranslation();
  const catalog = usePermissionCatalog();
  const create = useCreateRole();
  const update = useUpdateRole();
  const remove = useDeleteRole();
  const [selected, setSelected] = useState<Set<string>>(new Set(role?.permissions ?? []));
  const { register, handleSubmit, formState, control } = useForm<Values>({
    resolver: zodResolver(schema),
    defaultValues: { name: role?.name ?? '', description: role?.description ?? '', scope: role?.scope ?? 'server' },
  });
  const scope = useWatch({ control, name: 'scope' });
  const mutation = role ? update : create;
  const error = mutation.error ?? remove.error;

  const toggle = (key: string, on: boolean) =>
    setSelected((current) => {
      const next = new Set(current);
      if (on) {
        next.add(key);
      } else {
        next.delete(key);
      }
      return next;
    });

  const onSubmit = handleSubmit(({ name, description, scope: chosen }) => {
    // Without the catalog every permission would be dropped: the role would be saved empty.
    if (!catalog.data) {
      return;
    }
    // Only permissions valid for the chosen scope are sent (switching scope may leave others selected).
    const permissions = catalog.data
      .filter((p) => selected.has(p.key) && p.grantableScopes.includes(chosen))
      .map((p) => p.key);
    const body = { name, description: description || null, permissions };
    if (role) {
      update.mutate({ id: role.id, body }, { onSuccess: onClose });
    } else {
      create.mutate({ ...body, scope: chosen }, { onSuccess: onClose });
    }
  });

  return (
    <Dialog
      open
      onOpenChange={(open) => !open && onClose()}
      title={role ? t('roles.edit') : t('roles.create')}
      size="lg"
    >
      <form onSubmit={(e) => void onSubmit(e)} className="space-y-4" noValidate>
        {error && <Alert>{errorMessage(t, error)}</Alert>}
        <Field label={t('roles.name')} error={formState.errors.name && t(formState.errors.name.message ?? '')}>
          {(ids) => <Input {...ids} {...register('name')} />}
        </Field>
        <Field
          label={t('roles.descriptionLabel')}
          error={formState.errors.description && t(formState.errors.description.message ?? '')}
        >
          {(ids) => <Input {...ids} {...register('description')} />}
        </Field>
        <Field label={t('roles.scope')} hint={role ? t('roles.scopeFixed') : t('roles.scopeHint')}>
          {(ids) => (
            <Select {...ids} {...register('scope')} disabled={role !== null}>
              {scopes.map((s) => (
                <option key={s} value={s}>
                  {t(`roles.scopes.${s}`)}
                </option>
              ))}
            </Select>
          )}
        </Field>
        <fieldset className="space-y-3">
          <legend className="text-sm font-medium">{t('roles.permissions')}</legend>
          {catalog.isPending && <Spinner label={t('common.loading')} />}
          {catalog.isError && (
            <Alert>
              <div className="flex flex-wrap items-center justify-between gap-2">
                <span>{errorMessage(t, catalog.error)}</span>
                <Button variant="secondary" size="sm" busy={catalog.isFetching} onClick={() => void catalog.refetch()}>
                  {t('actions.retry')}
                </Button>
              </div>
            </Alert>
          )}
          {groupPermissions(catalog.data ?? [], scope).map(([group, permissions]) => (
            <div key={group} className="rounded-md border border-border p-3">
              <p className="mb-2 text-xs font-medium tracking-wide text-muted uppercase">
                {t(`permissionGroups.${group}`, { defaultValue: group })}
              </p>
              <div className="grid gap-2 sm:grid-cols-2">
                {permissions.map((p) => (
                  <Checkbox
                    key={p.key}
                    label={t(`permissions.${p.key}`, { defaultValue: p.key })}
                    checked={selected.has(p.key)}
                    onChange={(e) => toggle(p.key, e.target.checked)}
                  />
                ))}
              </div>
            </div>
          ))}
        </fieldset>
        <div className="flex flex-wrap justify-between gap-2 pt-2">
          {role ? (
            <Button
              variant="danger"
              busy={remove.isPending}
              onClick={() =>
                window.confirm(t('roles.deleteConfirm', { name: role.name })) &&
                remove.mutate(role.id, { onSuccess: onClose })
              }
            >
              {t('actions.delete')}
            </Button>
          ) : (
            <span />
          )}
          <div className="flex gap-2">
            <Button variant="secondary" onClick={onClose}>
              {t('actions.cancel')}
            </Button>
            <Button type="submit" busy={mutation.isPending} disabled={!catalog.data}>
              {t('actions.save')}
            </Button>
          </div>
        </div>
      </form>
    </Dialog>
  );
}
