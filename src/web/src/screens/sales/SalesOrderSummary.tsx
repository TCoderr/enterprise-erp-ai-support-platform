import { useParams } from 'react-router-dom';
import { salesApi } from '../../api/endpoints';
import type { SalesLine, SalesOrderSummary, ShipmentSummary } from '../../api/types';
import { BalanceStrip, salesBalance } from '../../components/BalanceStrip';
import { DataTable } from '../../components/DataTable';
import type { Column } from '../../components/DataTable';
import { QueryView } from '../../components/states';
import { Button, Code, Meta, PageHead, Qty, StatusTag } from '../../components/ui';
import type { MetaItem } from '../../components/ui';
import { formatDateTime, formatMoney } from '../../lib/format';
import { SALES_STATUS, lookOf } from '../../lib/labels';
import { useApiQuery } from '../../lib/query';
import { KLink } from '../../shell/context';

function SummaryDocument({ summary }: { summary: SalesOrderSummary }) {
  const { order } = summary;
  const full = order.view === 'Full';
  const total = (pick: (line: SalesLine) => number) => order.lines.reduce((acc, line) => acc + pick(line), 0);

  const meta: MetaItem[] = [
    {
      label: 'Müşteri',
      value: (
        <>
          {order.customerName} <Code muted>{order.customerCode}</Code>
        </>
      ),
    },
    { label: 'Sevk adresi', value: order.shippingAddress.length > 0 ? order.shippingAddress : '—' },
    {
      label: 'Depo',
      value: (
        <>
          {summary.warehouseName} <Code muted>{summary.warehouseCode}</Code>
        </>
      ),
    },
    { label: 'Sipariş tarihi', value: <span className="date">{formatDateTime(order.createdAt)}</span> },
  ];
  if (order.customerTaxNumber !== undefined) {
    meta.push({ label: 'Vergi no', value: <Code>{order.customerTaxNumber}</Code> });
  }

  if (order.confirmedAt !== undefined) {
    meta.push({ label: 'Onay tarihi', value: <span className="date">{formatDateTime(order.confirmedAt)}</span> });
  }

  if (order.note !== undefined) {
    meta.push({ label: 'Not', value: <span className="pretext">{order.note}</span> });
  }

  meta.push({ label: 'Özetin üretildiği an', value: <span className="date">{formatDateTime(summary.generatedAt)}</span> });

  const columns: Column<SalesLine>[] = [
    { id: 'no', header: 'No', kind: 'num', cell: (l) => l.lineNo },
    { id: 'sku', header: 'SKU', kind: 'code', rowHeader: true, cell: (l) => <span translate="no">{l.sku}</span> },
    { id: 'name', header: 'Ürün', kind: 'name', cell: (l) => l.productName },
    { id: 'unit', header: 'Birim', cell: (l) => l.unit },
    { id: 'ordered', header: 'Sipariş', kind: 'num', cell: (l) => <Qty value={l.orderedQty} /> },
    { id: 'shipped', header: 'Sevk edilen', kind: 'num', cell: (l) => <Qty value={l.shippedQty} /> },
    { id: 'cancelled', header: 'İptal', kind: 'num', cell: (l) => <Qty value={l.cancelledQty} /> },
    { id: 'open', header: 'Açık', kind: 'num', cell: (l) => <Qty value={l.openQty} /> },
  ];
  if (full) {
    columns.push(
      { id: 'price', header: 'Birim fiyat', kind: 'num', cell: (l) => (l.unitPrice !== undefined ? formatMoney(l.unitPrice) : '—') },
      { id: 'total', header: 'Tutar', kind: 'num', cell: (l) => (l.lineTotal !== undefined ? formatMoney(l.lineTotal) : '—') },
    );
  }

  const shipmentColumns: Column<ShipmentSummary>[] = [
    { id: 'number', header: 'Sevkiyat no', kind: 'code', rowHeader: true, cell: (s) => <span translate="no">{s.number}</span> },
    { id: 'reference', header: 'Sevk referansı', kind: 'code', cell: (s) => <span translate="no">{s.dispatchReference}</span> },
    { id: 'date', header: 'Tarih', kind: 'date', cell: (s) => formatDateTime(s.createdAt) },
    { id: 'quantity', header: 'Miktar', kind: 'num', cell: (s) => <Qty value={s.totalQuantity} /> },
  ];

  return (
    <>
      {/* The legal notice comes from the server and leads the page, on screen and on paper. */}
      <p className="legal" role="note">
        {summary.legalNotice}
      </p>

      <PageHead
        title="Sipariş özeti"
        plate={order.number}
        sub={<StatusTag look={lookOf(SALES_STATUS, order.status)} />}
        meta={<Meta items={meta} />}
        actions={
          <>
            <KLink className="btn" to={`/satis/siparisler/${order.id}`}>
              Sipariş belgesine dön
            </KLink>
            <Button variant="primary" icon="print" onClick={() => window.print()}>
              Özeti yazdır
            </Button>
          </>
        }
      />

      <section className="region balance" aria-labelledby="ozet-denge">
        <h2 className="sr-only" id="ozet-denge">
          Miktar dengesi
        </h2>
        <BalanceStrip model={salesBalance(order.lines, order.status !== 'Draft')} />
      </section>

      <section className="region tablewrap">
        <DataTable
          caption="Satırlar"
          captionNote={`${order.lines.length} satır`}
          columns={columns}
          rows={order.lines}
          rowKey={(l) => l.id}
          footer={{
            label: 'Toplam',
            span: 4,
            cells: {
              ordered: <Qty value={total((l) => l.orderedQty)} />,
              shipped: <Qty value={total((l) => l.shippedQty)} />,
              cancelled: <Qty value={total((l) => l.cancelledQty)} />,
              open: <Qty value={total((l) => l.openQty)} />,
              total: order.total !== undefined ? formatMoney(order.total) : undefined,
            },
          }}
        />
      </section>

      {order.shipments.length > 0 && (
        <section className="region tablewrap">
          <DataTable caption="Sevkiyatlar" columns={shipmentColumns} rows={order.shipments} rowKey={(s) => s.id} />
        </section>
      )}

      <p className="small">
        Tutarlar TRY’dir; vergi ve iskonto içermez. {summary.legalNotice}
      </p>
    </>
  );
}

/** Print-friendly order summary. It is explicitly not an invoice or a delivery note. */
export function SalesOrderSummaryScreen() {
  const { id = '' } = useParams();
  const query = useApiQuery(['sales-order-summary', id], (signal) => salesApi.summary(id, signal));
  return (
    <div className="view">
      <QueryView
        query={query}
        loadingLabel="Sipariş özeti yükleniyor…"
        what="Sipariş"
        notFoundAction={
          <KLink className="btn" to="/">
            Açılış sayfasına dön
          </KLink>
        }
      >
        {(summary) => <SummaryDocument summary={summary} />}
      </QueryView>
    </div>
  );
}
