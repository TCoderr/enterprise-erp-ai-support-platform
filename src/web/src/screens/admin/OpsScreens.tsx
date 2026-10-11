import { useState } from 'react';
import type { FormEvent } from 'react';
import { adminApi, masterApi, opsApi } from '../../api/endpoints';
import { asProblem } from '../../api/problem';
import type { AiUsageRow, AuditEntry, MetricRow, ReconciliationFinding, ReconciliationRun } from '../../api/types';
import { DataTable } from '../../components/DataTable';
import type { Column } from '../../components/DataTable';
import { LiveRegion, TextField } from '../../components/forms';
import { EmptyState, InfoNote, LoadingRegion, ProblemAlert, SuccessNotice } from '../../components/states';
import { Button, Code, PageHead, Qty, Tag } from '../../components/ui';
import { formatDateTime, formatQuantity } from '../../lib/format';
import { reconciliationRuleLabel } from '../../lib/labels';
import { ListRegion, useCursorList, useUrlState } from '../../lib/lists';
import { useApiQuery, useSetCached } from '../../lib/query';
import { useCommand } from '../../lib/useCommand';

// ---------------------------------------------------------------- audit
const AUDIT_FORM = 'denetim-suzgec';

function AuditFilters({ action, entityType, onApply }: { action: string; entityType: string; onApply: (changes: Record<string, string | null>) => void }) {
  const [actionText, setActionText] = useState(action);
  const [entityText, setEntityText] = useState(entityType);
  const submit = (event: FormEvent) => {
    event.preventDefault();
    onApply({ eylem: actionText.trim(), varlik: entityText.trim() });
  };

  return (
    <form className="region filters" role="search" aria-label="Denetim süzgeçleri" onSubmit={submit} noValidate>
      <TextField
        formId={AUDIT_FORM}
        name="action"
        label="Eylem kodu"
        value={actionText}
        onChange={setActionText}
        inputClassName="code"
        spellCheck={false}
        autoCapitalize="none"
        maxLength={64}
        translate="no"
        placeholder="sales.order_confirmed"
        hint="Tam eylem kodu, örn. sales.shipment_posted"
      />
      <TextField
        formId={AUDIT_FORM}
        name="entityType"
        label="Varlık türü"
        value={entityText}
        onChange={setEntityText}
        inputClassName="code"
        spellCheck={false}
        autoCapitalize="none"
        maxLength={64}
        translate="no"
        placeholder="SalesOrder"
        hint="Örn. SalesOrder, Shipment, User, Ticket"
      />
      <div className="actions">
        <Button type="submit">Denetim kaydını süz</Button>
        {(action.length > 0 || entityType.length > 0) && (
          <Button
            onClick={() => {
              setActionText('');
              setEntityText('');
              onApply({ eylem: null, varlik: null });
            }}
          >
            Süzgeçleri temizle
          </Button>
        )}
      </div>
    </form>
  );
}

export function AuditScreen() {
  const { params, update } = useUrlState();
  const action = params.get('eylem') ?? '';
  const entityType = params.get('varlik') ?? '';
  const users = useApiQuery(['admin-users'], (signal) => adminApi.users(signal));
  const warehouses = useApiQuery(['warehouses'], (signal) => masterApi.warehouses(signal));
  const list = useCursorList<AuditEntry>(['audit', action, entityType], (cursor, signal) =>
    adminApi.audit({ action: action.length > 0 ? action : undefined, entityType: entityType.length > 0 ? entityType : undefined, cursor }, signal),
  );

  const actor = (userId: string) => {
    const user = users.data?.find((u) => u.id === userId);
    return user !== undefined ? user.displayName : <Code muted>{userId.slice(0, 8)}</Code>;
  };
  const warehouse = (warehouseId: string | undefined) => {
    if (warehouseId === undefined) {
      return <span className="muted">—</span>;
    }

    const found = warehouses.data?.find((w) => w.id === warehouseId);
    return found !== undefined ? <Code>{found.code}</Code> : <Code muted>{warehouseId.slice(0, 8)}</Code>;
  };

  const columns: Column<AuditEntry>[] = [
    { id: 'time', header: 'Zaman', kind: 'date', cell: (e) => formatDateTime(e.occurredAt) },
    { id: 'action', header: 'Eylem', kind: 'code', cell: (e) => <span translate="no">{e.action}</span> },
    { id: 'actor', header: 'Kullanıcı', cell: (e) => actor(e.actorUserId) },
    { id: 'entityType', header: 'Varlık türü', kind: 'code', cell: (e) => <span translate="no">{e.entityType}</span> },
    { id: 'entityId', header: 'Varlık kimliği', kind: 'code', cell: (e) => <span translate="no">{e.entityId}</span> },
    { id: 'warehouse', header: 'Depo', cell: (e) => warehouse(e.warehouseId) },
    { id: 'correlation', header: 'Korelasyon kimliği', kind: 'code', cell: (e) => <span translate="no">{e.correlationId}</span> },
  ];

  return (
    <div className="view">
      <PageHead
        title="Denetim kaydı"
        lead="Belge, yetki ve hesap işlemlerinin değiştirilemez kaydı. Kayıtlar kimlik ve eylem kodu taşır; belge içeriği ve kişisel veri taşımaz."
      />
      <AuditFilters key={`${action}|${entityType}`} action={action} entityType={entityType} onApply={(changes) => update(changes)} />
      <ListRegion
        list={list}
        loadingLabel="Denetim kaydı yükleniyor…"
        noun="kayıt"
        empty={
          action.length > 0 || entityType.length > 0 ? (
            <EmptyState title="Bu süzgeçle eşleşen denetim kaydı yok" action={<Button onClick={() => update({ eylem: null, varlik: null })}>Süzgeçleri temizle</Button>}>
              Eylem kodu ve varlık türü tam eşleşmeyle aranır; yazımı kontrol edin.
            </EmptyState>
          ) : (
            <EmptyState title="Henüz denetim kaydı yok">İlk belge ya da yetki işlemi yapıldığında burada görünür.</EmptyState>
          )
        }
      >
        {(items) => (
          <DataTable caption="Denetim kaydı" captionHidden layout="scroll" scrollLabel="Denetim kaydı tablosu" columns={columns} rows={items} rowKey={(e) => String(e.id)} />
        )}
      </ListRegion>
    </div>
  );
}

// ---------------------------------------------------------------- operations
const OUTCOME_LABELS: Record<string, string> = { Consistent: 'Tutarlı', DriftFound: 'Fark bulundu' };

function ReconciliationResult({ run }: { run: ReconciliationRun }) {
  const clean = run.outcome === 'Consistent' || (run.outcome !== 'DriftFound' && run.findingCount === 0);
  const columns: Column<ReconciliationFinding>[] = [
    { id: 'warehouse', header: 'Depo', kind: 'code', cell: (f) => <span translate="no">{f.warehouseCode}</span> },
    { id: 'product', header: 'SKU', kind: 'code', cell: (f) => <span translate="no">{f.sku}</span> },
    { id: 'rule', header: 'Kural', kind: 'name', cell: (f) => reconciliationRuleLabel(f.rule) },
    { id: 'expected', header: 'Beklenen', kind: 'num', cell: (f) => formatQuantity(f.expected) },
    { id: 'actual', header: 'Bakiyedeki', kind: 'num', cell: (f) => formatQuantity(f.actual) },
    { id: 'diff', header: 'Fark', kind: 'num', cell: (f) => formatQuantity(f.actual - f.expected) },
  ];

  return (
    <div className="stack">
      <p className="pagehead__sub">
        {clean ? (
          <Tag tone="ok" glyph="done">
            Fark bulunmadı
          </Tag>
        ) : (
          <Tag tone="warn" glyph="warn">
            {formatQuantity(run.findingCount)} fark bulundu
          </Tag>
        )}
        <span className="muted">
          Sonuç: {OUTCOME_LABELS[run.outcome] ?? run.outcome} <Code muted>{run.outcome}</Code> · {formatQuantity(run.balancesChecked)} bakiye denetlendi · başlangıç{' '}
          <span className="date">{formatDateTime(run.startedAt)}</span> · bitiş <span className="date">{formatDateTime(run.completedAt)}</span>
        </span>
      </p>
      {!clean && (
        <>
          <InfoNote tone="warn" title="Farklar yalnız raporlanır; hiçbiri onarılmadı">
            <p>
              Mutabakat stok bakiyesini hareket günlüğü, rezervasyon defteri ve açık sipariş miktarlarıyla karşılaştırır ve farkı yazar. Bakiyeyi
              değiştirmez. Günlük ile bakiye arasındaki bir farkın uygulama içinde onarımı yoktur: stok düzeltmesi hem günlüğe hem bakiyeye aynı
              miktarı yazdığı için farkı kapatmaz. Her fark, nedeni bulunmak üzere sistem yöneticisi tarafından incelenmelidir.
            </p>
          </InfoNote>
          <DataTable
            caption="Mutabakat farkları"
            captionNote={run.findings.length < run.findingCount ? `İlk ${run.findings.length} fark gösteriliyor` : undefined}
            layout="scroll"
            scrollLabel="Mutabakat farkları tablosu"
            columns={columns}
            rows={run.findings}
            rowKey={(f, index) => `${f.warehouseId}-${f.productId}-${f.rule}-${index}`}
          />
        </>
      )}
    </div>
  );
}

export function OperationsScreen() {
  const setCached = useSetCached();
  const outbox = useApiQuery(['ops-outbox'], (signal) => opsApi.outbox(signal));
  const latest = useApiQuery(['ops-reconciliation'], (signal) => opsApi.latestReconciliation(signal));
  const metrics = useApiQuery(['ops-metrics'], (signal) => opsApi.metrics(signal));
  const [ran, setRan] = useState(false);

  const run = useCommand((_: null, key: string) => opsApi.runReconciliation(key), {
    onSuccess: (result) => {
      setCached(['ops-reconciliation'], result);
      setRan(true);
    },
    onProblem: () => setRan(false),
  });

  const counterColumns: Column<MetricRow>[] = [
    { id: 'name', header: 'Sayaç', kind: 'code', rowHeader: true, cell: (m) => <span translate="no">{m.name}</span> },
    { id: 'd1', header: 'İşlem / tür', kind: 'code', cell: (m) => <span translate="no">{m.dimension1}</span> },
    { id: 'd2', header: 'Sonuç', kind: 'code', cell: (m) => <span translate="no">{m.dimension2}</span> },
    { id: 'count', header: 'Adet', kind: 'num', cell: (m) => <Qty value={m.count} /> },
  ];
  const aiColumns: Column<AiUsageRow>[] = [
    { id: 'provider', header: 'Sağlayıcı', kind: 'code', rowHeader: true, cell: (a) => <span translate="no">{a.provider}</span> },
    { id: 'status', header: 'Yanıt durumu', kind: 'code', cell: (a) => <span translate="no">{a.status}</span> },
    { id: 'count', header: 'Adet', kind: 'num', cell: (a) => <Qty value={a.count} /> },
    { id: 'duration', header: 'Ortalama süre (ms)', kind: 'num', cell: (a) => formatQuantity(a.averageDurationMs) },
  ];

  return (
    <div className="view">
      <PageHead title="Operasyon" lead="Arka plan teslimatı, stok mutabakatı ve süreç sayaçları. Bu ekran yalnız okur; stok ya da belge değiştirmez." />

      <section className="region region--pad stack" aria-labelledby="ops-outbox">
        <h2 className="section-h section-h--flush" id="ops-outbox">
          Giden kutusu (outbox)
        </h2>
        {outbox.isPending ? (
          <p role="status" aria-busy="true" className="small">
            Giden kutusu özeti yükleniyor…
          </p>
        ) : outbox.isError ? (
          <ProblemAlert problem={asProblem(outbox.error)} title="Giden kutusu özeti yüklenemedi" onRetry={() => void outbox.refetch()} headingLevel={3} />
        ) : (
          <>
            <dl className="kv">
              <div>
                <dt>Bekleyen</dt>
                <dd>{formatQuantity(outbox.data.pending)}</dd>
              </div>
              <div>
                <dt>İşleniyor</dt>
                <dd>{formatQuantity(outbox.data.leased)}</dd>
              </div>
              <div>
                <dt>Yeniden denenecek</dt>
                <dd>{formatQuantity(outbox.data.retrying)}</dd>
              </div>
              <div>
                <dt>Teslim edilemedi</dt>
                <dd>{formatQuantity(outbox.data.dead)}</dd>
              </div>
              <div>
                <dt>Son 24 saatte işlenen</dt>
                <dd>{formatQuantity(outbox.data.processedLast24Hours)}</dd>
              </div>
            </dl>
            <p className="small">
              {outbox.data.oldestPendingCreatedAt !== undefined
                ? `En eski bekleyen ileti: ${formatDateTime(outbox.data.oldestPendingCreatedAt)}.`
                : 'Bekleyen ileti yok.'}{' '}
              {outbox.data.dead > 0 && 'Teslim edilemeyen iletiler bildirim üretmedi; stok ve belge kayıtları bundan etkilenmez.'}
            </p>
          </>
        )}
      </section>

      <section className="region region--pad stack" aria-labelledby="ops-mutabakat">
        <h2 className="section-h section-h--flush" id="ops-mutabakat">
          Stok mutabakatı
        </h2>
        <p className="small measure">
          Mutabakat <strong>salt okunur</strong> bir denetimdir: her (depo, ürün) bakiyesini hareket günlüğü, rezervasyon defteri ve onaylı siparişlerin
          açık miktarıyla karşılaştırır ve bir rapor yazar. Hiçbir bakiyeyi, hareketi ya da belgeyi değiştirmez; bulduğu farkı onarmaz.
        </p>
        <LiveRegion>
          {ran && latest.data !== undefined && latest.data !== null && (
            <SuccessNotice title="Mutabakat çalıştırıldı">
              <p>
                {latest.data.findingCount === 0
                  ? `${formatQuantity(latest.data.balancesChecked)} bakiye denetlendi; fark bulunmadı.`
                  : `${formatQuantity(latest.data.balancesChecked)} bakiye denetlendi; ${formatQuantity(latest.data.findingCount)} fark raporlandı. Hiçbiri onarılmadı.`}
              </p>
            </SuccessNotice>
          )}
        </LiveRegion>
        {run.problem !== null && (
          <ProblemAlert
            problem={run.problem}
            title="Mutabakat çalıştırılamadı"
            headingLevel={3}
            onRetry={run.problem.isRetryable ? () => void run.run(null) : undefined}
          />
        )}
        {latest.isPending ? (
          <p role="status" aria-busy="true" className="small">
            Son mutabakat raporu yükleniyor…
          </p>
        ) : latest.isError ? (
          <ProblemAlert problem={asProblem(latest.error)} title="Son mutabakat raporu yüklenemedi" onRetry={() => void latest.refetch()} headingLevel={3} />
        ) : latest.data === null ? (
          <p className="small">Henüz mutabakat çalıştırılmadı.</p>
        ) : (
          <ReconciliationResult run={latest.data} />
        )}
        <div className="actions">
          <Button variant="primary" onClick={() => void run.run(null)} pending={run.pending} pendingLabel="Mutabakat çalışıyor…">
            Mutabakatı çalıştır
          </Button>
        </div>
      </section>

      <section className="stack" aria-labelledby="ops-sayaclar">
        <h2 className="section-h section-h--flush" id="ops-sayaclar">
          Süreç sayaçları
        </h2>
        {metrics.isPending ? (
          <LoadingRegion label="Sayaçlar yükleniyor…" rows={3} />
        ) : metrics.isError ? (
          <ProblemAlert problem={asProblem(metrics.error)} title="Sayaçlar yüklenemedi" onRetry={() => void metrics.refetch()} headingLevel={3} />
        ) : (
          <>
            <p className="small">
              Sayaçlar süreç belleğindedir ve uygulama yeniden başladığında sıfırlanır. Süreç başlangıcı:{' '}
              <span className="date">{formatDateTime(metrics.data.processStartedAt)}</span>.
            </p>
            {metrics.data.counters.length === 0 ? (
              <p className="region region--pad small">Süreç başladığından beri sayılan komut ya da ileti yok.</p>
            ) : (
              <section className="region tablewrap">
                <DataTable
                  caption="Komut ve ileti sayaçları"
                  columns={counterColumns}
                  rows={metrics.data.counters}
                  rowKey={(m, index) => `${m.name}-${m.dimension1}-${m.dimension2}-${index}`}
                />
              </section>
            )}
            {metrics.data.aiUsageLast24Hours.length === 0 ? (
              <p className="region region--pad small">Son 24 saatte Kaynaklı Yardım sorusu yok.</p>
            ) : (
              <section className="region tablewrap">
                <DataTable
                  caption="Kaynaklı Yardım kullanımı"
                  captionNote="Son 24 saat · soru metni saklanmaz"
                  columns={aiColumns}
                  rows={metrics.data.aiUsageLast24Hours}
                  rowKey={(a, index) => `${a.provider}-${a.status}-${index}`}
                />
              </section>
            )}
          </>
        )}
      </section>
    </div>
  );
}
