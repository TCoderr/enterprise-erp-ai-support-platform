import { useState } from 'react';
import type { FormEvent } from 'react';
import { useParams } from 'react-router-dom';
import { supportApi } from '../../api/endpoints';
import { asProblem } from '../../api/problem';
import type { Ticket, TicketComment, TicketStatus } from '../../api/types';
import { abilitiesOf, roleLabel } from '../../auth/permissions';
import { useMe } from '../../auth/session';
import { providerLabel } from '../../assist/AssistPanel';
import { CheckField, ConfirmStep, LiveRegion, SelectField, TextAreaField, useDisclosure } from '../../components/forms';
import { InfoNote, ProblemAlert, QueryView, StaleAlert, SuccessNotice } from '../../components/states';
import { Button, Code, Meta, PageHead, StatusTag, Tag } from '../../components/ui';
import type { MetaItem } from '../../components/ui';
import { formatDateTime } from '../../lib/format';
import { MODULE_LABELS, PRIORITY_LABELS, RELATED_DOCUMENT_LABELS, SCREEN_LABELS, TICKET_STATUS, labelOf, lookOf } from '../../lib/labels';
import { useApiQuery, useInvalidate, useSetCached } from '../../lib/query';
import { SafeMarkdown } from '../../lib/safeMarkdown';
import { useCommand } from '../../lib/useCommand';
import { KLink, useAssist } from '../../shell/context';

const REPLY_FORM = 'yanit';

interface Transition {
  to: TicketStatus;
  label: string;
  pending: string;
  /** Closing is final and asks for an explicit confirmation. */
  final?: boolean;
}

/** Status changes the server accepts from support staff (Ticket.ChangeStatusByStaff). */
const STAFF_TRANSITIONS: Record<TicketStatus, Transition[]> = {
  Open: [
    { to: 'InProgress', label: 'İşleme al', pending: 'İşleme alınıyor…' },
    { to: 'Resolved', label: 'Çözüldü olarak işaretle', pending: 'İşaretleniyor…' },
  ],
  InProgress: [
    { to: 'WaitingForRequester', label: 'Talep sahibinden yanıt bekle', pending: 'Güncelleniyor…' },
    { to: 'Resolved', label: 'Çözüldü olarak işaretle', pending: 'İşaretleniyor…' },
  ],
  WaitingForRequester: [
    { to: 'InProgress', label: 'Yeniden işleme al', pending: 'Güncelleniyor…' },
    { to: 'Resolved', label: 'Çözüldü olarak işaretle', pending: 'İşaretleniyor…' },
  ],
  Resolved: [
    { to: 'InProgress', label: 'Talebi yeniden aç', pending: 'Yeniden açılıyor…' },
    { to: 'Closed', label: 'Talebi kapat…', pending: 'Kapatılıyor…', final: true },
  ],
  Closed: [],
};

function aiLabel(comment: TicketComment): string {
  const provider = comment.aiProvider ?? 'bilinmiyor';
  const info = { kind: provider, isDemo: provider === 'demo', ...(comment.aiModel !== undefined ? { model: comment.aiModel } : {}) };
  return `AI önerisi · ${providerLabel(info)}`;
}

function Thread({ comments }: { comments: readonly TicketComment[] }) {
  if (comments.length === 0) {
    return <p className="region--pad small">Henüz yanıt yok.</p>;
  }

  return (
    <ol className="thread">
      {comments.map((comment) => (
        <li key={comment.id} data-kind={comment.isInternal ? 'internal' : undefined}>
          <div className="thread__head">
            <strong>{comment.authorDisplayName}</strong>
            {comment.isInternal && (
              <Tag tone="plain" glyph="lock">
                İç not · yalnız destek görür
              </Tag>
            )}
            {comment.source === 'AiSuggestion' && (
              <Tag tone="plain" glyph="info">
                <span translate="no">{aiLabel(comment)}</span>
              </Tag>
            )}
            <span className="muted date">{formatDateTime(comment.createdAt)}</span>
          </div>
          {comment.source === 'AiSuggestion' ? (
            <>
              <p className="small">{comment.authorDisplayName} tarafından Kaynaklı Yardım yanıtından kaydedildi; destek personelinin yanıtı değildir.</p>
              <SafeMarkdown source={comment.body} mode="answer" />
            </>
          ) : (
            <p className="thread__body">{comment.body}</p>
          )}
          {comment.citations !== undefined && comment.citations.length > 0 && (
            <ul className="thread__cites" aria-label="Kaynaklar">
              {comment.citations.map((c) => (
                <li key={`${c.documentSlug}-${c.versionNo}-${c.sectionNo}`}>
                  <KLink to={`/bilgi-bankasi/makale/${encodeURIComponent(c.documentSlug)}`}>{c.title}</KLink> · sürüm {c.versionNo} · bölüm {c.sectionNo} —{' '}
                  {c.heading}
                </li>
              ))}
            </ul>
          )}
        </li>
      ))}
    </ol>
  );
}

function TicketDocument({ ticket, onReload, reloading }: { ticket: Ticket; onReload: () => void; reloading: boolean }) {
  const me = useMe();
  const assist = useAssist();
  const can = abilitiesOf(me);
  const invalidate = useInvalidate();
  const setCached = useSetCached();
  const closeStep = useDisclosure('talep-kapat');
  const staffList = useApiQuery(['support-staff'], (signal) => supportApi.staff(signal), { enabled: can.supportManager });
  const [body, setBody] = useState('');
  const [internal, setInternal] = useState(false);
  const [bodyError, setBodyError] = useState<string | undefined>(undefined);
  const [assignee, setAssignee] = useState('');
  const [notice, setNotice] = useState<string | null>(null);

  const staff = can.supportStaff;
  const requester = ticket.requesterUserId === me.id;
  const closed = ticket.status === 'Closed';

  const applied = async (result: Ticket, message: string) => {
    setCached(['ticket', ticket.id], result);
    setNotice(message);
    await invalidate('tickets');
  };

  const comment = useCommand((args: { body: string; isInternal: boolean }, key: string) => supportApi.addComment(ticket.id, args.body, args.isInternal, key), {
    onSuccess: (result, args) => {
      setBody('');
      setInternal(false);
      return applied(result, args.isInternal ? 'İç not kaydedildi.' : 'Yanıtınız gönderildi.');
    },
    onProblem: () => setNotice(null),
  });
  const assign = useCommand((args: { userId: string; etag: string }) => supportApi.assign(ticket.id, args.userId, args.etag), {
    onSuccess: (result) => applied(result, `Talep ${result.assigneeDisplayName ?? 'seçilen kişiye'} atandı.`),
    onProblem: () => setNotice(null),
  });
  const status = useCommand((args: { status: TicketStatus; etag: string }) => supportApi.setStatus(ticket.id, args.status, args.etag), {
    onSuccess: (result) => {
      closeStep.hide();
      return applied(result, `Talep durumu “${lookOf(TICKET_STATUS, result.status).label}” oldu.`);
    },
    onProblem: () => setNotice(null),
  });

  const headProblem = assign.problem ?? status.problem;
  const stale = headProblem !== null && headProblem.isStale;
  const reload = () => {
    assign.clearProblem();
    status.clearProblem();
    onReload();
  };

  const submitReply = (event: FormEvent) => {
    event.preventDefault();
    const text = body.trim();
    if (text.length < 1 || text.length > 4000) {
      setBodyError('Yanıt 1–4000 karakter olmalıdır.');
      document.getElementById(`${REPLY_FORM}-body`)?.focus();
      return;
    }

    setBodyError(undefined);
    void comment.run({ body: text, isInternal: staff && internal });
  };

  // A manager assigns to any support staff member; an agent only to themselves (enforced by the server).
  const colleagues = (staffList.data ?? [])
    .filter((person) => person.id !== me.id)
    .map((person) => ({ value: person.id, label: `${person.displayName} · ${person.roles.map(roleLabel).join(', ')}` }));

  const transitions: Transition[] = staff
    ? STAFF_TRANSITIONS[ticket.status]
    : requester && ticket.status === 'Resolved'
      ? [{ to: 'InProgress', label: 'Talebi yeniden aç', pending: 'Yeniden açılıyor…' }]
      : [];

  const meta: MetaItem[] = [
    { label: 'Talep sahibi', value: requester ? `${ticket.requesterDisplayName} (siz)` : ticket.requesterDisplayName },
    { label: 'Atanan', value: ticket.assigneeDisplayName ?? 'Atanmamış' },
    {
      label: 'Modül · Ekran',
      value: `${labelOf(MODULE_LABELS, ticket.module)}${ticket.screen !== undefined ? ` · ${labelOf(SCREEN_LABELS, ticket.screen)}` : ''}`,
    },
    { label: 'Hata kodu', value: ticket.errorCode !== undefined ? <Code>{ticket.errorCode}</Code> : '—' },
    {
      label: 'İlişkili belge',
      value:
        ticket.relatedDocumentType !== undefined && ticket.relatedDocumentNumber !== undefined ? (
          <>
            {labelOf(RELATED_DOCUMENT_LABELS, ticket.relatedDocumentType)} <Code>{ticket.relatedDocumentNumber}</Code>
            <br />
            <span className="small">Yalnız etikettir, bağlantı değildir; belge kendi yetkisiyle açılır ve varlığı denetlenmemiştir.</span>
          </>
        ) : (
          '—'
        ),
    },
    { label: 'Açılış', value: <span className="date">{formatDateTime(ticket.createdAt)}</span> },
    { label: 'Son güncelleme', value: <span className="date">{formatDateTime(ticket.updatedAt)}</span> },
  ];

  return (
    <>
      <PageHead
        title={ticket.title}
        plate={ticket.number}
        sub={
          <>
            <StatusTag look={lookOf(TICKET_STATUS, ticket.status)} />
            <Tag tone="plain">Öncelik: {labelOf(PRIORITY_LABELS, ticket.priority)}</Tag>
          </>
        }
        actions={
          <Button icon="assist" aria-expanded={assist.isOpen} aria-controls="yardim" onClick={() => assist.open({ ticket: { id: ticket.id, number: ticket.number } })}>
            Bu talep için Kaynaklı Yardım
          </Button>
        }
      />

      <LiveRegion>
        {notice !== null && <SuccessNotice title={notice} />}
      </LiveRegion>

      {stale && headProblem !== null && <StaleAlert what="talep" onReload={reload} pending={reloading} problem={headProblem} />}
      {headProblem !== null && !stale && (
        <ProblemAlert
          problem={headProblem}
          title={assign.problem !== null ? 'Talep atanamadı' : 'Talep durumu değiştirilemedi'}
          onRetry={headProblem.code === 'invalid_state' ? reload : undefined}
          retryLabel="Güncel talebi yükle"
          retryPending={reloading}
        />
      )}

      <section className="region region--pad" aria-labelledby="talep-bilgi">
        <h2 className="sr-only" id="talep-bilgi">
          Talep bilgileri
        </h2>
        <Meta items={meta} flush />
      </section>

      <section className="region region--pad stack" aria-labelledby="talep-aciklama">
        <h2 className="section-h section-h--flush" id="talep-aciklama">
          Açıklama
        </h2>
        <p className="pretext">{ticket.description}</p>
      </section>

      {(transitions.length > 0 || (staff && !closed)) && (
        <section className="region region--pad stack" aria-labelledby="talep-islem">
          <h2 className="section-h section-h--flush" id="talep-islem">
            {staff ? 'Atama ve durum' : 'Durum'}
          </h2>
          {staff && !closed && (
            <div className="actions">
              {ticket.assigneeUserId !== me.id && (
                <Button onClick={() => void assign.run({ userId: me.id, etag: ticket.eTag })} pending={assign.pending} pendingLabel="Atanıyor…">
                  Talebi bana ata
                </Button>
              )}
              {can.supportManager && staffList.isPending && (
                <span role="status" aria-busy="true" className="small">
                  Destek personeli yükleniyor…
                </span>
              )}
              {can.supportManager && colleagues.length > 0 && (
                <>
                  <SelectField
                    formId="talep-ata"
                    name="assigneeUserId"
                    label="Başka bir destek personeline ata"
                    labelHidden
                    value={assignee}
                    onChange={setAssignee}
                    placeholder="Başka bir destek personeli seçin"
                    options={colleagues}
                  />
                  <Button
                    onClick={() => {
                      if (assignee.length > 0) {
                        void assign.run({ userId: assignee, etag: ticket.eTag });
                      } else {
                        document.getElementById('talep-ata-assigneeUserId')?.focus();
                      }
                    }}
                    pending={assign.pending}
                    pendingLabel="Atanıyor…"
                  >
                    Seçilen kişiye ata
                  </Button>
                </>
              )}
            </div>
          )}
          {staff && !closed && can.supportManager && staffList.isError && (
            <ProblemAlert problem={asProblem(staffList.error)} title="Destek personeli listesi yüklenemedi" onRetry={() => void staffList.refetch()} headingLevel={3} />
          )}
          {staff && !closed && !can.supportManager && <p className="small">Destek uzmanı talebi yalnız kendine atayabilir; başkasına atamayı destek yöneticisi yapar.</p>}
          {transitions.length > 0 && (
            <div className="actions">
              {transitions.map((transition) =>
                transition.final === true ? (
                  <Button
                    key={transition.to}
                    variant="danger"
                    aria-expanded={closeStep.open}
                    aria-controls={closeStep.panelId}
                    onClick={closeStep.toggle}
                  >
                    {transition.label}
                  </Button>
                ) : (
                  <Button
                    key={transition.to}
                    onClick={() => void status.run({ status: transition.to, etag: ticket.eTag })}
                    pending={status.pending}
                    pendingLabel={transition.pending}
                  >
                    {transition.label}
                  </Button>
                ),
              )}
            </div>
          )}
          <ConfirmStep
            disclosure={closeStep}
            confirmLabel="Talebi kapat"
            pendingLabel="Kapatılıyor…"
            pending={status.pending}
            onConfirm={() => void status.run({ status: 'Closed', etag: ticket.eTag })}
            question={
              <>
                <strong>
                  <Code>{ticket.number}</Code> numaralı talep kapatılsın mı?
                </strong>{' '}
                Kapalı talep sondur: yeniden açılamaz, atanamaz ve yorum eklenemez.
              </>
            }
          />
        </section>
      )}

      {ticket.diagnostic !== undefined && (
        <section className="region region--pad stack" aria-labelledby="talep-tanilama">
          <h2 className="section-h section-h--flush" id="talep-tanilama">
            Tanılama bilgisi
          </h2>
          <p className="small">Talep açılırken sunucunun eklediği bilgiler; yalnız destek personeline görünür. Belge içeriği ve kişisel veri içermez.</p>
          <Meta
            flush
            items={[
              { label: 'Uygulama sürümü', value: <Code>{ticket.diagnostic.appVersion}</Code> },
              { label: 'Modül', value: labelOf(MODULE_LABELS, ticket.diagnostic.module) },
              { label: 'Ekran kodu', value: ticket.diagnostic.screen !== undefined ? <Code>{ticket.diagnostic.screen}</Code> : '—' },
              { label: 'Hata kodu', value: ticket.diagnostic.errorCode !== undefined ? <Code>{ticket.diagnostic.errorCode}</Code> : '—' },
              { label: 'Korelasyon kimliği', value: <Code>{ticket.diagnostic.correlationId}</Code> },
              { label: 'Talep sahibinin rolleri', value: ticket.diagnostic.requesterRoles.map(roleLabel).join(' · ') || '—' },
              { label: 'Sunucu zamanı', value: <span className="date">{formatDateTime(ticket.diagnostic.serverTimeUtc)}</span> },
            ]}
          />
        </section>
      )}

      <section className="region" aria-labelledby="talep-yazisma">
        <h2 className="section-h section-h--flush region--pad" id="talep-yazisma">
          Yazışma
        </h2>
        <Thread comments={ticket.comments} />
        {closed ? (
          <div className="region--pad actions--ruled">
            <InfoNote title="Bu talep kapatıldı">
              <p>Kapalı talebe yorum eklenemez. Konu sürüyorsa yeni bir talep açın.</p>
            </InfoNote>
          </div>
        ) : (
          <form className="region--pad stack actions--ruled" onSubmit={submitReply} noValidate aria-label="Yanıt yaz">
            {comment.problem !== null && (
              <ProblemAlert
                problem={comment.problem}
                title="Yanıt gönderilemedi"
                headingLevel={3}
                onRetry={comment.problem.isRetryable ? () => document.getElementById('yanit-gonder')?.click() : comment.problem.code === 'invalid_state' ? reload : undefined}
                retryLabel={comment.problem.isRetryable ? 'Yeniden dene' : 'Güncel talebi yükle'}
              />
            )}
            <TextAreaField
              formId={REPLY_FORM}
              name="body"
              label={staff && internal ? 'İç not yaz' : 'Yanıt yaz'}
              value={body}
              onChange={(value) => {
                setBody(value);
                if (bodyError !== undefined) {
                  setBodyError(undefined);
                }
              }}
              rows={4}
              maxLength={4000}
              counter
              required
              error={bodyError ?? comment.problem?.errors.find((e) => e.field === 'body')?.detail}
              placeholder="Yanıtınızı düz metin olarak yazın…"
            />
            <div className="actions">
              {staff && <CheckField formId={REPLY_FORM} name="isInternal" label="İç not olarak kaydet (talep sahibi görmez)" checked={internal} onChange={setInternal} />}
              <span className="spacer" />
              <Button id="yanit-gonder" type="submit" variant="primary" pending={comment.pending} pendingLabel="Gönderiliyor…">
                {staff && internal ? 'İç notu kaydet' : 'Yanıtı gönder'}
              </Button>
            </div>
          </form>
        )}
      </section>
    </>
  );
}

export function TicketDetailScreen() {
  const { id = '' } = useParams();
  const query = useApiQuery(['ticket', id], (signal) => supportApi.ticket(id, signal));
  return (
    <div className="view">
      <QueryView
        query={query}
        loadingLabel="Talep yükleniyor…"
        what="Talep"
        notFoundAction={
          <KLink className="btn" to="/destek/talepler">
            Taleplerime dön
          </KLink>
        }
      >
        {(ticket) => <TicketDocument ticket={ticket} onReload={() => void query.refetch()} reloading={query.isFetching} />}
      </QueryView>
    </div>
  );
}
