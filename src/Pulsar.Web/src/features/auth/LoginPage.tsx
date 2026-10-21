import { zodResolver } from '@hookform/resolvers/zod';
import { useState, type FormEvent } from 'react';
import { useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { z } from 'zod';
import { Button } from '../../components/ui/Button';
import { Alert } from '../../components/ui/Display';
import { Field, Input } from '../../components/ui/Form';
import { errorMessage } from '../../lib/errors';
import { useLogin, useLoginTwoFactor } from './api';
import { AuthCard } from './AuthCard';

const schema = z.object({
  userName: z.string().trim().min(1, 'validation.required'),
  password: z.string().min(1, 'validation.required'),
});

type Values = z.infer<typeof schema>;

export function LoginPage() {
  const login = useLogin();
  const [secondStep, setSecondStep] = useState(false);
  if (secondStep) {
    return <SecondFactor onBack={() => setSecondStep(false)} />;
  }
  return (
    <PasswordStep
      login={login}
      onSecondStep={() => {
        // The password is not kept while the code is asked for: the mutation forgets what it sent.
        login.reset();
        setSecondStep(true);
      }}
    />
  );
}

function PasswordStep({ login, onSecondStep }: { login: ReturnType<typeof useLogin>; onSecondStep: () => void }) {
  const { t } = useTranslation();
  const { register, handleSubmit, formState } = useForm<Values>({
    resolver: zodResolver(schema),
    defaultValues: { userName: '', password: '' },
  });

  // On success the session reloads and the PublicOnly guard redirects to the return address.
  const onSubmit = handleSubmit((values) =>
    login.mutate(values, {
      onSuccess: (answer) => {
        if (answer.twoFactorRequired) {
          onSecondStep();
        }
      },
    }),
  );

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

/** After the right password: a code from the authenticator app, or a recovery code. */
function SecondFactor({ onBack }: { onBack: () => void }) {
  const { t } = useTranslation();
  const verify = useLoginTwoFactor();
  const [recovery, setRecovery] = useState(false);
  const [value, setValue] = useState('');

  // On success the session reloads and the PublicOnly guard redirects, as after a password.
  const submit = (event: FormEvent) => {
    event.preventDefault();
    verify.mutate(
      recovery ? { code: null, recoveryCode: value.trim() } : { code: value.replace(/[\s-]/g, ''), recoveryCode: null },
    );
  };
  const switchMethod = () => {
    setRecovery(!recovery);
    setValue('');
    verify.reset();
  };

  return (
    <AuthCard
      title={t('twoFactor.loginTitle')}
      subtitle={recovery ? t('twoFactor.loginRecovery') : t('twoFactor.loginApp')}
    >
      <form onSubmit={submit} className="space-y-4" noValidate>
        {verify.isError && <Alert>{errorMessage(t, verify.error)}</Alert>}
        <Field label={recovery ? t('twoFactor.recoveryCode') : t('twoFactor.code')}>
          {(ids) => (
            <Input
              {...ids}
              key={recovery ? 'recovery' : 'code'}
              value={value}
              onChange={(e) => setValue(e.target.value)}
              autoComplete="one-time-code"
              inputMode={recovery ? 'text' : 'numeric'}
              maxLength={recovery ? 32 : 9}
              autoFocus
            />
          )}
        </Field>
        <Button type="submit" className="w-full" busy={verify.isPending} disabled={value.trim().length === 0}>
          {t('twoFactor.verify')}
        </Button>
        <Button variant="ghost" size="sm" className="w-full" onClick={switchMethod}>
          {recovery ? t('twoFactor.useApp') : t('twoFactor.useRecovery')}
        </Button>
        <Button variant="ghost" size="sm" className="w-full" onClick={onBack}>
          {t('twoFactor.otherAccount')}
        </Button>
      </form>
    </AuthCard>
  );
}
