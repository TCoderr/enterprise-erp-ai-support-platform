import { supportApi } from '../../api/endpoints';
import type { TicketListItem, TicketStatus } from '../../api/types';
import { useMe } from '../../auth/session';
import { DataTable } from '../../components/DataTable';
import type { Column } from '../../components/DataTable';
import { SelectField } from '../../components/forms';
import { EmptyState } from '../../components/states';
import { Button, Code, PageHead, StatusTag, Tag } from '../../components/ui';
import { formatDateTime } from '../../lib/format';
import { MODULE_LABELS, PRIORITY_LABELS, TICKET_STATUS, labelOf, lookOf } from '../../lib/labels';
import { ListRegion, useCursorList, useUrlState } from '../../lib/lists';
import { KLink } from '../../shell/context';

const STATUSES: readonly TicketStatus[] = ['Open', 'InProgress', 'WaitingForRequester', 'Resolved', 'Closed'];
const FORM_ID = 'talep-suzgec';

function priorityTag(priority: string) {
  const label = labelOf(PRIORITY_LABELS, priority);
  return priority === 'Urgent' || priority === 'High' ? (
    <Tag tone="warn" glyph="warn">
      {label}
    </Tag>
  ) : (
    <Tag tone="plain">{label}</Tag>
  );
}

/** "Taleplerim" (own tickets) and "Talep kuyruğu" (all tickets, support staff) share one list. */
function TicketList({ queue }: { queue: boolean }) {
  const me = useMe();
  const { params, update } = useUrlState();
  const statusParam = params.get('durum');
  const status = STATUSES.find((s) => s === statusParam);
  const assigneeParam = params.get('atanan');
  const assignee = queue && (assigneeParam === 'me' || assigneeParam === 'unassigned') ? assigneeParam : undefined;
  const filtered = status !== undefined || assignee !== undefined;

  const list = useCursorList<TicketListItem>(['tickets', queue ? 'queue' : 'mine', status ?? null, assignee ?? null], (cursor, signal) =>
    supportApi.tickets({ status, assignee, mine: !queue, cursor }, signal),
  );

  const columns: Column<TicketListItem>[] = [
    {
      id: 'number',
      header: 'Talep no',
      kind: 'code',
      rowHeader: true,
      cell: (t) => (
        <KLink to={`/destek/talepler/${t.id}`} translate="no">
          {t.number}
        </KLink>
      ),
    },
    { id: 'title', header: 'Başlık', kind: 'name', cell: (t) => t.title },
    { id: 'status', header: 'Durum', cell: (t) => <StatusTag look={lookOf(TICKET_STATUS, t.status)} /> },
    { id: 'priority', header: 'Öncelik', cell: (t) => priorityTag(t.priority) },
    { id: 'module', header: 'Modül', cell: (t) => labelOf(MODULE_LABELS, t.module) },
    { id: 'code', header: 'Hata kodu', kind: 'code', cell: (t) => (t.errorCode !== undefined ? <span translate="no">{t.errorCode}</span> : <span className="muted">—</span>) },
  ];
  if (queue) {
    columns.push({
      id: 'assignee',
      header: 'Atama',
      cell: (t) => (t.assigneeUserId === undefined ? 'Atanmamış' : t.assigneeUserId === me.id ? 'Size atanmış' : 'Başkasına atanmış'),
    });
  }

  columns.push(
    { id: 'created', header: 'Açılış', kind: 'date', cell: (t) => formatDateTime(t.createdAt) },
    { id: 'updated', header: 'Son güncelleme', kind: 'date', cell: (t) => formatDateTime(t.updatedAt) },
  );

  const clear = () => update({ durum: null, atanan: null });
  return (
    <div className="view">
      <PageHead
        title={queue ? 'Talep kuyruğu' : 'Taleplerim'}
        lead={queue ? 'Tüm kullanıcıların destek talepleri. İç notlar yalnız destek personeline görünür.' : 'Sizin açtığınız destek talepleri.'}
        actions={
          <KLink className="btn btn--primary" to="/destek/talepler/yeni">
            Yeni talep
          </KLink>
        }
      />

      <form className="region filters" role="search" aria-label="Talep süzgeçleri" onSubmit={(event) => event.preventDefault()}>
        <SelectField
          formId={FORM_ID}
          name="durum"
          label="Durum"
          value={status ?? ''}
          onChange={(value) => update({ durum: value })}
          placeholder="Tüm durumlar"
          options={STATUSES.map((s) => ({ value: s, label: TICKET_STATUS[s].label }))}
        />
        {queue && (
          <SelectField
            formId={FORM_ID}
            name="atanan"
            label="Atama"
            value={assignee ?? ''}
            onChange={(value) => update({ atanan: value })}
            placeholder="Tümü"
            options={[
              { value: 'me', label: 'Bana atananlar' },
              { value: 'unassigned', label: 'Atanmamışlar' },
            ]}
          />
        )}
        {filtered && (
          <div>
            <Button onClick={clear}>Süzgeçleri temizle</Button>
          </div>
        )}
      </form>

      <ListRegion
        list={list}
        loadingLabel="Talepler yükleniyor…"
        noun="talep"
        empty={
          filtered ? (
            <EmptyState title="Bu süzgeçle eşleşen talep yok" action={<Button onClick={clear}>Süzgeçleri temizle</Button>}>
              Seçili durum ya da atama için talep bulunamadı.
            </EmptyState>
          ) : queue ? (
            <EmptyState title="Kuyrukta talep yok">Kullanıcılar talep açtığında burada listelenir.</EmptyState>
          ) : (
            <EmptyState
              title="Henüz destek talebiniz yok"
              action={
                <KLink className="btn" to="/destek/talepler/yeni">
                  İlk talebi aç
                </KLink>
              }
            >
              Bir ekranın nasıl kullanılacağını ya da bir hata kodunun (örn. <Code>STK-409</Code>) ne anlama geldiğini sormak için talep açabilir,
              önce Kaynaklı Yardım’a da sorabilirsiniz.
            </EmptyState>
          )
        }
      >
        {(items) => <DataTable caption={queue ? 'Talep kuyruğu' : 'Taleplerim'} captionHidden columns={columns} rows={items} rowKey={(t) => t.id} />}
      </ListRegion>
    </div>
  );
}

export function MyTicketsScreen() {
  return <TicketList queue={false} />;
}

export function TicketQueueScreen() {
  return <TicketList queue />;
}
