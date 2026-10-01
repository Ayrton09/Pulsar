import { zodResolver } from '@hookform/resolvers/zod';
import { useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { z } from 'zod';
import { Button } from '../../components/ui/Button';
import { Alert } from '../../components/ui/Display';
import { Field, Input } from '../../components/ui/Form';
import { errorMessage, fieldErrors } from '../../lib/errors';
import { useSetup } from './api';
import { AuthCard } from './AuthCard';

/** Mirrors the server's password policy (length only, NIST SP 800-63B); the server re-validates. */
export const MIN_PASSWORD_LENGTH = 12;

const schema = z
  .object({
    token: z.string().trim().min(1, 'validation.required'),
    userName: z.string().trim().min(1, 'validation.required').max(64, 'validation.tooLong'),
    displayName: z.string().trim().max(128, 'validation.tooLong'),
    email: z.union([z.literal(''), z.email('validation.email')]),
    password: z.string().min(MIN_PASSWORD_LENGTH, 'validation.passwordLength'),
    confirmPassword: z.string(),
  })
  .refine((values) => values.password === values.confirmPassword, {
    path: ['confirmPassword'],
    message: 'validation.passwordsDiffer',
  });

type Values = z.infer<typeof schema>;

export function SetupPage() {
  const { t } = useTranslation();
  const setup = useSetup();
  const { register, handleSubmit, formState } = useForm<Values>({
    resolver: zodResolver(schema),
    defaultValues: { token: '', userName: '', displayName: '', email: '', password: '', confirmPassword: '' },
  });
  const server = fieldErrors(setup.error);
  const message = (key: keyof Values) => {
    const error = formState.errors[key]?.message;
    return error ? t(error, { min: MIN_PASSWORD_LENGTH }) : server[key];
  };

  const onSubmit = handleSubmit(({ token, userName, displayName, email, password }) =>
    setup.mutate({ token, userName, password, displayName: displayName || null, email: email || null }),
  );

  return (
    <AuthCard title={t('auth.setup.title')} subtitle={t('auth.setup.subtitle')}>
      <form onSubmit={(e) => void onSubmit(e)} className="space-y-4" noValidate>
        {setup.isError && <Alert>{errorMessage(t, setup.error)}</Alert>}
        <Field label={t('auth.setup.token')} hint={t('auth.setup.tokenHint')} error={message('token')}>
          {(ids) => <Input {...ids} {...register('token')} autoComplete="off" spellCheck={false} autoFocus />}
        </Field>
        <Field label={t('auth.userName')} error={message('userName')}>
          {(ids) => <Input {...ids} {...register('userName')} autoComplete="username" />}
        </Field>
        <Field label={t('users.displayName')} error={message('displayName')}>
          {(ids) => <Input {...ids} {...register('displayName')} autoComplete="name" />}
        </Field>
        <Field label={t('users.email')} error={message('email')}>
          {(ids) => <Input {...ids} {...register('email')} type="email" autoComplete="email" />}
        </Field>
        <Field
          label={t('auth.password')}
          hint={t('auth.passwordHint', { min: MIN_PASSWORD_LENGTH })}
          error={message('password')}
        >
          {(ids) => <Input {...ids} {...register('password')} type="password" autoComplete="new-password" />}
        </Field>
        <Field label={t('auth.setup.confirmPassword')} error={message('confirmPassword')}>
          {(ids) => <Input {...ids} {...register('confirmPassword')} type="password" autoComplete="new-password" />}
        </Field>
        <Button type="submit" className="w-full" busy={setup.isPending}>
          {t('auth.setup.submit')}
        </Button>
      </form>
    </AuthCard>
  );
}
