import { zodResolver } from '@hookform/resolvers/zod';
import { useState } from 'react';
import { useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { z } from 'zod';
import { Button, Spinner } from '../../components/ui/Button';
import { Dialog } from '../../components/ui/Dialog';
import { Alert, Badge, PageHeader, Table, Td, Th } from '../../components/ui/Display';
import { Checkbox, Field, Input } from '../../components/ui/Form';
import { errorMessage, fieldErrors } from '../../lib/errors';
import { useFormatDate } from '../../lib/format';
import { can, Permission } from '../../lib/permissions';
import { useCurrentUser } from '../auth/api';
import { MIN_PASSWORD_LENGTH } from '../auth/SetupPage';
import {
  useCreateUser,
  useDeleteUser,
  useResetPassword,
  useResetTwoFactor,
  useRoles,
  useSetUserRoles,
  useUpdateUser,
  useUsers,
  type Role,
  type User,
} from './api';

export function UsersPage() {
  const { t } = useTranslation();
  const me = useCurrentUser();
  const users = useUsers();
  const formatDate = useFormatDate();
  const [editing, setEditing] = useState<User | 'new' | null>(null);
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
                  {user.roles.length > 0 ? (
                    user.roles.map((r) => r.name).join(', ')
                  ) : (
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
                    <Button variant="ghost" size="sm" onClick={() => setEditing(user)}>
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
      {editing && editing !== 'new' && (
        <EditUserDialog user={editing} isSelf={editing.id === me.id} onClose={() => setEditing(null)} />
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
  const [roleIds, setRoleIds] = useState<Set<string>>(new Set(user.roles.map((r) => r.id)));
  const [newPassword, setNewPassword] = useState('');
  const [notice, setNotice] = useState<string | null>(null);
  const { register, handleSubmit, formState } = useForm<ProfileValues>({
    resolver: zodResolver(profileSchema),
    defaultValues: { displayName: user.displayName ?? '', email: user.email ?? '', isDisabled: user.isDisabled },
  });
  const error = update.error ?? setRoles.error ?? reset.error ?? remove.error ?? resetTwoFactor.error;

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
