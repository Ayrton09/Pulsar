import { zodResolver } from '@hookform/resolvers/zod';
import { useState } from 'react';
import { useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { z } from 'zod';
import { Button } from '../../components/ui/Button';
import { Alert, Card, PageHeader } from '../../components/ui/Display';
import { Field, Input } from '../../components/ui/Form';
import { errorMessage, fieldErrors } from '../../lib/errors';
import { useChangePassword, useCurrentUser, useUpdateProfile } from './api';
import { MIN_PASSWORD_LENGTH } from './SetupPage';
import { TwoFactorSection } from './TwoFactor';

const profileSchema = z.object({
  displayName: z.string().trim().max(128, 'validation.tooLong'),
  email: z.union([z.literal(''), z.email('validation.email')]),
});

const passwordSchema = z
  .object({
    currentPassword: z.string().min(1, 'validation.required'),
    newPassword: z.string().min(MIN_PASSWORD_LENGTH, 'validation.passwordLength'),
    confirmPassword: z.string(),
  })
  .refine((v) => v.newPassword === v.confirmPassword, {
    path: ['confirmPassword'],
    message: 'validation.passwordsDiffer',
  });

/** The signed-in user's own profile and password. */
export function AccountPage() {
  const { t } = useTranslation();
  return (
    <>
      <PageHeader title={t('account.title')} description={t('account.description')} />
      <div className="grid max-w-3xl gap-6">
        <ProfileForm />
        <PasswordForm />
        <TwoFactorSection />
      </div>
    </>
  );
}

function ProfileForm() {
  const { t } = useTranslation();
  const user = useCurrentUser();
  const update = useUpdateProfile();
  const [saved, setSaved] = useState(false);
  const { register, handleSubmit, formState } = useForm<z.infer<typeof profileSchema>>({
    resolver: zodResolver(profileSchema),
    defaultValues: { displayName: user.displayName ?? '', email: user.email ?? '' },
  });

  const onSubmit = handleSubmit(({ displayName, email }) => {
    setSaved(false);
    update.mutate({ displayName: displayName || null, email: email || null }, { onSuccess: () => setSaved(true) });
  });

  return (
    <Card className="p-6">
      <h2 className="mb-4 text-lg font-semibold">{t('account.profile')}</h2>
      <form onSubmit={(e) => void onSubmit(e)} className="space-y-4" noValidate>
        {update.isError && <Alert>{errorMessage(t, update.error)}</Alert>}
        {saved && (
          <p role="status" className="text-sm text-success">
            {t('users.saved')}
          </p>
        )}
        <Field label={t('auth.userName')} hint={t('account.userNameFixed')}>
          {(ids) => <Input {...ids} value={user.userName} readOnly disabled />}
        </Field>
        <Field
          label={t('users.displayName')}
          error={formState.errors.displayName && t(formState.errors.displayName.message ?? '')}
        >
          {(ids) => <Input {...ids} {...register('displayName')} autoComplete="name" />}
        </Field>
        <Field
          label={t('users.email')}
          hint={t('account.emailHint')}
          error={formState.errors.email && t(formState.errors.email.message ?? '')}
        >
          {(ids) => <Input {...ids} {...register('email')} type="email" autoComplete="email" />}
        </Field>
        <div className="flex justify-end">
          <Button type="submit" variant="secondary" busy={update.isPending}>
            {t('users.saveProfile')}
          </Button>
        </div>
      </form>
    </Card>
  );
}

function PasswordForm() {
  const { t } = useTranslation();
  const change = useChangePassword();
  const [changed, setChanged] = useState(false);
  const { register, handleSubmit, formState, reset } = useForm<z.infer<typeof passwordSchema>>({
    resolver: zodResolver(passwordSchema),
    defaultValues: { currentPassword: '', newPassword: '', confirmPassword: '' },
  });
  const server = fieldErrors(change.error);
  const message = (key: 'currentPassword' | 'newPassword' | 'confirmPassword') => {
    const error = formState.errors[key]?.message;
    return error ? t(error, { min: MIN_PASSWORD_LENGTH }) : key === 'newPassword' ? server['password'] : undefined;
  };

  const onSubmit = handleSubmit(({ currentPassword, newPassword }) => {
    setChanged(false);
    change.mutate(
      { currentPassword, newPassword },
      {
        onSuccess: () => {
          reset();
          setChanged(true);
        },
      },
    );
  });

  return (
    <Card className="p-6">
      <h2 className="mb-4 text-lg font-semibold">{t('account.password')}</h2>
      <form onSubmit={(e) => void onSubmit(e)} className="space-y-4" noValidate>
        {change.isError && <Alert>{errorMessage(t, change.error)}</Alert>}
        {changed && (
          <p role="status" className="text-sm text-success">
            {t('account.passwordChanged')}
          </p>
        )}
        <Field label={t('account.currentPassword')} error={message('currentPassword')}>
          {(ids) => <Input {...ids} {...register('currentPassword')} type="password" autoComplete="current-password" />}
        </Field>
        <Field
          label={t('users.newPassword')}
          hint={t('auth.passwordHint', { min: MIN_PASSWORD_LENGTH })}
          error={message('newPassword')}
        >
          {(ids) => <Input {...ids} {...register('newPassword')} type="password" autoComplete="new-password" />}
        </Field>
        <Field label={t('auth.setup.confirmPassword')} error={message('confirmPassword')}>
          {(ids) => <Input {...ids} {...register('confirmPassword')} type="password" autoComplete="new-password" />}
        </Field>
        <div className="flex justify-end">
          <Button type="submit" variant="secondary" busy={change.isPending}>
            {t('account.changePassword')}
          </Button>
        </div>
      </form>
    </Card>
  );
}
