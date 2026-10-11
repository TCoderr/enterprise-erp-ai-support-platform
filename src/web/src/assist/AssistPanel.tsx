import { useEffect, useRef, useState } from 'react';
import type { FormEvent } from 'react';
import { assistApi, supportApi } from '../api/endpoints';
import type { ApiProblem } from '../api/problem';
import type { AssistAnswer, AssistSource, Citation, ProviderInfo } from '../api/types';
import { ConfirmStep, LiveRegion, TextAreaField, useDisclosure } from '../components/forms';
import { Icon } from '../components/Icon';
import { InfoNote, ProblemAlert, SuccessNotice } from '../components/states';
import { Button, Code, Tag } from '../components/ui';
import { formatDateTime } from '../lib/format';
import { useEscape } from '../lib/hooks';
import { useApiQuery, useInvalidate } from '../lib/query';
import { SafeMarkdown } from '../lib/safeMarkdown';
import { useCommand } from '../lib/useCommand';
import { KLink, useAssist } from '../shell/context';
import type { AssistTicket } from '../shell/context';

const MIN_QUESTION = 3;
const MAX_QUESTION = 1000;
const FORM_ID = 'yardim-form';

/** The persistent provider label. A demo provider is never presented as a language model. */
export function providerLabel(provider: ProviderInfo): string {
  if (provider.isDemo) {
    return 'Demo sağlayıcı — dil modeli değil';
  }

  if (provider.kind === 'local-model') {
    return provider.model !== undefined ? `Yerel model: ${provider.model}` : 'Yerel model';
  }

  if (provider.kind === 'disabled') {
    return 'Yanıt üretimi kapalı — yalnız kaynak araması';
  }

  return provider.model !== undefined ? `Sağlayıcı: ${provider.kind} · ${provider.model}` : `Sağlayıcı: ${provider.kind}`;
}

function ProviderTag({ provider }: { provider: ProviderInfo }) {
  return (
    <p>
      <Tag tone="plain" glyph="info">
        <span translate="no">{providerLabel(provider)}</span>
      </Tag>
    </p>
  );
}

function sourceKey(c: Citation): string {
  return `${c.documentSlug}#${c.versionNo}#${c.sectionNo}`;
}

/** belge başlığı · sürüm N · bölüm M — heading, with the retrieved passage so it can be compared with the answer. */
function SourceList({ sources }: { sources: readonly (Citation & { snippet?: string | undefined })[] }) {
  return (
    <ol className="sources">
      {sources.map((source) => (
        <li key={sourceKey(source)}>
          <KLink to={`/bilgi-bankasi/makale/${encodeURIComponent(source.documentSlug)}`}>{source.title}</KLink>
          <small>
            sürüm {source.versionNo} · bölüm {source.sectionNo} — {source.heading}
          </small>
          {source.snippet !== undefined && source.snippet.length > 0 && <blockquote>{source.snippet}</blockquote>}
        </li>
      ))}
    </ol>
  );
}

interface SaveProps {
  answerId: string;
  ticket: AssistTicket;
  provider: ProviderInfo;
  sourceCount: number;
}

/** Two explicit steps: ask to save, then confirm what goes where. The server re-checks the sources. */
function SaveToTicket({ answerId, ticket, provider, sourceCount }: SaveProps) {
  const disclosure = useDisclosure('yardim-kaydet');
  const invalidate = useInvalidate();
  const [saved, setSaved] = useState(false);
  const save = useCommand((_: null, key: string) => supportApi.saveSuggestion(ticket.id, answerId, key), {
    onSuccess: async () => {
      setSaved(true);
      await invalidate('ticket', ticket.id);
    },
  });

  if (saved) {
    return (
      <SuccessNotice title="Yanıt talebe öneri olarak kaydedildi">
        <p>
          <Code>{ticket.number}</Code> numaralı talebin yazışmasına “AI önerisi” etiketiyle eklendi.
        </p>
      </SuccessNotice>
    );
  }

  const sourceGone = save.problem?.code === 'source_no_longer_available';
  return (
    <div className="stack">
      {save.problem !== null && sourceGone && (
        <InfoNote tone="warn" title="Kaydedilemedi: kaynak artık kullanılamıyor">
          <p>
            Yanıtın dayandığı kaynaklardan en az biri artık yayında değil ya da okuma yetkiniz kalmadı. Talebe hiçbir şey eklenmedi. Soruyu
            yeniden sorarak güncel kaynaklarla yeni bir yanıt alın.
          </p>
          {save.problem.correlationId !== undefined && (
            <p className="corr">
              Korelasyon kimliği: <Code>{save.problem.correlationId}</Code>
            </p>
          )}
        </InfoNote>
      )}
      {save.problem !== null && !sourceGone && (
        <ProblemAlert
          problem={save.problem}
          title="Yanıt talebe kaydedilemedi"
          headingLevel={3}
          onRetry={save.problem.isRetryable ? () => void save.run(null) : undefined}
        />
      )}
      <div className="actions">
        <Button aria-expanded={disclosure.open} aria-controls={disclosure.panelId} onClick={disclosure.toggle}>
          Talebe öneri olarak kaydet…
        </Button>
      </div>
      <ConfirmStep
        disclosure={disclosure}
        tone="primary"
        confirmLabel="Öneriyi talebe kaydet"
        pendingLabel="Kaydediliyor…"
        pending={save.pending}
        onConfirm={() => void save.run(null)}
        question={
          <>
            Bu yanıt ve {sourceCount} kaynağı, <Code>{ticket.number}</Code> numaralı talebe{' '}
            <strong>“AI önerisi · {providerLabel(provider)}”</strong> etiketli bir yorum olarak eklenecek. Talep sahibi bu yorumu görebilir.
          </>
        }
      />
    </div>
  );
}

function AnswerView({ answer, ticket }: { answer: AssistAnswer; ticket: AssistTicket | undefined }) {
  const snippets = new Map(answer.sources.map((source) => [sourceKey(source), source.snippet]));

  switch (answer.status) {
    case 'Answered': {
      const cited = answer.citations.map((citation) => ({ ...citation, snippet: snippets.get(sourceKey(citation)) }));
      return (
        <section className="answer" aria-labelledby="yardim-yanit">
          <h3 id="yardim-yanit">Yanıt</h3>
          <ProviderTag provider={answer.provider} />
          <div className="answer__text">
            <SafeMarkdown source={answer.answer ?? ''} mode="answer" />
          </div>
          <div>
            <h3>Kaynaklar</h3>
            <SourceList sources={cited} />
          </div>
          <p className="small">
            <strong>Sınırlamalar:</strong> {answer.limitations !== undefined ? `${answer.limitations} ` : ''}
            Yanıt yalnız yukarıdaki kaynaklardan derlendi; sipariş ve stok verilerinizi içermez. Atfın geçerli olması, cümlenin kaynakla tam
            örtüştüğünü garanti etmez — alıntılarla karşılaştırın.
            {answer.expiresAt !== undefined && ` Yanıt ${formatDateTime(answer.expiresAt)} sonrasında sunucudan silinir.`}
          </p>
          {answer.id !== undefined && ticket !== undefined && (
            <SaveToTicket answerId={answer.id} ticket={ticket} provider={answer.provider} sourceCount={cited.length} />
          )}
        </section>
      );
    }
    case 'NoSource':
      return (
        <section className="answer" aria-labelledby="yardim-yok">
          <h3 id="yardim-yok">Yeterli yetkili kaynak yok</h3>
          <InfoNote title="Bu soruya dayanak olacak bir kaynak bulunamadı">
            <p>{answer.limitations ?? 'Okumaya yetkili olduğunuz yayınlı belgelerde sorunuzla eşleşen bölüm yok.'}</p>
            <p>Yanıt üretilmedi. Hata kodunu veya ekran adını ekleyerek yeniden sorun, bilgi bankasında arayın ya da bir destek talebi açın.</p>
            <div className="actions">
              <KLink className="btn" to="/bilgi-bankasi">
                Bilgi bankasında ara
              </KLink>
              <KLink className="btn" to="/destek/talepler/yeni">
                Destek talebi aç
              </KLink>
            </div>
          </InfoNote>
        </section>
      );
    case 'ProviderUnavailable':
      return (
        <section className="answer" aria-labelledby="yardim-saglayici">
          <h3 id="yardim-saglayici">Yanıt şu anda üretilemiyor</h3>
          <ProviderTag provider={answer.provider} />
          <InfoNote tone="warn" title="Yanıt sağlayıcısı kullanılamıyor">
            <p>
              {answer.limitations ?? 'Yanıt üretici şu anda kullanılamıyor.'} Sipariş, stok ve destek işlemleri bundan etkilenmez.
              {answer.sources.length > 0
                ? ` Sorunuzla eşleşen ${answer.sources.length} kaynak bulundu; aşağıdan doğrudan okuyabilirsiniz.`
                : ' Sorunuzla eşleşen kaynak listesi boş döndü.'}
            </p>
          </InfoNote>
          {answer.sources.length > 0 && (
            <div>
              <h3>Bulunan kaynaklar</h3>
              <SourceList sources={answer.sources} />
            </div>
          )}
        </section>
      );
    case 'RejectedUnsupported':
      return (
        <section className="answer" aria-labelledby="yardim-red">
          <h3 id="yardim-red">Yanıt gösterilmiyor</h3>
          <ProviderTag provider={answer.provider} />
          <InfoNote tone="warn" title="Üretilen yanıt kaynaklarla doğrulanamadı">
            <p>
              {answer.limitations ?? 'Sağlayıcının çıktısı, sunucunun getirdiği kaynaklara atıf içermiyordu.'} Doğrulanamayan metin size
              gösterilmez. Soruyu daha açık biçimde yeniden sorabilir ya da yalnız kaynak araması yapabilirsiniz.
            </p>
          </InfoNote>
        </section>
      );
    case 'AccessChanged':
      return (
        <section className="answer" aria-labelledby="yardim-erisim">
          <h3 id="yardim-erisim">Erişim değişti</h3>
          <InfoNote tone="warn" title="Yanıt hazırlanırken erişiminiz veya kaynağın yayın durumu değişti">
            <p>
              {answer.limitations ?? 'Yanıtın dayandığı kaynak artık okunamıyor.'} Üretilen metin teslim edilmedi. Soruyu yeniden sorarak
              güncel yetkinizle arama yapın.
            </p>
          </InfoNote>
        </section>
      );
    default:
      return (
        <InfoNote tone="warn" title="Beklenmeyen yanıt durumu">
          <p>
            Sunucu tanınmayan bir durum döndürdü: <Code>{String(answer.status)}</Code>
          </p>
        </InfoNote>
      );
  }
}

type Result = { kind: 'answer'; answer: AssistAnswer } | { kind: 'search'; sources: AssistSource[] };

/**
 * Kaynaklı Yardım: a side panel, never a conversation. One question, one result; nothing is kept
 * once the panel closes or the user changes (the panel unmounts and its state goes with it).
 */
interface AssistPanelProps {
  /** Narrow screens: the panel is a sheet over the page (dialog semantics, background inert). */
  modal: boolean;
  /**
   * The user just opened the panel to ask something, so the question box takes focus. Not set when
   * the page was loaded with the panel already open.
   */
  focusQuestion?: boolean;
}

export function AssistPanel({ modal, focusQuestion = false }: AssistPanelProps) {
  const { request, close, clearTicket } = useAssist();
  const [question, setQuestion] = useState(request.prefill ?? '');
  const [result, setResult] = useState<Result | null>(null);
  const [fieldError, setFieldError] = useState<string | undefined>(undefined);
  const closeButton = useRef<HTMLButtonElement>(null);
  const status = useApiQuery(['assist-status'], (signal) => assistApi.status(signal));

  const ask = useCommand((text: string) => assistApi.ask(text, request.ticket?.id), {
    onSuccess: (answer) => setResult({ kind: 'answer', answer }),
  });
  const search = useCommand((text: string) => assistApi.search(text), {
    onSuccess: (sources) => setResult({ kind: 'search', sources }),
  });

  useEscape(modal, close);
  useEffect(() => {
    if (modal) {
      closeButton.current?.focus();
    } else if (focusQuestion) {
      document.getElementById(`${FORM_ID}-question`)?.focus();
    }
  }, [focusQuestion, modal]);

  const validated = (): string | null => {
    const text = question.trim();
    if (text.length < MIN_QUESTION || text.length > MAX_QUESTION) {
      setFieldError(`Soru ${MIN_QUESTION}–${MAX_QUESTION} karakter olmalıdır.`);
      document.getElementById(`${FORM_ID}-question`)?.focus();
      return null;
    }

    setFieldError(undefined);
    return text;
  };

  const submitAsk = (event: FormEvent) => {
    event.preventDefault();
    const text = validated();
    if (text !== null) {
      search.clearProblem();
      setResult(null);
      void ask.run(text);
    }
  };

  const submitSearch = () => {
    const text = validated();
    if (text !== null) {
      ask.clearProblem();
      setResult(null);
      void search.run(text);
    }
  };

  const problem: ApiProblem | null = ask.problem ?? search.problem;
  const busy = ask.pending || search.pending;
  // A server-side rejection of the text itself (length, invisible characters) belongs next to the field.
  const serverFieldError = problem?.errors.find((e) => e.field === 'question' || e.field === 'query')?.detail;
  const provider = status.data;

  return (
    <aside
      className="assist"
      id="yardim"
      aria-labelledby="yardim-baslik"
      {...(modal ? { role: 'dialog', 'aria-modal': true } : {})}
      // As a side column the panel is not modal, so Escape closes it only while focus is inside it.
      onKeyDown={(event) => {
        if (event.key === 'Escape' && !modal) {
          close();
        }
      }}
    >
      <div className="assist__head">
        <h2 id="yardim-baslik">
          <Icon name="assist" />
          Kaynaklı Yardım
        </h2>
        <button type="button" className="iconbtn iconbtn--panel" ref={closeButton} aria-label="Kaynaklı Yardım panelini kapat" onClick={close}>
          <Icon name="close" />
        </button>
      </div>
      <div className="assist__body">
        <div aria-live="polite">
          {status.isPending && <p className="small">Sağlayıcı durumu yükleniyor…</p>}
          {status.isError && <p className="small">Sağlayıcı durumu okunamadı; arama ve soru yine de denenebilir.</p>}
          {provider !== undefined && <ProviderTag provider={provider} />}
          {provider?.kind === 'disabled' && (
            <p className="small">Yanıt üretimi bu kurulumda kapalı. “Yalnız kaynak ara” çalışır; soru sorulursa yalnız kaynak listesi döner.</p>
          )}
        </div>

        {request.ticket !== undefined && (
          <p className="small">
            Talep bağlamı: <Code>{request.ticket.number}</Code>. Bu talebin modülü, hata kodu ve başlığı aramada ipucu olarak kullanılır;
            açıklaması ve yorumları kullanılmaz.{' '}
            <button type="button" className="linkbtn" onClick={clearTicket}>
              Talep bağlamını kaldır
            </button>
          </p>
        )}

        <form className="stack" onSubmit={submitAsk} noValidate>
          <TextAreaField
            formId={FORM_ID}
            name="question"
            label="Sorunuz"
            value={question}
            onChange={(value) => {
              setQuestion(value);
              if (fieldError !== undefined) {
                setFieldError(undefined);
              }
            }}
            rows={4}
            maxLength={MAX_QUESTION}
            counter
            required
            error={fieldError ?? serverFieldError}
            placeholder="Örn. STK-409 hatası ne anlama geliyor…"
            hint="Yalnız okumaya yetkili olduğunuz yayınlı belgelerden yanıtlanır. Sorular kişisel veri içerebilir; soru metni saklanmaz."
          />
          <div className="actions">
            <Button type="submit" pending={ask.pending} pendingLabel="Kaynaklar aranıyor…">
              Kaynaklarda ara ve yanıtla
            </Button>
            <Button onClick={submitSearch} pending={search.pending} pendingLabel="Aranıyor…">
              Yalnız kaynak ara
            </Button>
          </div>
        </form>

        <LiveRegion>
          {busy && (
            <p role="status" aria-busy="true" className="small">
              {ask.pending ? 'Kaynaklar aranıyor ve yanıt hazırlanıyor…' : 'Kaynaklar aranıyor…'}
            </p>
          )}
          {problem !== null && !busy && (
            <ProblemAlert
              problem={problem}
              title={ask.problem !== null ? 'Soru yanıtlanamadı' : 'Kaynak araması yapılamadı'}
              headingLevel={3}
              onRetry={problem.isRetryable ? (ask.problem !== null ? () => void ask.run(question.trim()) : submitSearch) : undefined}
            />
          )}
          {result?.kind === 'answer' && !busy && <AnswerView answer={result.answer} ticket={request.ticket} />}
          {result?.kind === 'search' && !busy && (
            <section className="answer" aria-labelledby="yardim-arama">
              <h3 id="yardim-arama">Kaynak araması</h3>
              {result.sources.length === 0 ? (
                <InfoNote title="Eşleşen kaynak bulunamadı">
                  <p>Okumaya yetkili olduğunuz yayınlı belgelerde bu ifadeyle eşleşen bölüm yok. Farklı sözcüklerle ya da hata koduyla deneyin.</p>
                </InfoNote>
              ) : (
                <>
                  <p className="small">{result.sources.length} bölüm bulundu. Yanıt üretilmedi; kaynakları doğrudan okuyabilirsiniz.</p>
                  <SourceList sources={result.sources} />
                </>
              )}
            </section>
          )}
        </LiveRegion>
      </div>
    </aside>
  );
}
