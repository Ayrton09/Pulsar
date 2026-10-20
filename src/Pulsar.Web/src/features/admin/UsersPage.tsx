import { zodResolver } from '@hookform/resolvers/zod';
import { useState } from 'react';
import { useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { z } from 'zod';
import { Button, Spinner } from '../../components/ui/Button';
import { Dialog } from '../../components/ui/Dialog';
import { Alert, Badge, PageHeader, Table, Td, Th } from '../../components/ui/Display';
import { Checkbox, Field, Input, Select } from '../../components/ui/Form';
import { errorMessage, fieldErrors } from '../../lib/errors';
import { useFormatDate } from '../../lib/format';
import { can, Permission } from '../../lib/permissions';
import { useCurrentUser } from '../auth/api';
import { useNodes } from '../nodes/api';
import { useServers } from '../servers/api';
import { MIN_PASSWORD_LENGTH } from '../auth/SetupPage';
import {
  useCreateUser,
  useDeleteUser,
  useDeleteUserSshKeys,
  useResetPassword,
  useResetTwoFactor,
  useRevokeUserApiKeys,
  useRoles,
  useSetUserAccess,
  useSetUserRoles,
  useUpdateUser,
  useUsers,
  type Access,
  type Role,
  type User,
} from './api';

export function UsersPage() {
  const { t } = useTranslation();
  const me = useCurrentUser();
  const users = useUsers();
  const formatDate = useFormatDate();
  // The id only: the dialog shows the user as the list now has them (after a reset, a role change…).
  const [editing, setEditing] = useState<string | 'new' | null>(null);
  const editedUser = users.data?.find((user) => user.id === editing);
  const canManage = can(me, Permission.UserManage);

  return (
    <>
      <PageHeader
        title={t('users.title')}
        description={t('users.description')}
        actions={canManage && <Button onClick={() => setEditing('new')}>{t('users.create')}</Button>}
      />
      {users.isPending && <Spinner label={t('common.loading')} />}
      {users.isError && <Alert>{errorMessage(t, users.error)}</Alert>}
      {users.data && (
        <Table label={t('users.title')}>
          <thead>
            <tr>
              <Th>{t('users.user')}</Th>
              <Th>{t('users.roles')}</Th>
              <Th>{t('users.status')}</Th>
              <Th>{t('users.lastLogin')}</Th>
              {canManage && (
                <Th>
                  <span className="sr-only">{t('common.actions')}</span>
                </Th>
              )}
            </tr>
          </thead>
          <tbody>
            {users.data.map((user) => (
              <tr key={user.id}>
                <Td>
                  <p className="font-medium">{user.displayName ?? user.userName}</p>
                  {(user.displayName ?? user.email) && (
                    <p className="text-xs text-muted">
                      {[user.displayName && user.userName, user.email].filter(Boolean).join(' · ')}
                    </p>
                  )}
                </Td>
                <Td>
                  {user.roles.length > 0 && user.roles.map((r) => r.name).join(', ')}
                  {user.access.map((access) => (
                    <p key={`${access.scope}-${access.targetId}-${access.role.id}`} className="text-xs text-muted">
                      {t('users.access.on', { role: access.role.name, target: access.targetName })}
                    </p>
                  ))}
                  {user.roles.length === 0 && user.access.length === 0 && (
                    <span className="text-muted">{t('common.none')}</span>
                  )}
                </Td>
                <Td>
                  <div className="flex flex-wrap gap-1">
                    {user.isDisabled ? (
                      <Badge tone="danger">{t('users.disabled')}</Badge>
                    ) : (
                      <Badge tone="success">{t('users.active')}</Badge>
                    )}
                    {user.twoFactorEnabled && <Badge tone="accent">{t('users.twoFactor')}</Badge>}
                  </div>
                </Td>
                <Td>
                  {user.lastLoginAt ? (
                    formatDate(user.lastLoginAt)
                  ) : (
                    <span className="text-muted">{t('common.never')}</span>
                  )}
                </Td>
                {canManage && (
                  <Td className="text-right">
                    <Button variant="ghost" size="sm" onClick={() => setEditing(user.id)}>
                      {t('actions.edit')}
                    </Button>
                  </Td>
                )}
              </tr>
            ))}
          </tbody>
        </Table>
      )}
      {editing === 'new' && <CreateUserDialog onClose={() => setEditing(null)} />}
      {editedUser && (
        <EditUserDialog user={editedUser} isSelf={editedUser.id === me.id} onClose={() => setEditing(null)} />
      )}
    </>
  );
}

function RoleChecklist({
  roles,
  selected,
  onChange,
}: {
  roles: Role[];
  selected: Set<string>;
  onChange: (next: Set<string>) => void;
}) {
  const { t } = useTranslation();
  const globalRoles = roles.filter((r) => r.scope === 'global');
  return (
    <fieldset className="space-y-2">
      <legend className="mb-1 text-sm font-medium">{t('users.roles')}</legend>
      {globalRoles.map((role) => (
        <Checkbox
          key={role.id}
          label={
            <>
              {role.name}
              {role.description && <span className="block text-xs text-muted">{role.description}</span>}
            </>
          }
          checked={selected.has(role.id)}
          onChange={(e) => {
            const next = new Set(selected);
            if (e.target.checked) {
              next.add(role.id);
            } else {
              next.delete(role.id);
            }
            onChange(next);
          }}
        />
      ))}
    </fieldset>
  );
}

const createSchema = z.object({
  userName: z.string().trim().min(1, 'validation.required').max(64, 'validation.tooLong'),
  displayName: z.string().trim().max(128, 'validation.tooLong'),
  email: z.union([z.literal(''), z.email('validation.email')]),
  password: z.string().min(MIN_PASSWORD_LENGTH, 'validation.passwordLength'),
});

type CreateValues = z.infer<typeof createSchema>;

function CreateUserDialog({ onClose }: { onClose: () => void }) {
  const { t } = useTranslation();
  const roles = useRoles();
  const create = useCreateUser();
  const [roleIds, setRoleIds] = useState<Set<string>>(new Set());
  const { register, handleSubmit, formState } = useForm<CreateValues>({
    resolver: zodResolver(createSchema),
    defaultValues: { userName: '', displayName: '', email: '', password: '' },
  });
  const server = fieldErrors(create.error);
  const message = (key: keyof CreateValues) => {
    const error = formState.errors[key]?.message;
    return error ? t(error, { min: MIN_PASSWORD_LENGTH }) : server[key];
  };

  const onSubmit = handleSubmit(({ userName, displayName, email, password }) =>
    create.mutate(
      { userName, password, displayName: displayName || null, email: email || null, roleIds: [...roleIds] },
      { onSuccess: onClose },
    ),
  );

  return (
    <Dialog open onOpenChange={(open) => !open && onClose()} title={t('users.create')}>
      <form onSubmit={(e) => void onSubmit(e)} className="space-y-4" noValidate>
        {create.isError && <Alert>{errorMessage(t, create.error)}</Alert>}
        <Field label={t('auth.userName')} error={message('userName')}>
          {(ids) => <Input {...ids} {...register('userName')} autoComplete="off" />}
        </Field>
        <Field label={t('users.displayName')} error={message('displayName')}>
          {(ids) => <Input {...ids} {...register('displayName')} autoComplete="off" />}
        </Field>
        <Field label={t('users.email')} error={message('email')}>
          {(ids) => <Input {...ids} {...register('email')} type="email" autoComplete="off" />}
        </Field>
        <Field
          label={t('auth.password')}
          hint={t('auth.passwordHint', { min: MIN_PASSWORD_LENGTH })}
          error={message('password')}
        >
          {(ids) => <Input {...ids} {...register('password')} type="password" autoComplete="new-password" />}
        </Field>
        {roles.data && <RoleChecklist roles={roles.data} selected={roleIds} onChange={setRoleIds} />}
        <div className="flex justify-end gap-2 pt-2">
          <Button variant="secondary" onClick={onClose}>
            {t('actions.cancel')}
          </Button>
          <Button type="submit" busy={create.isPending}>
            {t('actions.create')}
          </Button>
        </div>
      </form>
    </Dialog>
  );
}

const profileSchema = z.object({
  displayName: z.string().trim().max(128, 'validation.tooLong'),
  email: z.union([z.literal(''), z.email('validation.email')]),
  isDisabled: z.boolean(),
});

type ProfileValues = z.infer<typeof profileSchema>;

function EditUserDialog({ user, isSelf, onClose }: { user: User; isSelf: boolean; onClose: () => void }) {
  const { t } = useTranslation();
  const roles = useRoles();
  const update = useUpdateUser();
  const setRoles = useSetUserRoles();
  const reset = useResetPassword();
  const remove = useDeleteUser();
  const resetTwoFactor = useResetTwoFactor();
  const revokeKeys = useRevokeUserApiKeys();
  const deleteSshKeys = useDeleteUserSshKeys();
  const [apiKeys, setApiKeys] = useState(user.apiKeys);
  const [sshKeys, setSshKeys] = useState(user.sshKeys);
  const [roleIds, setRoleIds] = useState<Set<string>>(new Set(user.roles.map((r) => r.id)));
  const [newPassword, setNewPassword] = useState('');
  const [notice, setNotice] = useState<string | null>(null);
  const { register, handleSubmit, formState } = useForm<ProfileValues>({
    resolver: zodResolver(profileSchema),
    defaultValues: { displayName: user.displayName ?? '', email: user.email ?? '', isDisabled: user.isDisabled },
  });
  const error =
    update.error ??
    setRoles.error ??
    reset.error ??
    remove.error ??
    resetTwoFactor.error ??
    revokeKeys.error ??
    deleteSshKeys.error;

  const onSaveProfile = handleSubmit(({ displayName, email, isDisabled }) =>
    update.mutate(
      { id: user.id, body: { displayName: displayName || null, email: email || null, isDisabled } },
      { onSuccess: () => setNotice(t('users.saved')) },
    ),
  );

  return (
    <Dialog open onOpenChange={(open) => !open && onClose()} title={t('users.editTitle', { name: user.userName })}>
      <div className="space-y-6">
        {error && <Alert>{errorMessage(t, error)}</Alert>}
        {notice && !error && (
          <p role="status" className="text-sm text-success">
            {notice}
          </p>
        )}
        {isSelf && <p className="text-sm text-muted">{t('users.selfNotice')}</p>}

        <form onSubmit={(e) => void onSaveProfile(e)} className="space-y-4" noValidate>
          <Field
            label={t('users.displayName')}
            error={formState.errors.displayName && t(formState.errors.displayName.message ?? '')}
          >
            {(ids) => <Input {...ids} {...register('displayName')} />}
          </Field>
          <Field label={t('users.email')} error={formState.errors.email && t(formState.errors.email.message ?? '')}>
            {(ids) => <Input {...ids} {...register('email')} type="email" />}
          </Field>
          {!isSelf && <Checkbox label={t('users.disableLabel')} {...register('isDisabled')} />}
          <div className="flex justify-end">
            <Button type="submit" variant="secondary" busy={update.isPending} disabled={isSelf}>
              {t('users.saveProfile')}
            </Button>
          </div>
        </form>

        {!isSelf && roles.data && (
          <div className="space-y-3 border-t border-border pt-4">
            <RoleChecklist roles={roles.data} selected={roleIds} onChange={setRoleIds} />
            <div className="flex justify-end">
              <Button
                variant="secondary"
                busy={setRoles.isPending}
                onClick={() =>
                  setRoles.mutate(
                    { id: user.id, roleIds: [...roleIds] },
                    { onSuccess: () => setNotice(t('users.saved')) },
                  )
                }
              >
                {t('users.saveRoles')}
              </Button>
            </div>
          </div>
        )}

        {!isSelf && roles.data && (
          <AccessSection user={user} roles={roles.data} onSaved={() => setNotice(t('users.saved'))} />
        )}

        {!isSelf && (
          <div className="space-y-3 border-t border-border pt-4">
            <Field label={t('users.newPassword')} hint={t('auth.passwordHint', { min: MIN_PASSWORD_LENGTH })}>
              {(ids) => (
                <Input
                  {...ids}
                  type="password"
                  autoComplete="new-password"
                  value={newPassword}
                  onChange={(e) => setNewPassword(e.target.value)}
                />
              )}
            </Field>
            <div className="flex justify-end">
              <Button
                variant="secondary"
                busy={reset.isPending}
                disabled={newPassword.length < MIN_PASSWORD_LENGTH}
                onClick={() =>
                  reset.mutate(
                    { id: user.id, newPassword },
                    {
                      onSuccess: () => {
                        setNewPassword('');
                        reset.reset(); // the password was its variable
                        setNotice(t('users.passwordReset'));
                      },
                    },
                  )
                }
              >
                {t('users.resetPassword')}
              </Button>
            </div>
          </div>
        )}

        {!isSelf && user.twoFactorEnabled && (
          <div className="flex items-center justify-between gap-4 border-t border-border pt-4">
            <p className="text-sm text-muted">{t('users.twoFactorOn')}</p>
            <Button
              variant="secondary"
              busy={resetTwoFactor.isPending}
              onClick={() =>
                window.confirm(t('users.resetTwoFactorConfirm', { name: user.userName })) &&
                resetTwoFactor.mutate(user.id, { onSuccess: () => setNotice(t('users.twoFactorReset')) })
              }
            >
              {t('users.resetTwoFactor')}
            </Button>
          </div>
        )}

        {!isSelf && apiKeys > 0 && (
          <div className="flex items-center justify-between gap-4 border-t border-border pt-4">
            <p className="text-sm text-muted">{t('users.apiKeys', { count: apiKeys })}</p>
            <Button
              variant="secondary"
              busy={revokeKeys.isPending}
              onClick={() =>
                window.confirm(t('users.revokeApiKeysConfirm', { name: user.userName })) &&
                revokeKeys.mutate(user.id, {
                  onSuccess: (updated) => {
                    setApiKeys(updated.apiKeys);
                    setNotice(t('users.apiKeysRevoked'));
                  },
                })
              }
            >
              {t('users.revokeApiKeys')}
            </Button>
          </div>
        )}

        {!isSelf && sshKeys > 0 && (
          <div className="flex items-center justify-between gap-4 border-t border-border pt-4">
            <p className="text-sm text-muted">{t('users.sshKeys', { count: sshKeys })}</p>
            <Button
              variant="secondary"
              busy={deleteSshKeys.isPending}
              onClick={() =>
                window.confirm(t('users.deleteSshKeysConfirm', { name: user.userName })) &&
                deleteSshKeys.mutate(user.id, {
                  onSuccess: (updated) => {
                    setSshKeys(updated.sshKeys);
                    setNotice(t('users.sshKeysDeleted'));
                  },
                })
              }
            >
              {t('users.deleteSshKeys')}
            </Button>
          </div>
        )}

        <div className="flex justify-between gap-2 border-t border-border pt-4">
          {!isSelf ? (
            <Button
              variant="danger"
              busy={remove.isPending}
              onClick={() =>
                window.confirm(t('users.deleteConfirm', { name: user.userName })) &&
                remove.mutate(user.id, { onSuccess: onClose })
              }
            >
              {t('actions.delete')}
            </Button>
          ) : (
            <span />
          )}
          <Button variant="secondary" onClick={onClose}>
            {t('actions.close')}
          </Button>
        </div>
      </div>
    </Dialog>
  );
}

/**
 * Roles on one node (and its servers) or on one server: what the user has, and adding or taking one away. Applied at
 * once (the user's live pages follow); the list here is the panel's answer to each change.
 */
function AccessSection({ user, roles, onSaved }: { user: User; roles: Role[]; onSaved: () => void }) {
  const { t } = useTranslation();
  const change = useSetUserAccess();
  const nodes = useNodes();
  const servers = useServers();
  const [access, setAccess] = useState<Access[]>(user.access);
  const [scope, setScope] = useState<'node' | 'server'>('server');
  const [targetId, setTargetId] = useState('');
  const [roleId, setRoleId] = useState('');
  const targets =
    scope === 'node'
      ? (nodes.data ?? []).map((node) => ({ id: node.id, name: node.name }))
      : (servers.data ?? []).map((server) => ({ id: server.id, name: `${server.name} (${server.nodeName})` }));
  const scopedRoles = roles.filter((role) => role.scope === scope);

  const apply = (next: { scope: 'node' | 'server'; targetId: string; roleId: string; granted: boolean }) =>
    change.mutate(
      { id: user.id, ...next },
      {
        onSuccess: (updated) => {
          setAccess(updated.access);
          onSaved();
        },
      },
    );

  return (
    <section aria-labelledby="user-access" className="space-y-3 border-t border-border pt-4">
      <div>
        <h3 id="user-access" className="text-sm font-medium">
          {t('users.access.title')}
        </h3>
        <p className="text-xs text-muted">{t('users.access.hint')}</p>
      </div>
      {change.isError && <Alert>{errorMessage(t, change.error)}</Alert>}
      {access.length === 0 ? (
        <p className="text-sm text-muted">{t('users.access.empty')}</p>
      ) : (
        <ul className="space-y-1 text-sm">
          {access.map((granted) => (
            <li
              key={`${granted.scope}-${granted.targetId}-${granted.role.id}`}
              className="flex items-center justify-between gap-2"
            >
              <span className="break-words">
                {t('users.access.on', { role: granted.role.name, target: granted.targetName })}{' '}
                <span className="text-xs text-muted">({t(`roles.scopes.${granted.scope}`)})</span>
              </span>
              <Button
                variant="ghost"
                size="sm"
                busy={change.isPending}
                onClick={() =>
                  apply({
                    scope: granted.scope as 'node' | 'server',
                    targetId: granted.targetId,
                    roleId: granted.role.id,
                    granted: false,
                  })
                }
              >
                {t('users.access.remove')}
              </Button>
            </li>
          ))}
        </ul>
      )}
      <div className="grid gap-2 sm:grid-cols-3">
        <Field label={t('roles.scope')}>
          {(ids) => (
            <Select
              {...ids}
              value={scope}
              onChange={(e) => {
                setScope(e.target.value as 'node' | 'server');
                setTargetId('');
                setRoleId('');
              }}
            >
              <option value="server">{t('roles.scopes.server')}</option>
              <option value="node">{t('roles.scopes.node')}</option>
            </Select>
          )}
        </Field>
        <Field label={scope === 'node' ? t('users.access.node') : t('users.access.server')}>
          {(ids) => (
            <Select {...ids} value={targetId} onChange={(e) => setTargetId(e.target.value)}>
              <option value="">{t('users.access.choose')}</option>
              {targets.map((target) => (
                <option key={target.id} value={target.id}>
                  {target.name}
                </option>
              ))}
            </Select>
          )}
        </Field>
        <Field label={t('users.access.role')}>
          {(ids) => (
            <Select {...ids} value={roleId} onChange={(e) => setRoleId(e.target.value)}>
              <option value="">{t('users.access.choose')}</option>
              {scopedRoles.map((role) => (
                <option key={role.id} value={role.id}>
                  {role.name}
                </option>
              ))}
            </Select>
          )}
        </Field>
      </div>
      {scopedRoles.length === 0 && <p className="text-xs text-muted">{t('users.access.noRoles')}</p>}
      <div className="flex justify-end">
        <Button
          variant="secondary"
          busy={change.isPending}
          disabled={targetId === '' || roleId === ''}
          onClick={() => apply({ scope, targetId, roleId, granted: true })}
        >
          {t('users.access.add')}
        </Button>
      </div>
    </section>
  );
}
