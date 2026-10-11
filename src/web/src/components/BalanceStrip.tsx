import { formatQuantity } from '../lib/format';
import { Icon } from './Icon';

// Terazi şeridi — the product's signature: one proportional bar whose segments always fill it
// exactly, with the figures underneath. It never appears without its numbers.

export type SegmentKind = 'done' | 'cancel' | 'open' | 'free';

export interface BalanceSegment {
  kind: SegmentKind;
  label: string;
  value: number;
}

export interface BalanceModel {
  totalLabel: string;
  total: number;
  segments: BalanceSegment[];
  /** Full-sentence description of the bar for assistive technology. */
  description: string;
  /** False when the parts do not add up to the total: shown as a warning, never hidden. */
  balanced: boolean;
}

interface OrderQuantities {
  orderedQty: number;
  cancelledQty: number;
  openQty: number;
}

function build(totalLabel: string, total: number, segments: BalanceSegment[]): BalanceModel {
  const sum = segments.reduce((acc, segment) => acc + segment.value, 0);
  const parts = segments.map((segment) => `${segment.label.toLocaleLowerCase('tr-TR')} ${formatQuantity(segment.value)}`).join(', ');
  return {
    totalLabel,
    total,
    segments,
    description: `${totalLabel} ${formatQuantity(total)} adet: ${parts}.`,
    balanced: sum === total && segments.every((segment) => segment.value >= 0),
  };
}

function sumOf<T>(rows: readonly T[], pick: (row: T) => number): number {
  return rows.reduce((acc, row) => acc + pick(row), 0);
}

/** Sipariş = Sevk edilen + İptal + Açık (rezerve). */
export function salesBalance(lines: readonly (OrderQuantities & { shippedQty: number })[], reserved: boolean): BalanceModel {
  return build('Sipariş', sumOf(lines, (l) => l.orderedQty), [
    { kind: 'done', label: 'Sevk edilen', value: sumOf(lines, (l) => l.shippedQty) },
    { kind: 'cancel', label: 'İptal', value: sumOf(lines, (l) => l.cancelledQty) },
    { kind: 'open', label: reserved ? 'Açık (rezerve)' : 'Açık', value: sumOf(lines, (l) => l.openQty) },
  ]);
}

/** Sipariş = Kabul edilen + İptal + Açık. */
export function purchaseBalance(lines: readonly (OrderQuantities & { receivedQty: number })[]): BalanceModel {
  return build('Sipariş', sumOf(lines, (l) => l.orderedQty), [
    { kind: 'done', label: 'Kabul edilen', value: sumOf(lines, (l) => l.receivedQty) },
    { kind: 'cancel', label: 'İptal', value: sumOf(lines, (l) => l.cancelledQty) },
    { kind: 'open', label: 'Açık', value: sumOf(lines, (l) => l.openQty) },
  ]);
}

/** Eldeki = Rezerve + Kullanılabilir. */
export function stockBalance(balance: { onHand: number; reserved: number; available: number }): BalanceModel {
  return build('Eldeki', balance.onHand, [
    { kind: 'open', label: 'Rezerve', value: balance.reserved },
    { kind: 'free', label: 'Kullanılabilir', value: balance.available },
  ]);
}

export function BalanceStrip({ model }: { model: BalanceModel }) {
  const visible = model.segments.filter((segment) => segment.value > 0);
  return (
    <div>
      <div className={visible.length === 0 ? 'balance__bar balance__bar--empty' : 'balance__bar'} role="img" aria-label={model.description}>
        {visible.map((segment) => (
          <span key={segment.kind} className={`balance__seg balance__seg--${segment.kind}`} style={{ flex: `${segment.value} 0 0` }} />
        ))}
      </div>
      <dl className="balance__eq">
        <div className="balance__term">
          <dt>{model.totalLabel}</dt>
          <dd>{formatQuantity(model.total)}</dd>
        </div>
        {model.segments.map((segment, index) => (
          <div className="balance__term" data-op={index === 0 ? '=' : '+'} key={segment.kind}>
            <dt>
              <span className={`swatch swatch--${segment.kind}`} aria-hidden="true" />
              {segment.label}
            </dt>
            <dd>{formatQuantity(segment.value)}</dd>
          </div>
        ))}
      </dl>
      {!model.balanced && (
        <p className="field-error balance__warn" role="alert">
          <Icon name="warn" />
          Miktarlar birbirini tutmuyor: parçaların toplamı {model.totalLabel.toLocaleLowerCase('tr-TR')} miktarına eşit değil. Sistem
          yöneticinize haber verin.
        </p>
      )}
    </div>
  );
}
