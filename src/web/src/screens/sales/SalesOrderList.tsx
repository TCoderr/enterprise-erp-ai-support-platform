import { salesApi } from '../../api/endpoints';
import type { SalesOrderListItem, SalesOrderStatus } from '../../api/types';
import { hasRole } from '../../auth/permissions';
import { useMe } from '../../auth/session';
import { DataTable } from '../../components/DataTable';
import type { Column } from '../../components/DataTable';
import { CheckField, SelectField } from '../../components/forms';
import { EmptyState } from '../../components/states';
import { Button, Code, PageHead, Qty, StatusTag, Tag } from '../../components/ui';
import { formatDateTime, formatMoney } from '../../lib/format';
import { SALES_STATUS, lookOf, personLabel } from '../../lib/labels';
import { ListRegion, useCursorList, useUrlState } from '../../lib/lists';
import { KLink, useWarehouse } from '../../shell/context';

const STATUSES: readonly SalesOrderStatus[] = ['Draft', 'Confirmed', 'PartiallyShipped', 'Shipped', 'Cancelled'];
const FORM_ID = 'siparis-suzgec';

export function SalesOrderListScreen() {
  const me = useMe();
  const { selected } = useWarehouse();
  const { params, update } = useUrlState();
  const statusParam = params.get('durum');
  const status = STATUSES.find((s) => s === statusParam);
  const mine = params.get('benim') === '1';
  const manager = hasRole(me, 'SalesManager');
  const filtered = status !== undefined || mine;

  const list = useCursorList<SalesOrderListItem>(['sales-orders', selected?.id ?? null, status ?? null, mine], (cursor, signal) =>
    salesApi.list({ warehouseId: selected?.id, status, mine, cursor }, signal),
  );

  const showTotals = list.items.some((item) => item.total !== undefined);
  const columns: Column<SalesOrderListItem>[] = [
    {
      id: 'number',
      header: 'Sipariş no',
      kind: 'code',
      rowHeader: true,
      cell: (o) => (
        <KLink to={`/satis/siparisler/${o.id}`} translate="no">
          {o.number}
        </KLink>
      ),
    },
    { id: 'customer', header: 'Müşteri', kind: 'name', cell: (o) => o.customerName },
    { id: 'status', header: 'Durum', cell: (o) => <StatusTag look={lookOf(SALES_STATUS, o.status)} /> },
    { id: 'owner', header: 'Sahip', cell: (o) => personLabel(o.ownerDisplayName, o.ownerUserId, me.id) },
    { id: 'date', header: 'Oluşturma', kind: 'date', cell: (o) => formatDateTime(o.createdAt) },
    { id: 'ordered', header: 'Sipariş', kind: 'num', cell: (o) => <Qty value={o.orderedQty} /> },
    { id: 'shipped', header: 'Sevk edilen', kind: 'num', cell: (o) => <Qty value={o.shippedQty} /> },
    { id: 'open', header: 'Açık', kind: 'num', cell: (o) => <Qty value={o.openQty} /> },
  ];
  if (showTotals) {
    columns.push({ id: 'total', header: 'Toplam', kind: 'num', cell: (o) => (o.total !== undefined ? formatMoney(o.total) : '—') });
  }

  return (
    <div className="view">
      <PageHead
        title="Satış siparişleri"
        sub={
          selected !== null ? (
            <Tag tone="plain">
              <Code>{selected.code}</Code>
              {selected.name}
            </Tag>
          ) : undefined
        }
        actions={
          <KLink className="btn btn--primary" to="/satis/siparisler/yeni">
            Yeni sipariş
          </KLink>
        }
      />

      <form className="region filters" role="search" aria-label="Sipariş süzgeçleri" onSubmit={(event) => event.preventDefault()}>
        <SelectField
          formId={FORM_ID}
          name="durum"
          label="Durum"
          value={status ?? ''}
          onChange={(value) => update({ durum: value })}
          placeholder="Tüm durumlar"
          options={STATUSES.map((s) => ({ value: s, label: SALES_STATUS[s].label }))}
        />
        {manager && (
          <CheckField formId={FORM_ID} name="benim" label="Yalnız benim siparişlerim" checked={mine} onChange={(checked) => update({ benim: checked ? '1' : null })} />
        )}
        {filtered && (
          <div>
            <Button onClick={() => update({ durum: null, benim: null })}>Süzgeçleri temizle</Button>
          </div>
        )}
      </form>

      <ListRegion
        list={list}
        loadingLabel="Siparişler yükleniyor…"
        noun="sipariş"
        scopeNoun="Depo"
        empty={
          filtered ? (
            <EmptyState title="Bu süzgeçle eşleşen sipariş yok" action={<Button onClick={() => update({ durum: null, benim: null })}>Süzgeçleri temizle</Button>}>
              Seçili depoda bu durumda sipariş bulunamadı. Süzgeci değiştirin ya da üstteki depo seçiciyle başka bir depoya bakın.
            </EmptyState>
          ) : (
            <EmptyState
              title="Bu depoda henüz satış siparişi yok"
              action={
                <KLink className="btn" to="/satis/siparisler/yeni">
                  İlk siparişi oluştur
                </KLink>
              }
            >
              {manager
                ? 'Seçili depoya ait siparişler burada listelenir.'
                : 'Burada yalnız sizin oluşturduğunuz siparişler listelenir. Taslak sipariş stok ayırmaz; onaylandığında rezerve edilir.'}
            </EmptyState>
          )
        }
      >
        {(items) => (
          <DataTable caption="Satış siparişleri" captionHidden columns={columns} rows={items} rowKey={(o) => o.id} />
        )}
      </ListRegion>
    </div>
  );
}
