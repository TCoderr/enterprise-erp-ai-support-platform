import { useState } from 'react';
import type { FormEvent } from 'react';
import { GateFrame } from '../../components/GateFrame';
import type { ApiProblem } from '../../api/problem';
import { asProblem, isAbort } from '../../api/problem';
import type { AnonymousNotice } from '../../auth/session';
import { useSession } from '../../auth/session';
import { FormErrorSummary, TextField, fieldDomId } from '../../components/forms';
import type { SummaryItem } from '../../components/forms';
import { InfoNote, ProblemAlert } from '../../components/states';
import { Button, useDocumentTitle } from '../../components/ui';

const FORM_ID = 'giris';

const NOTICES: Record<AnonymousNotice, { title: string; text: string; tone: 'info' | 'warn' }> = {
  expired: {
    title: 'Oturumunuz sona erdi',
    text: 'Güvenlik için ekrandaki veriler temizlendi. Yeniden giriş yaptığınızda kaldığınız sayfaya dönersiniz.',
    tone: 'info',
  },
  'signed-out': { title: 'Çıkış yapıldı', text: 'Bu bilgisayarı başka biri kullanacaksa tarayıcı penceresini kapatabilirsiniz.', tone: 'info' },
  'logout-unconfirmed': {
    title: 'Çıkış sunucuda doğrulanamadı',
    text: 'Ekrandaki veriler temizlendi, ancak çıkış isteği sunucuya ulaşmadı; oturum sunucuda açık kalmış olabilir. Bağlantı gelince yeniden giriş yapıp çıkışı tekrarlayın veya tarayıcıyı kapatın.',
    tone: 'warn',
  },
};

export function LoginScreen({ notice }: { notice?: AnonymousNotice | undefined }) {
  useDocumentTitle('Giriş');
  const { login } = useSession();
  const [userName, setUserName] = useState('');
  const [password, setPassword] = useState('');
  const [pending, setPending] = useState(false);
  const [missing, setMissing] = useState<SummaryItem[]>([]);
  const [problem, setProblem] = useState<ApiProblem | null>(null);
  const [attempt, setAttempt] = useState(0);

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (pending) {
      return;
    }

    const items: SummaryItem[] = [];
    if (userName.trim().length === 0) {
      items.push({ fieldId: fieldDomId(FORM_ID, 'userName'), message: 'Kullanıcı adınızı yazın.' });
    }

    if (password.length === 0) {
      items.push({ fieldId: fieldDomId(FORM_ID, 'password'), message: 'Parolanızı yazın.' });
    }

    setMissing(items);
    setProblem(null);
    if (items.length > 0) {
      setAttempt((n) => n + 1);
      return;
    }

    setPending(true);
    try {
      await login(userName.trim(), password);
    } catch (error) {
      if (!isAbort(error)) {
        setProblem(asProblem(error));
        setAttempt((n) => n + 1);
      }
    } finally {
      setPending(false);
    }
  };

  const rejected = problem !== null && problem.status === 401;
  const shownNotice = notice !== undefined && problem === null && missing.length === 0 ? NOTICES[notice] : undefined;

  return (
    <GateFrame>
      <h1>Giriş</h1>
      <p className="pagehead__lead">Sipariş, satın alma, stok ve destek işlemleri için hesabınızla giriş yapın.</p>

      <form className="region gate__form" onSubmit={(event) => void submit(event)} noValidate aria-label="Giriş">
        {shownNotice !== undefined && (
          <InfoNote title={shownNotice.title} tone={shownNotice.tone}>
            <p>{shownNotice.text}</p>
          </InfoNote>
        )}
        {missing.length > 0 && <FormErrorSummary title="Giriş yapılamadı: eksik alanları doldurun" items={missing} focusKey={attempt} />}
        {rejected && (
          <FormErrorSummary
            title="Giriş yapılamadı"
            focusKey={attempt}
            items={[{ fieldId: fieldDomId(FORM_ID, 'userName'), message: problem.detail ?? 'Kullanıcı adı veya parola hatalı.' }]}
            note="Art arda 5 hatalı denemeden sonra hesap 15 dakika kilitlenir. Parolanızı bilmiyorsanız sistem yöneticinizden sıfırlamasını isteyin."
          />
        )}
        {problem !== null && !rejected && (
          <ProblemAlert problem={problem} title="Giriş yapılamadı" hideFieldErrors={false} />
        )}

        <TextField
          formId={FORM_ID}
          name="userName"
          label="Kullanıcı adı"
          value={userName}
          onChange={setUserName}
          autoComplete="username"
          autoCapitalize="none"
          spellCheck={false}
          required
          maxLength={64}
        />
        <TextField
          formId={FORM_ID}
          name="password"
          label="Parola"
          type="password"
          value={password}
          onChange={setPassword}
          autoComplete="current-password"
          required
          maxLength={128}
        />
        <div className="actions">
          <Button type="submit" variant="primary" pending={pending} pendingLabel="Giriş yapılıyor…">
            Giriş yap
          </Button>
        </div>
      </form>
    </GateFrame>
  );
}
