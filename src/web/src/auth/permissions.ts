import type { Me, Role, WarehouseAccess, WarehouseLevel } from '../api/types';

// Presentation only. These helpers decide what to OFFER; the server decides what is allowed, and
// every screen still handles its 403 and 404 answers. Rules follow docs/02 §2.4.

const LEVEL_RANK: Record<WarehouseLevel, number> = { View: 1, Operate: 2, Supervise: 3 };

export function hasRole(me: Me, ...roles: Role[]): boolean {
  return roles.some((role) => me.roles.includes(role));
}

export function hasLevel(me: Me, warehouseId: string, level: WarehouseLevel): boolean {
  const grant = me.warehouses.find((w) => w.id === warehouseId);
  return grant !== undefined && LEVEL_RANK[grant.level] >= LEVEL_RANK[level];
}

export function warehousesWith(me: Me, level: WarehouseLevel): WarehouseAccess[] {
  return me.warehouses.filter((w) => LEVEL_RANK[w.level] >= LEVEL_RANK[level]);
}

export interface Abilities {
  sales: boolean;
  salesManager: boolean;
  purchasing: boolean;
  warehouseOps: boolean;
  /** May read stock in at least one warehouse. */
  stock: boolean;
  transfer: boolean;
  adjust: boolean;
  supportStaff: boolean;
  supportManager: boolean;
  admin: boolean;
}

export function abilitiesOf(me: Me): Abilities {
  const sales = hasRole(me, 'SalesAgent', 'SalesManager');
  const purchasing = hasRole(me, 'PurchasingAgent');
  const operator = hasRole(me, 'WarehouseOperator');
  const anyWarehouse = me.warehouses.length > 0;
  return {
    sales,
    salesManager: hasRole(me, 'SalesManager'),
    purchasing,
    warehouseOps: operator && warehousesWith(me, 'Operate').length > 0,
    stock: (sales || purchasing || operator) && anyWarehouse,
    transfer: operator && warehousesWith(me, 'Operate').length > 0,
    adjust: operator && warehousesWith(me, 'Supervise').length > 0,
    supportStaff: hasRole(me, 'SupportAgent', 'SupportManager'),
    supportManager: hasRole(me, 'SupportManager'),
    admin: hasRole(me, 'SystemAdmin'),
  };
}

export interface NavItem {
  to: string;
  label: string;
  /** Match only the exact path (used where a sibling route shares the prefix). */
  end?: boolean;
}

export interface NavGroup {
  id: string;
  label: string;
  items: NavItem[];
}

/** Navigation by task, filtered by role. A group the user has no role for is not rendered at all. */
export function navigationFor(me: Me): NavGroup[] {
  const can = abilitiesOf(me);
  const groups: NavGroup[] = [];

  if (can.sales) {
    groups.push({
      id: 'satis',
      label: 'Satış',
      items: [
        { to: '/satis/siparisler', label: 'Siparişler', end: true },
        { to: '/satis/siparisler/yeni', label: 'Yeni sipariş' },
      ],
    });
  }

  if (can.purchasing) {
    groups.push({
      id: 'satin-alma',
      label: 'Satın Alma',
      items: [
        { to: '/satin-alma/siparisler', label: 'Satın alma siparişleri', end: true },
        { to: '/satin-alma/siparisler/yeni', label: 'Yeni sipariş' },
      ],
    });
  }

  const depot: NavItem[] = [];
  if (can.stock) {
    depot.push(
      { to: '/depo/stok', label: 'Stok bakiyeleri' },
      { to: '/depo/hareketler', label: 'Hareketler' },
      { to: '/depo/rezervasyonlar', label: 'Rezervasyonlar' },
    );
  }

  if (can.warehouseOps) {
    depot.push({ to: '/depo/sevk', label: 'Sevk bekleyenler' }, { to: '/depo/kabul', label: 'Kabul bekleyenler' });
  }

  if (can.transfer) {
    depot.push({ to: '/depo/transfer', label: 'Transfer' });
  }

  if (can.adjust) {
    depot.push({ to: '/depo/duzeltme', label: 'Düzeltme' });
  }

  if (depot.length > 0) {
    groups.push({ id: 'depo', label: 'Depo', items: depot });
  }

  const support: NavItem[] = [
    { to: '/destek/talepler', label: 'Taleplerim', end: true },
    { to: '/destek/talepler/yeni', label: 'Yeni talep' },
  ];
  if (can.supportStaff) {
    support.push({ to: '/destek/kuyruk', label: 'Talep kuyruğu' });
  }

  groups.push({ id: 'destek', label: 'Destek', items: support });

  const kb: NavItem[] = [{ to: '/bilgi-bankasi', label: 'Makaleler', end: true }];
  if (can.supportStaff) {
    kb.push({ to: '/bilgi-bankasi/yonetim', label: 'Belge yönetimi' });
  }

  groups.push({ id: 'bilgi-bankasi', label: 'Bilgi Bankası', items: kb });

  if (can.admin) {
    groups.push({
      id: 'yonetim',
      label: 'Yönetim',
      items: [
        { to: '/yonetim/kullanicilar', label: 'Kullanıcılar' },
        { to: '/yonetim/ana-veri', label: 'Ana veri' },
        { to: '/yonetim/denetim', label: 'Denetim kaydı' },
        { to: '/yonetim/operasyon', label: 'Operasyon' },
      ],
    });
  }

  return groups;
}

/** The first task list of the user's role; there is no shared dashboard. */
export function homePathFor(me: Me): string {
  const can = abilitiesOf(me);
  if (can.sales) {
    return '/satis/siparisler';
  }

  if (can.purchasing) {
    return '/satin-alma/siparisler';
  }

  if (can.warehouseOps) {
    return '/depo/sevk';
  }

  if (can.stock) {
    return '/depo/stok';
  }

  if (can.supportStaff) {
    return '/destek/kuyruk';
  }

  if (can.admin) {
    return '/yonetim/kullanicilar';
  }

  return '/destek/talepler';
}

const ROLE_LABELS: Record<Role, string> = {
  SalesAgent: 'Satış temsilcisi',
  SalesManager: 'Satış yöneticisi',
  PurchasingAgent: 'Satın alma uzmanı',
  WarehouseOperator: 'Depo görevlisi',
  SupportAgent: 'Destek uzmanı',
  SupportManager: 'Destek yöneticisi',
  SystemAdmin: 'Sistem yöneticisi',
};

export function roleLabel(role: string): string {
  return (ROLE_LABELS as Record<string, string | undefined>)[role] ?? role;
}

const LEVEL_LABELS: Record<WarehouseLevel, string> = {
  View: 'Görüntüleme',
  Operate: 'İşlem (sevk, kabul, transfer)',
  Supervise: 'Sorumlu (stok düzeltmesi dahil)',
};

export function levelLabel(level: WarehouseLevel): string {
  return LEVEL_LABELS[level];
}
