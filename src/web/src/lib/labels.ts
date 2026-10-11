import type {
  AdjustmentReason,
  AppModule,
  KbStatus,
  KbVisibility,
  MovementType,
  PurchaseOrderStatus,
  RelatedDocumentType,
  ReservationReason,
  SalesOrderStatus,
  TicketPriority,
  TicketStatus,
} from '../api/types';

export type Tone = 'neutral' | 'info' | 'warn' | 'ok' | 'danger' | 'plain';
export type Glyph = 'draft' | 'full' | 'half' | 'done' | 'cancel' | 'warn' | 'error' | 'info' | 'lock';

export interface StatusLook {
  label: string;
  tone: Tone;
  glyph: Glyph;
}

// Status is always glyph + word; colour is the third signal (DESIGN.md).
export const SALES_STATUS: Record<SalesOrderStatus, StatusLook> = {
  Draft: { label: 'Taslak', tone: 'neutral', glyph: 'draft' },
  Confirmed: { label: 'Onaylandı', tone: 'info', glyph: 'full' },
  PartiallyShipped: { label: 'Kısmen sevk edildi', tone: 'warn', glyph: 'half' },
  Shipped: { label: 'Sevk edildi', tone: 'ok', glyph: 'done' },
  Cancelled: { label: 'İptal edildi', tone: 'neutral', glyph: 'cancel' },
};

export const PURCHASE_STATUS: Record<PurchaseOrderStatus, StatusLook> = {
  Draft: { label: 'Taslak', tone: 'neutral', glyph: 'draft' },
  Approved: { label: 'Onaylandı', tone: 'info', glyph: 'full' },
  PartiallyReceived: { label: 'Kısmen kabul edildi', tone: 'warn', glyph: 'half' },
  Received: { label: 'Teslim alındı', tone: 'ok', glyph: 'done' },
  Cancelled: { label: 'İptal edildi', tone: 'neutral', glyph: 'cancel' },
};

export const TICKET_STATUS: Record<TicketStatus, StatusLook> = {
  Open: { label: 'Açık', tone: 'neutral', glyph: 'draft' },
  InProgress: { label: 'İşlemde', tone: 'info', glyph: 'full' },
  WaitingForRequester: { label: 'Talep sahibi bekleniyor', tone: 'warn', glyph: 'half' },
  Resolved: { label: 'Çözüldü', tone: 'ok', glyph: 'done' },
  Closed: { label: 'Kapatıldı', tone: 'neutral', glyph: 'cancel' },
};

export const KB_STATUS: Record<KbStatus, StatusLook> = {
  Draft: { label: 'Taslak', tone: 'neutral', glyph: 'draft' },
  Published: { label: 'Yayında', tone: 'ok', glyph: 'done' },
  Retired: { label: 'Emekli edildi', tone: 'neutral', glyph: 'cancel' },
};

const FALLBACK: StatusLook = { label: 'Bilinmiyor', tone: 'neutral', glyph: 'draft' };

/** Look-up that survives a status value this build does not know yet. */
export function lookOf<K extends string>(table: Record<K, StatusLook>, key: string): StatusLook {
  return (table as Record<string, StatusLook | undefined>)[key] ?? { ...FALLBACK, label: key };
}

export const PRIORITY_LABELS: Record<TicketPriority, string> = {
  Low: 'Düşük',
  Normal: 'Normal',
  High: 'Yüksek',
  Urgent: 'Acil',
};

export const MODULE_LABELS: Record<AppModule, string> = {
  General: 'Genel',
  Sales: 'Satış',
  Purchasing: 'Satın alma',
  Inventory: 'Depo',
  Support: 'Destek',
};

export const VISIBILITY_LABELS: Record<KbVisibility, string> = {
  General: 'Genel (oturum açan herkes)',
  SupportOnly: 'Yalnız destek personeli',
  ManagerOnly: 'Yalnız yöneticiler',
};

export const MOVEMENT_LABELS: Record<MovementType, string> = {
  Receipt: 'Mal kabul',
  Shipment: 'Sevkiyat',
  TransferOut: 'Transfer çıkışı',
  TransferIn: 'Transfer girişi',
  Adjustment: 'Düzeltme',
};

export const RESERVATION_LABELS: Record<ReservationReason, string> = {
  Reserve: 'Rezerve edildi (onay)',
  ReleaseOnShipment: 'Sevkle düşüldü',
  ReleaseOnCancel: 'İptalle serbest bırakıldı',
};

export const ADJUSTMENT_REASON_LABELS: Record<AdjustmentReason, string> = {
  CountDifference: 'Sayım farkı',
  Damage: 'Hasar',
  Loss: 'Kayıp',
  Found: 'Bulunan',
  DataCorrection: 'Kayıt düzeltmesi',
};

export const RELATED_DOCUMENT_LABELS: Record<RelatedDocumentType, string> = {
  SalesOrder: 'Satış siparişi',
  PurchaseOrder: 'Satın alma siparişi',
  Shipment: 'Sevkiyat',
  GoodsReceipt: 'Mal kabul',
  StockTransfer: 'Depo transferi',
  StockAdjustment: 'Stok düzeltmesi',
};

export const SCREEN_LABELS: Record<string, string> = {
  'sales.orders': 'Satış · Sipariş listesi',
  'sales.order': 'Satış · Sipariş belgesi',
  'sales.dispatch': 'Depo · Sevkiyat girişi',
  'purchasing.orders': 'Satın alma · Sipariş listesi',
  'purchasing.order': 'Satın alma · Sipariş belgesi',
  'purchasing.receive': 'Depo · Mal kabul girişi',
  'inventory.balances': 'Depo · Stok bakiyeleri',
  'inventory.movements': 'Depo · Hareketler',
  'inventory.transfer': 'Depo · Transfer',
  'inventory.adjustment': 'Depo · Düzeltme',
  'support.tickets': 'Destek · Talep listesi',
  'support.ticket': 'Destek · Talep',
  'kb.articles': 'Bilgi Bankası · Makaleler',
  'kb.manage': 'Bilgi Bankası · Belge yönetimi',
  'assist.panel': 'Kaynaklı Yardım paneli',
  'admin.users': 'Yönetim · Kullanıcılar',
  'auth.login': 'Giriş',
};

/** A person's name, with a hint when it is the signed-in user. A deleted user arrives as "—". */
export function personLabel(displayName: string, userId: string, meId: string): string {
  return userId === meId ? `${displayName} (siz)` : displayName;
}

export function labelOf(table: Record<string, string>, key: string | undefined): string {
  if (key === undefined) {
    return '—';
  }

  return table[key] ?? key;
}

const RECONCILIATION_RULES: Record<string, string> = {
  onhand_equals_movement_sum: 'Eldeki = hareket günlüğünün toplamı',
  reserved_equals_reservation_sum: 'Rezerve = rezervasyon kayıtlarının toplamı',
  reserved_equals_open_order_quantity: 'Rezerve = onaylı siparişlerin açık miktarı',
  onhand_equals_last_movement_after: 'Eldeki = son hareketin "sonraki eldeki" değeri',
  reserved_equals_last_reservation_after: 'Rezerve = son rezervasyon kaydının "sonraki rezerve" değeri',
};

export function reconciliationRuleLabel(rule: string): string {
  return RECONCILIATION_RULES[rule] ?? rule;
}
