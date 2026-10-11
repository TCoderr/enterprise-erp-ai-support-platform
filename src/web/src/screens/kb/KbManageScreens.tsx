import { useState } from 'react';
import type { FormEvent } from 'react';
import { useParams } from 'react-router-dom';
import { kbApi, masterApi } from '../../api/endpoints';
import type { KbDocumentInput } from '../../api/endpoints';
import type { AppModule, KbDocument, KbVersion, KbVisibility } from '../../api/types';
import { abilitiesOf } from '../../auth/permissions';
import { useMe } from '../../auth/session';
import { DataTable } from '../../components/DataTable';
import type { Column } from '../../components/DataTable';
import { ConfirmStep, FormErrorSummary, LiveRegion, SelectField, TextAreaField, TextField, fieldDomId, fieldErrorsOf, useDisclosure } from '../../components/forms';
import type { SummaryItem } from '../../components/forms';
import { EmptyState, InfoNote, ProblemAlert, QueryView, StaleAlert, SuccessNotice } from '../../components/states';
import { Button, Code, Meta, PageHead, StatusTag, Tag } from '../../components/ui';
import { formatDateTime } from '../../lib/format';
import { KB_STATUS, MODULE_LABELS, VISIBILITY_LABELS, labelOf, lookOf } from '../../lib/labels';
import { useUrlState } from '../../lib/lists';
import { useApiQuery, useInvalidate, useSetCached } from '../../lib/query';
import { SafeMarkdown } from '../../lib/safeMarkdown';
import { useCommand } from '../../lib/useCommand';
import { KLink, useGo } from '../../shell/context';

const MODULES: readonly AppModule[] = ['General', 'Sales', 'Purchasing', 'Inventory', 'Support'];
const VISIBILITIES: readonly KbVisibility[] = ['General', 'SupportOnly', 'ManagerOnly'];
const SLUG = /^[a-z0-9]+(-[a-z0-9]+)*$/;
const ERROR_CODE = /^[A-Z]{3}-\d{3}$/;
const MAX_BODY = 20_000;
const MARKDOWN_HINT =
  'Markdown: # başlık, paragraf, - liste, 1. sıralı liste, **kalın**, *italik*, `kod`, ``` kod bloğu. Ham HTML ve bağlantılar etkinleştirilmez; düz metin olarak gösterilir. Her başlık bir bölümdür ve Kaynaklı Yardım atıfları bölüm numarasıyla yapılır.';

function validateContent(title: string, body: string): Record<string, string> {
  const errors: Record<string, string> = {};
  if (title.trim().length < 3 || title.trim().length > 160) {
    errors.title = 'Başlık 3–160 karakter olmalıdır.';
  }

  if (body.trim().length < 20 || body.trim().length > MAX_BODY) {
    errors.bodyMarkdown = `Metin 20–${MAX_BODY} karakter olmalıdır.`;
  }

  return errors;
}

// ---------------------------------------------------------------- list
export function KbManageListScreen() {
  const query = useApiQuery(['kb-documents'], (signal) => kbApi.documents(signal));
  const columns: Column<KbDocument>[] = [
    {
      id: 'slug',
      header: 'Kısa ad',
      kind: 'code',
      rowHeader: true,
      cell: (d) => (
        <KLink to={`/bilgi-bankasi/yonetim/${d.id}`} translate="no">
          {d.slug}
        </KLink>
      ),
    },
    { id: 'title', header: 'Son sürümün başlığı', kind: 'name', cell: (d) => d.versions.find((v) => v.versionNo === d.latestVersionNo)?.title ?? '—' },
    { id: 'status', header: 'Durum', cell: (d) => <StatusTag look={lookOf(KB_STATUS, d.status)} /> },
    { id: 'visibility', header: 'Görünürlük', cell: (d) => labelOf(VISIBILITY_LABELS, d.visibility) },
    { id: 'module', header: 'Modül', cell: (d) => labelOf(MODULE_LABELS, d.module) },
    { id: 'published', header: 'Yayınlı sürüm', kind: 'num', cell: (d) => d.publishedVersionNo ?? '—' },
    { id: 'latest', header: 'Son sürüm', kind: 'num', cell: (d) => d.latestVersionNo },
    { id: 'updated', header: 'Güncelleme', kind: 'date', cell: (d) => formatDateTime(d.updatedAt) },
  ];

  return (
    <div className="view">
      <PageHead
        title="Belge yönetimi"
        lead="Tüm durumlardaki bilgi bankası belgeleri ve sürümleri. Taslak ve emekli belgeler okuyuculara, aramaya ve Kaynaklı Yardım’a hiçbir koşulda girmez."
        actions={
          <KLink className="btn btn--primary" to="/bilgi-bankasi/yonetim/yeni">
            Yeni belge
          </KLink>
        }
      />
      <QueryView query={query} loadingLabel="Belgeler yükleniyor…">
        {(documents) =>
          documents.length === 0 ? (
            <EmptyState
              title="Henüz bilgi bankası belgesi yok"
              action={
                <KLink className="btn" to="/bilgi-bankasi/yonetim/yeni">
                  İlk belgeyi yaz
                </KLink>
              }
            >
              Belge taslak olarak açılır; destek yöneticisi yayınladığında okuyuculara ve Kaynaklı Yardım’a açılır.
            </EmptyState>
          ) : (
            <section className="region tablewrap">
              <DataTable caption="Belgeler" captionNote={`${documents.length} belge`} columns={columns} rows={documents} rowKey={(d) => d.id} />
            </section>
          )
        }
      </QueryView>
    </div>
  );
}

// ---------------------------------------------------------------- new document
const NEW_FORM = 'kb-yeni';

export function KbManageNewScreen() {
  const go = useGo();
  const invalidate = useInvalidate();
  const setCached = useSetCached();
  const warehouses = useApiQuery(['warehouses'], (signal) => masterApi.warehouses(signal));
  const [slug, setSlug] = useState('');
  const [visibility, setVisibility] = useState<string>('General');
  const [module, setModule] = useState<string>('General');
  const [warehouseId, setWarehouseId] = useState('');
  const [errorCode, setErrorCode] = useState('');
  const [title, setTitle] = useState('');
  const [body, setBody] = useState('');
  const [local, setLocal] = useState<Record<string, string>>({});
  const [attempt, setAttempt] = useState(0);

  const create = useCommand((input: KbDocumentInput, key: string) => kbApi.createDocument(input, key), {
    onSuccess: (document) => {
      setCached(['kb-document', document.id], document);
      void invalidate('kb-documents');
      go(`/bilgi-bankasi/yonetim/${document.id}`, { replace: true });
    },
    onProblem: () => setAttempt((n) => n + 1),
  });

  const server = fieldErrorsOf(create.problem);
  if (create.problem?.code === 'code_taken') {
    server.slug = create.problem.detail ?? 'Bu kısa ad kullanılıyor.';
  }

  const errorOf = (key: string): string | undefined => local[key] ?? server[key];

  const submit = (event: FormEvent) => {
    event.preventDefault();
    const next = validateContent(title, body);
    const cleanSlug = slug.trim();
    if (cleanSlug.length < 3 || cleanSlug.length > 80 || !SLUG.test(cleanSlug)) {
      next.slug = '3–80 karakter; yalnız küçük harf (a–z), rakam ve tire. Örn. hata-stk-409';
    }

    const code = errorCode.trim().toLocaleUpperCase('en-US');
    if (code.length > 0 && !ERROR_CODE.test(code)) {
      next.errorCode = 'Hata kodu AAA-000 biçiminde olmalıdır.';
    }

    setLocal(next);
    create.clearProblem();
    if (Object.keys(next).length > 0) {
      setAttempt((n) => n + 1);
      return;
    }

    void create.run({
      slug: cleanSlug,
      visibility: VISIBILITIES.find((v) => v === visibility) ?? 'General',
      module: MODULES.find((m) => m === module) ?? 'General',
      title: title.trim(),
      bodyMarkdown: body.trim(),
      warehouseId: warehouseId.length > 0 ? warehouseId : undefined,
      errorCode: code.length > 0 ? code : undefined,
    });
  };

  const labels: Record<string, string> = {
    slug: 'Kısa ad',
    visibility: 'Görünürlük',
    module: 'Modül',
    warehouseId: 'Depo kapsamı',
    errorCode: 'Hata kodu',
    title: 'Başlık',
    bodyMarkdown: 'Metin',
  };
  const summary: SummaryItem[] = [];
  for (const key of Object.keys(labels)) {
    const message = errorOf(key);
    if (message !== undefined) {
      summary.push({ fieldId: fieldDomId(NEW_FORM, key), message: `${labels[key] ?? key}: ${message}` });
    }
  }

  const problem = create.problem;
  return (
    <div className="view">
      <PageHead title="Yeni bilgi bankası belgesi" lead="Belge taslak olarak ve 1. sürümüyle açılır. Yayınlanana kadar okuyuculara görünmez." />
      <form className="region region--pad stack" onSubmit={submit} noValidate aria-label="Yeni belge">
        {summary.length > 0 && (
          <FormErrorSummary
            title={`Belge oluşturulmadı: ${summary.length} alanı düzeltin`}
            items={summary}
            focusKey={attempt}
            note={problem !== null ? `${problem.title}. Belge kaydedilmedi.` : 'Belge kaydedilmedi.'}
          />
        )}
        {problem !== null && summary.length === 0 && (
          <ProblemAlert
            problem={problem}
            title="Belge oluşturulamadı"
            onRetry={problem.isRetryable ? () => document.getElementById('kb-yeni-gonder')?.click() : undefined}
          />
        )}

        <div className="formgrid">
          <TextField
            formId={NEW_FORM}
            name="slug"
            label="Kısa ad (adres)"
            value={slug}
            onChange={setSlug}
            inputClassName="code"
            spellCheck={false}
            autoCapitalize="none"
            maxLength={80}
            required
            translate="no"
            placeholder="hata-stk-409"
            error={errorOf('slug')}
            hint="Makalenin kalıcı adresidir; sonradan değiştirilemez."
          />
          <TextField
            formId={NEW_FORM}
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
            hint="Soruda bu kod geçerse belge aramada öne alınır."
          />
          <SelectField
            formId={NEW_FORM}
            name="visibility"
            label="Görünürlük"
            value={visibility}
            onChange={setVisibility}
            options={VISIBILITIES.map((v) => ({ value: v, label: VISIBILITY_LABELS[v] }))}
            error={errorOf('visibility')}
          />
          <SelectField
            formId={NEW_FORM}
            name="module"
            label="Modül"
            value={module}
            onChange={setModule}
            options={MODULES.map((m) => ({ value: m, label: MODULE_LABELS[m] }))}
            error={errorOf('module')}
            hint="Okuyucunun rolü bu modülü kapsamalıdır (Genel ve Destek herkese açıktır)."
          />
          <SelectField
            formId={NEW_FORM}
            name="warehouseId"
            label="Depo kapsamı (isteğe bağlı)"
            value={warehouseId}
            onChange={setWarehouseId}
            placeholder={warehouses.isPending ? 'Depolar yükleniyor…' : 'Tüm depolar'}
            options={(warehouses.data ?? []).map((w) => ({ value: w.id, label: `${w.code} · ${w.name}${w.isActive ? '' : ' (pasif)'}` }))}
            error={errorOf('warehouseId')}
            hint={warehouses.isError ? 'Depo listesi yüklenemedi; belge tüm depolar için açılabilir.' : 'Seçilirse belgeyi yalnız o depoda yetkisi olanlar okur.'}
          />
          <TextField
            formId={NEW_FORM}
            name="title"
            label="Başlık"
            value={title}
            onChange={setTitle}
            maxLength={160}
            required
            fieldClassName="field--full"
            error={errorOf('title')}
          />
          <TextAreaField
            formId={NEW_FORM}
            name="bodyMarkdown"
            label="Metin (Markdown)"
            value={body}
            onChange={setBody}
            rows={16}
            maxLength={MAX_BODY}
            counter
            required
            spellCheck
            fieldClassName="field--full"
            error={errorOf('bodyMarkdown')}
            hint={MARKDOWN_HINT}
          />
        </div>

        <div className="actions actions--sticky">
          <Button onClick={() => go('/bilgi-bankasi/yonetim')}>Vazgeç</Button>
          <span className="spacer" />
          <Button id="kb-yeni-gonder" type="submit" variant="primary" pending={create.pending} pendingLabel="Belge oluşturuluyor…">
            Taslak belgeyi oluştur
          </Button>
        </div>
      </form>
    </div>
  );
}

// ---------------------------------------------------------------- document detail
const VERSION_FORM = 'kb-surum';

function NewVersionForm({ document: doc, onSaved, onStale }: { document: KbDocument; onSaved: (result: KbDocument) => void | Promise<void>; onStale: () => void }) {
  const latest = doc.versions.find((v) => v.versionNo === doc.latestVersionNo);
  // Starts from the latest version and is NOT reset when the document is reloaded after a 412,
  // so an edit in progress survives.
  const [title, setTitle] = useState(latest?.title ?? '');
  const [body, setBody] = useState(latest?.bodyMarkdown ?? '');
  const [local, setLocal] = useState<Record<string, string>>({});
  const [attempt, setAttempt] = useState(0);

  const add = useCommand((args: { title: string; body: string; etag: string }, key: string) => kbApi.addVersion(doc.id, args.title, args.body, args.etag, key), {
    onSuccess: onSaved,
    onProblem: () => setAttempt((n) => n + 1),
  });

  const server = fieldErrorsOf(add.problem);
  const errorOf = (key: string): string | undefined => local[key] ?? server[key];
  const submit = (event: FormEvent) => {
    event.preventDefault();
    const next = validateContent(title, body);
    setLocal(next);
    add.clearProblem();
    if (Object.keys(next).length > 0) {
      setAttempt((n) => n + 1);
      return;
    }

    void add.run({ title: title.trim(), body: body.trim(), etag: doc.eTag });
  };

  const summary: SummaryItem[] = [];
  for (const [key, label] of [
    ['title', 'Başlık'],
    ['bodyMarkdown', 'Metin'],
  ] as const) {
    const message = errorOf(key);
    if (message !== undefined) {
      summary.push({ fieldId: fieldDomId(VERSION_FORM, key), message: `${label}: ${message}` });
    }
  }

  const problem = add.problem;
  return (
    <form className="region region--pad stack" onSubmit={submit} noValidate aria-labelledby="kb-surum-baslik">
      <h2 className="section-h section-h--flush" id="kb-surum-baslik">
        Yeni sürüm yaz (sürüm {doc.latestVersionNo + 1})
      </h2>
      <p className="small">
        Sürümler değiştirilemez; düzeltme yeni bir sürümdür. Yeni sürüm taslak olarak eklenir ve yayınlanana kadar okuyucular yayınlı sürümü görmeye devam
        eder.
      </p>
      {problem !== null && problem.isStale && <StaleAlert what="belge" onReload={onStale} problem={problem} />}
      {summary.length > 0 && (
        <FormErrorSummary title={`Sürüm eklenmedi: ${summary.length} alanı düzeltin`} items={summary} focusKey={attempt} note="Yeni sürüm kaydedilmedi." />
      )}
      {problem !== null && !problem.isStale && summary.length === 0 && (
        <ProblemAlert
          problem={problem}
          title="Sürüm eklenemedi"
          onRetry={problem.isRetryable ? () => document.getElementById('kb-surum-gonder')?.click() : undefined}
        />
      )}
      <TextField formId={VERSION_FORM} name="title" label="Başlık" value={title} onChange={setTitle} maxLength={160} required error={errorOf('title')} />
      <TextAreaField
        formId={VERSION_FORM}
        name="bodyMarkdown"
        label="Metin (Markdown)"
        value={body}
        onChange={setBody}
        rows={16}
        maxLength={MAX_BODY}
        counter
        required
        spellCheck
        error={errorOf('bodyMarkdown')}
        hint={MARKDOWN_HINT}
      />
      <div className="actions actions--end">
        <Button id="kb-surum-gonder" type="submit" variant="primary" pending={add.pending} pendingLabel="Sürüm ekleniyor…">
          Sürüm {doc.latestVersionNo + 1} olarak kaydet
        </Button>
      </div>
    </form>
  );
}

function KbDocumentView({ document: doc, onReload, reloading }: { document: KbDocument; onReload: () => void; reloading: boolean }) {
  const me = useMe();
  const can = abilitiesOf(me);
  const { params, update } = useUrlState();
  const invalidate = useInvalidate();
  const setCached = useSetCached();
  const publishStep = useDisclosure('kb-yayinla');
  const retireStep = useDisclosure('kb-emekli');
  const [notice, setNotice] = useState<string | null>(null);
  const [writing, setWriting] = useState(false);

  const requested = Number.parseInt(params.get('surum') ?? '', 10);
  const shown: KbVersion | undefined =
    doc.versions.find((v) => v.versionNo === requested) ?? doc.versions.find((v) => v.versionNo === doc.latestVersionNo) ?? doc.versions[0];
  const retired = doc.status === 'Retired';

  const applied = async (result: KbDocument, message: string) => {
    setCached(['kb-document', doc.id], result);
    setNotice(message);
    await Promise.all([invalidate('kb-documents'), invalidate('kb-articles'), invalidate('kb-article')]);
  };

  const publish = useCommand((args: { versionNo: number; etag: string }, key: string) => kbApi.publish(doc.id, args.versionNo, args.etag, key), {
    onSuccess: (result, args) => {
      publishStep.hide();
      return applied(result, `Sürüm ${args.versionNo} yayınlandı. Okuyucular ve Kaynaklı Yardım artık bu sürümü kullanır.`);
    },
    onProblem: () => setNotice(null),
  });
  const retire = useCommand((etag: string, key: string) => kbApi.retire(doc.id, etag, key), {
    onSuccess: (result) => {
      retireStep.hide();
      return applied(result, 'Belge emekli edildi. Okuyuculara, aramaya ve Kaynaklı Yardım’a kapatıldı.');
    },
    onProblem: () => setNotice(null),
  });

  const problem = publish.problem ?? retire.problem;
  const stale = problem !== null && problem.isStale;
  const reload = () => {
    publish.clearProblem();
    retire.clearProblem();
    onReload();
  };

  const versionColumns: Column<KbVersion>[] = [
    {
      id: 'no',
      header: 'Sürüm',
      kind: 'num',
      rowHeader: true,
      cell: (v) => (
        <KLink to={`/bilgi-bankasi/yonetim/${doc.id}?surum=${v.versionNo}`} aria-current={shown?.versionNo === v.versionNo ? 'true' : undefined}>
          {v.versionNo}
        </KLink>
      ),
    },
    { id: 'title', header: 'Başlık', kind: 'name', cell: (v) => v.title },
    { id: 'author', header: 'Yazar', cell: (v) => (v.authorUserId === me.id ? 'Siz' : 'Başka bir kullanıcı') },
    { id: 'date', header: 'Yazıldığı an', kind: 'date', cell: (v) => formatDateTime(v.createdAt) },
    {
      id: 'state',
      header: 'Durum',
      cell: (v) =>
        v.isPublished ? (
          <Tag tone="ok" glyph="done">
            Yayında
          </Tag>
        ) : (
          <Tag glyph="draft">Yayında değil</Tag>
        ),
    },
  ];

  return (
    <>
      <PageHead
        title={shown?.title ?? 'Bilgi bankası belgesi'}
        sub={
          <>
            <StatusTag look={lookOf(KB_STATUS, doc.status)} />
            <Code>{doc.slug}</Code>
          </>
        }
        meta={
          <Meta
            items={[
              { label: 'Görünürlük', value: labelOf(VISIBILITY_LABELS, doc.visibility) },
              { label: 'Modül', value: labelOf(MODULE_LABELS, doc.module) },
              { label: 'Hata kodu', value: doc.errorCode !== undefined ? <Code>{doc.errorCode}</Code> : '—' },
              { label: 'Depo kapsamı', value: doc.warehouseId !== undefined ? 'Tek depo ile sınırlı' : 'Tüm depolar' },
              { label: 'Yayınlı sürüm', value: doc.publishedVersionNo !== undefined ? <span className="num">{doc.publishedVersionNo}</span> : 'Yok' },
              { label: 'Son sürüm', value: <span className="num">{doc.latestVersionNo}</span> },
              { label: 'Güncelleme', value: <span className="date">{formatDateTime(doc.updatedAt)}</span> },
            ]}
          />
        }
        actions={
          <>
            {doc.status === 'Published' && (
              <KLink className="btn" to={`/bilgi-bankasi/makale/${encodeURIComponent(doc.slug)}`}>
                Okuyucu görünümünü aç
              </KLink>
            )}
            <KLink className="btn" to="/bilgi-bankasi/yonetim">
              Belgelere dön
            </KLink>
          </>
        }
      />

      <LiveRegion>{notice !== null && <SuccessNotice title={notice} />}</LiveRegion>
      {stale && problem !== null && <StaleAlert what="belge" onReload={reload} pending={reloading} problem={problem} />}
      {problem !== null && !stale && (
        <ProblemAlert
          problem={problem}
          title={publish.problem !== null ? 'Sürüm yayınlanamadı' : 'Belge emekli edilemedi'}
          onRetry={problem.code === 'invalid_state' ? reload : undefined}
          retryLabel="Güncel belgeyi yükle"
          retryPending={reloading}
        />
      )}

      <section className="region tablewrap">
        <DataTable caption="Sürümler" captionNote="Bir sürümü görmek için numarasını seçin" columns={versionColumns} rows={doc.versions} rowKey={(v) => String(v.versionNo)} />
      </section>

      {shown !== undefined && (
        <section className="region region--pad stack" aria-labelledby="kb-onizleme">
          <h2 className="section-h section-h--flush" id="kb-onizleme">
            Sürüm {shown.versionNo} — okuyucunun göreceği biçim
          </h2>
          {shown.bodyMarkdown.length > 0 ? <SafeMarkdown source={shown.bodyMarkdown} /> : <p className="small">Bu sürümün metni boş döndü.</p>}
        </section>
      )}

      {can.supportManager && !retired && shown !== undefined && (
        <>
          <ConfirmStep
            disclosure={publishStep}
            tone="primary"
            confirmLabel={`Yayınla: sürüm ${shown.versionNo}`}
            pendingLabel="Yayınlanıyor…"
            pending={publish.pending}
            onConfirm={() => void publish.run({ versionNo: shown.versionNo, etag: doc.eTag })}
            question={
              <>
                <strong>
                  <Code>{doc.slug}</Code> belgesinin {shown.versionNo}. sürümü yayınlansın mı?
                </strong>{' '}
                Görünürlük: {labelOf(VISIBILITY_LABELS, doc.visibility)}.{' '}
                {doc.publishedVersionNo !== undefined
                  ? `Şu an yayında olan ${doc.publishedVersionNo}. sürüm aynı anda yayından kalkar.`
                  : 'Belge ilk kez okuyuculara açılır.'}{' '}
                Kaynaklı Yardım bir sonraki sorudan itibaren yalnız bu sürümün bölümlerini kullanır.
              </>
            }
          />
          <ConfirmStep
            disclosure={retireStep}
            confirmLabel="Belgeyi emekli et"
            pendingLabel="Emekli ediliyor…"
            pending={retire.pending}
            onConfirm={() => void retire.run(doc.eTag)}
            question={
              <>
                <strong>
                  <Code>{doc.slug}</Code> belgesi emekli edilsin mi?
                </strong>{' '}
                Belge okuyuculara, aramaya ve Kaynaklı Yardım’a kapanır. Emekli belge yeniden yayınlanamaz ve yeni sürüm alamaz; gerekiyorsa yeni belge
                açılır.
              </>
            }
          />
        </>
      )}

      {!retired && (
        <div className="actions actions--sticky">
          <Button onClick={() => setWriting((value) => !value)} aria-expanded={writing} aria-controls="kb-surum-alani">
            {writing ? 'Yeni sürüm formunu kapat' : 'Yeni sürüm yaz'}
          </Button>
          <span className="spacer" />
          {can.supportManager && doc.status === 'Published' && (
            <Button variant="danger" aria-expanded={retireStep.open} aria-controls={retireStep.panelId} onClick={retireStep.toggle}>
              Belgeyi emekli et…
            </Button>
          )}
          {can.supportManager && shown !== undefined && !shown.isPublished && (
            <Button variant={writing ? 'default' : 'primary'} aria-expanded={publishStep.open} aria-controls={publishStep.panelId} onClick={publishStep.toggle}>
              Bu sürümü yayınla (sürüm {shown.versionNo})…
            </Button>
          )}
        </div>
      )}
      {!retired && !can.supportManager && <p className="small">Yayınlama ve emekli etme destek yöneticisinin yetkisindedir; destek uzmanı taslak sürüm yazar.</p>}
      {retired && (
        <InfoNote title="Bu belge emekli edildi">
          <p>Emekli belge yeniden yayınlanamaz ve yeni sürüm alamaz. Konu hâlâ geçerliyse yeni bir belge açın.</p>
        </InfoNote>
      )}

      <div id="kb-surum-alani">
        {writing && !retired && (
          <NewVersionForm
            document={doc}
            onStale={onReload}
            onSaved={async (result) => {
              setWriting(false);
              update({ surum: String(result.latestVersionNo) }, { replace: true });
              await applied(result, `Sürüm ${result.latestVersionNo} taslak olarak eklendi. Yayınlanana kadar okuyucular önceki yayınlı sürümü görür.`);
            }}
          />
        )}
      </div>
    </>
  );
}

export function KbManageDetailScreen() {
  const { id = '' } = useParams();
  const query = useApiQuery(['kb-document', id], (signal) => kbApi.document(id, signal));
  return (
    <div className="view">
      <QueryView
        query={query}
        loadingLabel="Belge yükleniyor…"
        what="Belge"
        notFoundAction={
          <KLink className="btn" to="/bilgi-bankasi/yonetim">
            Belgelere dön
          </KLink>
        }
      >
        {(doc) => <KbDocumentView document={doc} onReload={() => void query.refetch()} reloading={query.isFetching} />}
      </QueryView>
    </div>
  );
}
