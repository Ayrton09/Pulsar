import { zodResolver } from '@hookform/resolvers/zod';
import { useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { z } from 'zod';
import { Button } from '../../components/ui/Button';
import { Alert } from '../../components/ui/Display';
import { Field, Input } from '../../components/ui/Form';
import { errorMessage } from '../../lib/errors';
import { useLogin } from './api';
import { AuthCard } from './AuthCard';

const schema = z.object({
  userName: z.string().trim().min(1, 'validation.required'),
  password: z.string().min(1, 'validation.required'),
});

type Values = z.infer<typeof schema>;

export function LoginPage() {
  const { t } = useTranslation();
  const login = useLogin();
  const { register, handleSubmit, formState } = useForm<Values>({
    resolver: zodResolver(schema),
    defaultValues: { userName: '', password: '' },
  });

  // On success the session reloads and the PublicOnly guard redirects to the return address.
  const onSubmit = handleSubmit((values) => login.mutate(values));

  return (
    <AuthCard title={t('auth.login.title')} subtitle={t('auth.login.subtitle')}>
      <form onSubmit={(e) => void onSubmit(e)} className="space-y-4" noValidate>
        {login.isError && <Alert>{errorMessage(t, login.error)}</Alert>}
        <Field
          label={t('auth.userName')}
          error={formState.errors.userName && t(formState.errors.userName.message ?? '')}
        >
          {(ids) => <Input {...ids} {...register('userName')} autoComplete="username" autoFocus />}
        </Field>
        <Field
          label={t('auth.password')}
          error={formState.errors.password && t(formState.errors.password.message ?? '')}
        >
          {(ids) => <Input {...ids} {...register('password')} type="password" autoComplete="current-password" />}
        </Field>
        <Button type="submit" className="w-full" busy={login.isPending}>
          {t('auth.login.submit')}
        </Button>
      </form>
    </AuthCard>
  );
}
