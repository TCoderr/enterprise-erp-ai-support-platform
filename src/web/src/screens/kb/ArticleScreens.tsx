import { useState } from 'react';
import type { FormEvent } from 'react';
import { useParams } from 'react-router-dom';
import { kbApi } from '../../api/endpoints';
import type { AppModule, KbArticleListItem } from '../../api/types';
import { DataTable } from '../../components/DataTable';
import type { Column } from '../../components/DataTable';
import { SelectField, TextField, fieldDomId } from '../../components/forms';
import { EmptyState, QueryView } from '../../components/states';
import { Button, Code, Meta, PageHead, Tag } from '../../components/ui';
import { formatDate, formatDateTime } from '../../lib/format';
import { MODULE_LABELS, VISIBILITY_LABELS, labelOf } from '../../lib/labels';
import { useUrlState } from '../../lib/lists';
import { useApiQuery } from '../../lib/query';
import { SafeMarkdown } from '../../lib/safeMarkdown';
import { KLink, useAssist, useWarehouse } from '../../shell/context';

const MODULES: readonly AppModule[] = ['General', 'Sales', 'Purchasing', 'Inventory', 'Support'];
const FORM_ID = 'makale-suzgec';
const ERROR_CODE = /^[A-Z]{3}-\d{3}$/;

function ArticleFilters({
  search,
  module,
  errorCode,
  onApply,
}: {
  search: string;
  module: string;
  errorCode: string;
  onApply: (changes: Record<string, string | null>) => void;
}) {
  const [text, setText] = useState(search);
  const [moduleValue, setModuleValue] = useState(module);
  const [code, setCode] = useState(errorCode);
  const [errors, setErrors] = useState<Record<string, string>>({});

  const submit = (event: FormEvent) => {
    event.preventDefault();
    const next: Record<string, string> = {};
    const cleanCode = code.trim().toLocaleUpperCase('en-US');
    if (text.trim().length > 64) {
      next.search = 'Arama en fazla 64 karakter olabilir.';
    }

    if (cleanCode.length > 0 && !ERROR_CODE.test(cleanCode)) {
      next.errorCode = 'Hata kodu AAA-000 biçiminde olmalıdır (örn. STK-409).';
    }

    setErrors(next);
    const firstInvalid = Object.keys(next)[0];
    if (firstInvalid !== undefined) {
      document.getElementById(fieldDomId(FORM_ID, firstInvalid))?.focus();
      return;
    }

    onApply({ ara: text.trim(), modul: moduleValue, kod: cleanCode });
  };

  return (
    <form className="region filters" role="search" aria-label="Makale araması" onSubmit={submit} noValidate>
      <TextField
        formId={FORM_ID}
        name="search"
        label="Başlıkta veya metinde ara"
        type="search"
        value={text}
        onChange={setText}
        maxLength={64}
        placeholder="Örn. kısmi sevkiyat…"
        error={errors.search}
      />
      <SelectField
        formId={FORM_ID}
        name="module"
        label="Modül"
        value={moduleValue}
        onChange={setModuleValue}
        placeholder="Tüm modüller"
        options={MODULES.map((m) => ({ value: m, label: MODULE_LABELS[m] }))}
      />
      <TextField
        formId={FORM_ID}
        name="errorCode"
        label="Hata kodu"
        value={code}
        onChange={setCode}
        inputClassName="code"
        spellCheck={false}
        autoCapitalize="characters"
        maxLength={7}
        translate="no"
        placeholder="STK-409"
        error={errors.errorCode}
      />
      <div className="actions">
        <Button type="submit">Makalelerde ara</Button>
        {(search.length > 0 || module.length > 0 || errorCode.length > 0) && (
          <Button
            onClick={() => {
              setText('');
              setModuleValue('');
              setCode('');
              setErrors({});
              onApply({ ara: null, modul: null, kod: null });
            }}
          >
            Süzgeçleri temizle
          </Button>
        )}
      </div>
    </form>
  );
}

export function ArticleListScreen() {
  const { params, update } = useUrlState();
  const assist = useAssist();
  const search = params.get('ara') ?? '';
  const moduleParam = params.get('modul') ?? '';
  const module = MODULES.find((m) => m === moduleParam);
  const errorCode = ERROR_CODE.test(params.get('kod') ?? '') ? (params.get('kod') ?? '') : '';
  const filtered = search.length > 0 || module !== undefined || errorCode.length > 0;

  const query = useApiQuery(['kb-articles', search, module ?? null, errorCode], (signal) =>
    kbApi.articles({ search: search.length > 0 ? search : undefined, module, errorCode: errorCode.length > 0 ? errorCode : undefined }, signal),
  );

  const columns: Column<KbArticleListItem>[] = [
    {
      id: 'title',
      header: 'Makale',
      kind: 'name',
      rowHeader: true,
      cell: (a) => <KLink to={`/bilgi-bankasi/makale/${encodeURIComponent(a.slug)}`}>{a.title}</KLink>,
    },
    { id: 'module', header: 'Modül', cell: (a) => labelOf(MODULE_LABELS, a.module) },
    { id: 'code', header: 'Hata kodu', kind: 'code', cell: (a) => (a.errorCode !== undefined ? <span translate="no">{a.errorCode}</span> : <span className="muted">—</span>) },
    { id: 'version', header: 'Sürüm', kind: 'num', cell: (a) => a.versionNo },
    { id: 'updated', header: 'Güncelleme', kind: 'date', cell: (a) => formatDate(a.updatedAt) },
  ];

  return (
    <div className="view">
      <PageHead
        title="Bilgi bankası"
        lead="Okumaya yetkili olduğunuz yayınlı prosedür ve hata kodu makaleleri. Kaynaklı Yardım yanıtları yalnız bu makalelere dayanır."
      />
      <ArticleFilters key={`${search}|${moduleParam}|${errorCode}`} search={search} module={module ?? ''} errorCode={errorCode} onApply={(changes) => update(changes)} />
      <QueryView query={query} loadingLabel="Makaleler yükleniyor…">
        {(articles) =>
          articles.length === 0 ? (
            filtered ? (
              <EmptyState
                title="Aramanızla eşleşen makale yok"
                action={
                  <div className="actions actions--end">
                    <Button onClick={() => update({ ara: null, modul: null, kod: null })}>Süzgeçleri temizle</Button>
                    <Button icon="assist" onClick={() => assist.open(search.length > 0 ? { prefill: search } : {})}>
                      Kaynaklı Yardım’a sor
                    </Button>
                  </div>
                }
              >
                Okuyabildiğiniz yayınlı makalelerde bu ifade, modül ya da hata kodu bulunamadı. Arama, başlıkta ve metinde geçen ifadeyi aynen arar.
              </EmptyState>
            ) : (
              <EmptyState title="Okuyabileceğiniz yayınlı makale yok">
                Makaleler destek ekibi tarafından yazılır ve yayınlandığında burada görünür.
              </EmptyState>
            )
          ) : (
            <section className="region tablewrap">
              <DataTable caption="Makaleler" captionNote={`${articles.length} makale`} columns={columns} rows={articles} rowKey={(a) => a.slug} />
            </section>
          )
        }
      </QueryView>
    </div>
  );
}

export function ArticleScreen() {
  const { slug = '' } = useParams();
  const { nameOf } = useWarehouse();
  const query = useApiQuery(['kb-article', slug], (signal) => kbApi.article(slug, signal));
  return (
    <div className="view">
      <QueryView
        query={query}
        loadingLabel="Makale yükleniyor…"
        what="Makale"
        notFoundAction={
          <KLink className="btn" to="/bilgi-bankasi">
            Makalelere dön
          </KLink>
        }
      >
        {(article) => (
          <article className="stack stack--wide">
            <PageHead
              title={article.title}
              sub={
                <>
                  <Tag tone="plain">{labelOf(MODULE_LABELS, article.module)}</Tag>
                  {article.errorCode !== undefined && (
                    <Tag tone="plain">
                      Hata kodu <Code>{article.errorCode}</Code>
                    </Tag>
                  )}
                </>
              }
              meta={
                <Meta
                  items={[
                    { label: 'Yayınlı sürüm', value: <span className="num">{article.versionNo}</span> },
                    { label: 'Sürüm tarihi', value: <span className="date">{formatDateTime(article.publishedVersionCreatedAt)}</span> },
                    { label: 'Görünürlük', value: labelOf(VISIBILITY_LABELS, article.visibility) },
                    ...(article.warehouseId !== undefined ? [{ label: 'Depo kapsamı', value: nameOf(article.warehouseId) }] : []),
                  ]}
                />
              }
              actions={
                <KLink className="btn" to="/bilgi-bankasi">
                  Makalelere dön
                </KLink>
              }
            />
            <section className="region region--pad" aria-labelledby="makale-metni">
              {/* Keeps the outline sequential: article headings render as h3–h6 below this h2. */}
              <h2 className="sr-only" id="makale-metni">
                Makale metni
              </h2>
              {/* Rendered as React elements only: raw HTML stays text and links are never clickable. */}
              <SafeMarkdown source={article.bodyMarkdown} />
            </section>
          </article>
        )}
      </QueryView>
    </div>
  );
}
