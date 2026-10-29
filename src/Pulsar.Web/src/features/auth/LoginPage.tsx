import { zodResolver } from '@hookform/resolvers/zod';
import { useState, type FormEvent } from 'react';
import { useSearchParams } from 'react-router';
import { useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { z } from 'zod';
import { Button } from '../../components/ui/Button';
import { Alert } from '../../components/ui/Display';
import { Checkbox, Field, Input } from '../../components/ui/Form';
import { errorMessage } from '../../lib/errors';
import { ssoName, useSsoOptions, useStartSsoLogin } from '../sso/api';
import { useLogin, useLoginTwoFactor } from './api';
import { AuthCard } from './AuthCard';

const schema = z.object({
  userName: z.string().trim().min(1, 'validation.required'),
  password: z.string().min(1, 'validation.required'),
  rememberMe: z.boolean(),
});

type Values = z.infer<typeof schema>;

export function LoginPage() {
  const login = useLogin();
  const [search] = useSearchParams();
  // The second step asks for the code only: whether to remember the device was chosen with the password. A sign-in
  // with a provider comes back here for it, not remembered.
  const [secondStep, setSecondStep] = useState<{ rememberMe: boolean } | null>(
    search.get('sso') === 'two_factor' ? { rememberMe: false } : null,
  );
  if (secondStep) {
    return <SecondFactor rememberMe={secondStep.rememberMe} onBack={() => setSecondStep(null)} />;
  }
  return (
    <PasswordStep
      login={login}
      onSecondStep={(rememberMe) => {
        // The password is not kept while the code is asked for: the mutation forgets what it sent.
        login.reset();
        setSecondStep({ rememberMe });
      }}
    />
  );
}

function PasswordStep({
  login,
  onSecondStep,
}: {
  login: ReturnType<typeof useLogin>;
  onSecondStep: (rememberMe: boolean) => void;
}) {
  const { t } = useTranslation();
  const { register, handleSubmit, formState } = useForm<Values>({
    resolver: zodResolver(schema),
    defaultValues: { userName: '', password: '', rememberMe: false },
  });

  // On success the session reloads and the PublicOnly guard redirects to the return address.
  const onSubmit = handleSubmit((values) =>
    login.mutate(values, {
      onSuccess: (answer) => {
        if (answer.twoFactorRequired) {
          onSecondStep(values.rememberMe);
        }
      },
    }),
  );

  return (
    <AuthCard title={t('auth.login.title')} subtitle={t('auth.login.subtitle')}>
      <form onSubmit={(e) => void onSubmit(e)} className="space-y-4" noValidate>
        <SsoFailure />
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
        <Checkbox {...register('rememberMe')} label={t('auth.login.remember')} />
        <Button type="submit" className="w-full" busy={login.isPending}>
          {t('auth.login.submit')}
        </Button>
      </form>
      <SsoButtons />
    </AuthCard>
  );
}

/** Why a sign-in with a provider did not end in the panel (a code, never what the provider said). */
function SsoFailure() {
  const { t } = useTranslation();
  const [search] = useSearchParams();
  const failed = search.get('sso_error');
  return failed ? <Alert>{t(`sso.errors.${failed}`, { defaultValue: t('sso.errors.unknown') })}</Alert> : null;
}

/** The providers the administrator turned on: each signs in an account its owner linked to it. */
function SsoButtons() {
  const { t } = useTranslation();
  const options = useSsoOptions();
  const start = useStartSsoLogin();
  if (!options.data?.length) {
    return null;
  }

  return (
    <div className="mt-6 space-y-3">
      <div className="flex items-center gap-3 text-xs text-muted">
        <span className="h-px flex-1 bg-border" />
        {t('sso.or')}
        <span className="h-px flex-1 bg-border" />
      </div>
      {start.isError && <Alert>{errorMessage(t, start.error)}</Alert>}
      {options.data.map((option) => (
        <Button
          key={option.kind}
          variant="secondary"
          className="w-full"
          busy={(start.isPending || start.isSuccess) && start.variables === option.kind}
          onClick={() => start.mutate(option.kind)}
        >
          {t('sso.signInWith', { name: ssoName(option.kind, option.name) })}
        </Button>
      ))}
    </div>
  );
}

/** After the right password: a code from the authenticator app, or a recovery code. */
function SecondFactor({ rememberMe, onBack }: { rememberMe: boolean; onBack: () => void }) {
  const { t } = useTranslation();
  const verify = useLoginTwoFactor();
  const [recovery, setRecovery] = useState(false);
  const [value, setValue] = useState('');

  // On success the session reloads and the PublicOnly guard redirects, as after a password.
  const submit = (event: FormEvent) => {
    event.preventDefault();
    verify.mutate(
      recovery
        ? { code: null, recoveryCode: value.trim(), rememberMe }
        : { code: value.replace(/[\s-]/g, ''), recoveryCode: null, rememberMe },
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
