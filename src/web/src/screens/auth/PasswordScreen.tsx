import { useState } from 'react';
import type { FormEvent } from 'react';
import { authApi } from '../../api/endpoints';
import type { Me } from '../../api/types';
import { useMe, useSession } from '../../auth/session';
import { FormErrorSummary, LiveRegion, TextField, fieldDomId, fieldErrorsOf } from '../../components/forms';
import type { SummaryItem } from '../../components/forms';
import { GateFrame } from '../../components/GateFrame';
import { InfoNote, ProblemAlert, SuccessNotice } from '../../components/states';
import { Button, PageHead, useDocumentTitle } from '../../components/ui';
import { useCommand } from '../../lib/useCommand';

const FORM_ID = 'parola';
const FIELDS = ['currentPassword', 'newPassword', 'confirmPassword'] as const;
type Field = (typeof FIELDS)[number];

const LABELS: Record<Field, string> = {
  currentPassword: 'Mevcut parola',
  newPassword: 'Yeni parola',
  confirmPassword: 'Yeni parola (tekrar)',
};

function PasswordForm({ userName, onChanged, submitLabel }: { userName: string; onChanged: () => void | Promise<void>; submitLabel: string }) {
  const [values, setValues] = useState<Record<Field, string>>({ currentPassword: '', newPassword: '', confirmPassword: '' });
  const [local, setLocal] = useState<Partial<Record<Field, string>>>({});
  const [attempt, setAttempt] = useState(0);
  const change = useCommand(
    (args: { currentPassword: string; newPassword: string }) => authApi.changePassword(args.currentPassword, args.newPassword),
    {
      onSuccess: async () => {
        setValues({ currentPassword: '', newPassword: '', confirmPassword: '' });
        await onChanged();
      },
      onProblem: () => setAttempt((n) => n + 1),
    },
  );

  const server = fieldErrorsOf(change.problem);
  const errors: Partial<Record<Field, string>> = { ...local };
  for (const field of FIELDS) {
    const message = server[field];
    if (message !== undefined && errors[field] === undefined) {
      errors[field] = message;
    }
  }

  const submit = (event: FormEvent) => {
    event.preventDefault();
    const next: Partial<Record<Field, string>> = {};
    if (values.currentPassword.length === 0) {
      next.currentPassword = 'Mevcut parolanızı yazın.';
    }

    if (values.newPassword.length === 0) {
      next.newPassword = 'Yeni parolanızı yazın.';
    } else if (values.newPassword === values.currentPassword) {
      next.newPassword = 'Yeni parola mevcut paroladan farklı olmalıdır.';
    }

    if (values.confirmPassword !== values.newPassword) {
      next.confirmPassword = 'İki yeni parola aynı değil.';
    }

    setLocal(next);
    change.clearProblem();
    if (Object.keys(next).length > 0) {
      setAttempt((n) => n + 1);
      return;
    }

    void change.run({ currentPassword: values.currentPassword, newPassword: values.newPassword });
  };

  const summary: SummaryItem[] = FIELDS.filter((field) => errors[field] !== undefined).map((field) => ({
    fieldId: fieldDomId(FORM_ID, field),
    message: `${LABELS[field]}: ${errors[field] ?? ''}`,
  }));
  const set = (field: Field) => (value: string) => {
    setValues((previous) => ({ ...previous, [field]: value }));
    if (local[field] !== undefined) {
      setLocal((previous) => {
        const rest = { ...previous };
        delete rest[field];
        return rest;
      });
    }
  };
  const unmapped = change.problem !== null && summary.length === 0;

  return (
    <form className="region gate__form" onSubmit={submit} noValidate aria-label="Parola değiştir">
      {summary.length > 0 && (
        <FormErrorSummary
          title={`Parola değiştirilmedi: ${summary.length} alanı düzeltin`}
          items={summary}
          focusKey={attempt}
          note="Parolanız değişmedi."
        />
      )}
      {unmapped && change.problem !== null && <ProblemAlert problem={change.problem} title="Parola değiştirilemedi" />}

      {/* Lets password managers associate the new password with the account. */}
      <input type="text" name="username" autoComplete="username" value={userName} readOnly hidden />
      <TextField
        formId={FORM_ID}
        name="currentPassword"
        label={LABELS.currentPassword}
        type="password"
        value={values.currentPassword}
        onChange={set('currentPassword')}
        autoComplete="current-password"
        required
        maxLength={128}
        error={errors.currentPassword}
      />
      <TextField
        formId={FORM_ID}
        name="newPassword"
        label={LABELS.newPassword}
        type="password"
        value={values.newPassword}
        onChange={set('newPassword')}
        autoComplete="new-password"
        required
        maxLength={128}
        error={errors.newPassword}
        hint="12–128 karakter; küçük harf, büyük harf, rakam ve simgeden en az üçü; kullanıcı adınızı içeremez."
      />
      <TextField
        formId={FORM_ID}
        name="confirmPassword"
        label={LABELS.confirmPassword}
        type="password"
        value={values.confirmPassword}
        onChange={set('confirmPassword')}
        autoComplete="new-password"
        required
        maxLength={128}
        error={errors.confirmPassword}
      />
      <div className="actions">
        <Button type="submit" variant="primary" pending={change.pending} pendingLabel="Parola değiştiriliyor…">
          {submitLabel}
        </Button>
      </div>
    </form>
  );
}

/** Forced change after a temporary password: nothing else in the product is reachable until it is done. */
export function PasswordScreen({ me }: { forced: true; me: Me }) {
  useDocumentTitle('Parolanızı değiştirin');
  const { refresh, logout } = useSession();
  return (
    <GateFrame
      tools={
        <button type="button" className="iconbtn" onClick={() => void logout()}>
          Çıkış yap
        </button>
      }
    >
      <h1>Parolanızı değiştirin</h1>
      <p className="pagehead__lead">
        <strong>{me.displayName}</strong>, hesabınız geçici bir parolayla açıldı. Devam etmeden önce yalnız sizin bildiğiniz yeni bir parola
        belirleyin.
      </p>
      <PasswordForm userName={me.userName} submitLabel="Yeni parolayı kaydet ve devam et" onChanged={refresh} />
    </GateFrame>
  );
}

/** Voluntary change from the user menu. */
export function ChangePasswordScreen() {
  const me = useMe();
  const [done, setDone] = useState(false);
  return (
    <div className="view">
      <PageHead title="Parolayı değiştir" lead="Parolanız değiştiğinde diğer cihaz ve tarayıcılardaki oturumlarınız kapatılır; bu oturum açık kalır." />
      <LiveRegion>
        {done && (
          <SuccessNotice title="Parolanız değiştirildi">
            <p>Diğer oturumlarınız kapatıldı. Bir sonraki girişinizde yeni parolanızı kullanın.</p>
          </SuccessNotice>
        )}
      </LiveRegion>
      <div className="measure-form">
        <PasswordForm userName={me.userName} submitLabel="Parolayı değiştir" onChanged={() => setDone(true)} />
      </div>
      {!done && (
        <div className="measure">
          <InfoNote title="Parolanızı unuttuysanız">
            <p>Mevcut parolayı bilmeden buradan değiştiremezsiniz. Sistem yöneticiniz geçici bir parola üretebilir.</p>
          </InfoNote>
        </div>
      )}
    </div>
  );
}
