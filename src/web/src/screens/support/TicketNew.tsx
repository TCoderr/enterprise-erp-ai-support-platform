import { useState } from 'react';
import type { FormEvent } from 'react';
import { supportApi } from '../../api/endpoints';
import type { TicketInput } from '../../api/endpoints';
import type { AppModule, RelatedDocumentType, TicketPriority } from '../../api/types';
import { KNOWN_SCREENS } from '../../api/types';
import { FormErrorSummary, SelectField, TextAreaField, TextField, fieldDomId, fieldErrorsOf } from '../../components/forms';
import type { SummaryItem } from '../../components/forms';
import { ProblemAlert } from '../../components/states';
import { Button, Code, PageHead } from '../../components/ui';
import { MODULE_LABELS, PRIORITY_LABELS, RELATED_DOCUMENT_LABELS, SCREEN_LABELS } from '../../lib/labels';
import { useInvalidate, useSetCached } from '../../lib/query';
import { useCommand } from '../../lib/useCommand';
import { useGo } from '../../shell/context';

const FORM_ID = 'talep';
const PRIORITIES: readonly TicketPriority[] = ['Low', 'Normal', 'High', 'Urgent'];
const MODULES: readonly AppModule[] = ['General', 'Sales', 'Purchasing', 'Inventory', 'Support'];
const DOCUMENT_TYPES: readonly RelatedDocumentType[] = ['SalesOrder', 'PurchaseOrder', 'Shipment', 'GoodsReceipt', 'StockTransfer', 'StockAdjustment'];
const ERROR_CODE = /^[A-Z]{3}-\d{3}$/;
const DOCUMENT_NUMBER = /^[A-Z0-9\-_/.]{4,16}$/;

const LABELS: Record<string, string> = {
  title: 'Başlık',
  description: 'Açıklama',
  priority: 'Öncelik',
  module: 'Modül',
  screen: 'Ekran',
  errorCode: 'Hata kodu',
  relatedDocument: 'İlişkili belge',
};

export function TicketNewScreen() {
  const go = useGo();
  const invalidate = useInvalidate();
  const setCached = useSetCached();
  const [title, setTitle] = useState('');
  const [description, setDescription] = useState('');
  const [priority, setPriority] = useState<string>('Normal');
  const [module, setModule] = useState<string>('General');
  const [screen, setScreen] = useState('');
  const [errorCode, setErrorCode] = useState('');
  const [documentType, setDocumentType] = useState('');
  const [documentNumber, setDocumentNumber] = useState('');
  const [local, setLocal] = useState<Record<string, string>>({});
  const [attempt, setAttempt] = useState(0);

  const create = useCommand((input: TicketInput, key: string) => supportApi.createTicket(input, key), {
    onSuccess: (ticket) => {
      setCached(['ticket', ticket.id], ticket);
      void invalidate('tickets');
      go(`/destek/talepler/${ticket.id}`, { replace: true });
    },
    onProblem: () => setAttempt((n) => n + 1),
  });

  const server = fieldErrorsOf(create.problem);
  const errorOf = (key: string): string | undefined => local[key] ?? server[key];

  const submit = (event: FormEvent) => {
    event.preventDefault();
    const next: Record<string, string> = {};
    const cleanTitle = title.trim();
    const cleanDescription = description.trim();
    if (cleanTitle.length < 5 || cleanTitle.length > 160) {
      next.title = 'Başlık 5–160 karakter olmalıdır.';
    }

    if (cleanDescription.length < 10 || cleanDescription.length > 4000) {
      next.description = 'Açıklama 10–4000 karakter olmalıdır.';
    }

    const code = errorCode.trim().toLocaleUpperCase('en-US');
    if (code.length > 0 && !ERROR_CODE.test(code)) {
      next.errorCode = 'Hata kodu AAA-000 biçiminde olmalıdır (örn. STK-409).';
    }

    const number = documentNumber.trim().toLocaleUpperCase('en-US');
    const type = DOCUMENT_TYPES.find((t) => t === documentType);
    if ((type === undefined) !== (number.length === 0)) {
      next.relatedDocument = 'Belge türü ve numarası birlikte girilmelidir; ikisini de boş bırakabilirsiniz.';
    } else if (type !== undefined && !DOCUMENT_NUMBER.test(number)) {
      next.relatedDocument = 'Belge numarası 4–16 karakter olmalıdır (örn. SO-00001042).';
    }

    setLocal(next);
    create.clearProblem();
    if (Object.keys(next).length > 0) {
      setAttempt((n) => n + 1);
      return;
    }

    void create.run({
      title: cleanTitle,
      description: cleanDescription,
      priority: PRIORITIES.find((p) => p === priority) ?? 'Normal',
      module: MODULES.find((m) => m === module) ?? 'General',
      screen: screen.length > 0 ? screen : undefined,
      errorCode: code.length > 0 ? code : undefined,
      relatedDocument: type !== undefined ? { type, number } : undefined,
    });
  };

  const summary: SummaryItem[] = [];
  for (const key of Object.keys(LABELS)) {
    const message = errorOf(key);
    if (message !== undefined) {
      summary.push({ fieldId: fieldDomId(FORM_ID, key === 'relatedDocument' ? 'relatedDocumentType' : key), message: `${LABELS[key] ?? key}: ${message}` });
    }
  }

  const problem = create.problem;
  return (
    <div className="view">
      <PageHead
        title="Yeni destek talebi"
        lead="Uygulamanın kullanımıyla ilgili soru ve sorunlar için. Talebe tanılama bilgisi (modül, ekran, hata kodu, rol adlarınız, sunucu zamanı) sunucu tarafından eklenir; siparişlerinizin içeriği eklenmez."
      />
      <form className="region region--pad stack" onSubmit={submit} noValidate aria-label="Yeni destek talebi">
        {summary.length > 0 && (
          <FormErrorSummary
            title={`Talep açılmadı: ${summary.length} alanı düzeltin`}
            items={summary}
            focusKey={attempt}
            note={problem !== null ? `${problem.title}. Talep kaydedilmedi.` : 'Talep kaydedilmedi.'}
          />
        )}
        {problem !== null && summary.length === 0 && (
          <ProblemAlert
            problem={problem}
            title="Talep açılamadı"
            onRetry={problem.isRetryable ? () => document.getElementById('talep-gonder')?.click() : undefined}
          />
        )}

        <div className="formgrid">
          <TextField
            formId={FORM_ID}
            name="title"
            label="Başlık"
            value={title}
            onChange={setTitle}
            required
            maxLength={160}
            fieldClassName="field--full"
            placeholder="Örn. Sevkiyat kaydederken STK-409 hatası alıyorum…"
            error={errorOf('title')}
          />
          <TextAreaField
            formId={FORM_ID}
            name="description"
            label="Açıklama"
            value={description}
            onChange={setDescription}
            required
            rows={6}
            maxLength={4000}
            counter
            fieldClassName="field--full"
            error={errorOf('description')}
            hint="Ne yapmak istediniz, ne oldu, ne bekliyordunuz? Düz metin; parola ya da müşteri kişisel verisi yazmayın."
          />
          <SelectField
            formId={FORM_ID}
            name="priority"
            label="Öncelik"
            value={priority}
            onChange={setPriority}
            options={PRIORITIES.map((p) => ({ value: p, label: PRIORITY_LABELS[p] }))}
            error={errorOf('priority')}
          />
          <SelectField
            formId={FORM_ID}
            name="module"
            label="Modül"
            value={module}
            onChange={setModule}
            options={MODULES.map((m) => ({ value: m, label: MODULE_LABELS[m] }))}
            error={errorOf('module')}
          />
          <SelectField
            formId={FORM_ID}
            name="screen"
            label="Ekran (isteğe bağlı)"
            value={screen}
            onChange={setScreen}
            placeholder="Belirtilmedi"
            options={KNOWN_SCREENS.map((s) => ({ value: s, label: SCREEN_LABELS[s] ?? s }))}
            error={errorOf('screen')}
          />
          <TextField
            formId={FORM_ID}
            name="errorCode"
            label="Hata kodu (isteğe bağlı)"
            value={errorCode}
            onChange={setErrorCode}
            inputClassName="code"
            spellCheck={false}
            autoCapitalize="characters"
            maxLength={7}
            translate="no"
            placeholder="STK-409"
            error={errorOf('errorCode')}
            hint="Hata iletisinde gösterilen kısa kod: üç harf, tire, üç rakam."
          />
        </div>

        <fieldset className="stack">
          <legend className="legend">İlişkili belge (isteğe bağlı)</legend>
          <div className="formgrid">
            <SelectField
              formId={FORM_ID}
              name="relatedDocumentType"
              label="Belge türü"
              value={documentType}
              onChange={setDocumentType}
              placeholder="Belirtilmedi"
              options={DOCUMENT_TYPES.map((t) => ({ value: t, label: RELATED_DOCUMENT_LABELS[t] }))}
              error={errorOf('relatedDocument')}
            />
            <TextField
              formId={FORM_ID}
              name="relatedDocumentNumber"
              label="Belge numarası"
              value={documentNumber}
              onChange={setDocumentNumber}
              inputClassName="code"
              spellCheck={false}
              autoCapitalize="characters"
              maxLength={16}
              translate="no"
              placeholder="SO-00001042"
            />
          </div>
          <p className="small">
            Bu bir <strong>etikettir, bağlantı değildir</strong>: yalnız tür ve numara metni saklanır (örn. <Code>SO-00001042</Code>). Sunucu belgenin
            varlığını denetlemez ve talep, destek personeline o belgeyi görme yetkisi vermez; belge kendi yetkisiyle açılır.
          </p>
        </fieldset>

        <div className="actions actions--sticky">
          <Button onClick={() => go('/destek/talepler')}>Vazgeç</Button>
          <span className="spacer" />
          <Button id="talep-gonder" type="submit" variant="primary" pending={create.pending} pendingLabel="Talep açılıyor…">
            Talebi aç
          </Button>
        </div>
      </form>
    </div>
  );
}
