import { render, screen, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { BalanceStrip, purchaseBalance, salesBalance, stockBalance } from './BalanceStrip';

const salesLines = [
  { orderedQty: 40, shippedQty: 25, cancelledQty: 0, openQty: 15 },
  { orderedQty: 120, shippedQty: 120, cancelledQty: 0, openQty: 0 },
  { orderedQty: 60, shippedQty: 20, cancelledQty: 10, openQty: 30 },
];

function figures(): Record<string, string> {
  const result: Record<string, string> = {};
  for (const term of document.querySelectorAll('.balance__term')) {
    const label = term.querySelector('dt')?.textContent ?? '';
    result[label] = term.querySelector('dd')?.textContent ?? '';
  }

  return result;
}

describe('terazi şeridi', () => {
  it('sipariş = sevk edilen + iptal + açık kimliğini satırlardan toplar', () => {
    const model = salesBalance(salesLines, true);
    expect(model.total).toBe(220);
    expect(model.segments.map((s) => [s.kind, s.value])).toEqual([
      ['done', 165],
      ['cancel', 10],
      ['open', 45],
    ]);
    expect(model.segments.reduce((acc, s) => acc + s.value, 0)).toBe(model.total);
    expect(model.balanced).toBe(true);
  });

  it('rakamları metin olarak, çubuğu tam cümlelik erişilebilir adla gösterir', () => {
    render(<BalanceStrip model={salesBalance(salesLines, true)} />);

    const bar = screen.getByRole('img');
    expect(bar).toHaveAccessibleName('Sipariş 220 adet: sevk edilen 165, iptal 10, açık (rezerve) 45.');
    expect(figures()).toEqual({ Sipariş: '220', 'Sevk edilen': '165', İptal: '10', 'Açık (rezerve)': '45' });
  });

  it('dilimleri miktarla orantılı çizer ve sıfır olan dilimi çizmez', () => {
    const { container } = render(
      <BalanceStrip model={salesBalance([{ orderedQty: 450, shippedQty: 245, cancelledQty: 0, openQty: 205 }], true)} />,
    );
    const segments = [...container.querySelectorAll<HTMLElement>('.balance__seg')];
    expect(segments.map((s) => s.className)).toEqual(['balance__seg balance__seg--done', 'balance__seg balance__seg--open']);
    expect(segments.map((s) => s.style.flexGrow)).toEqual(['245', '205']);
    // The zero still appears as a figure: the identity is always spelled out in full.
    expect(figures()['İptal']).toBe('0');
  });

  it('büyük sayıları tr-TR biçiminde yazar', () => {
    render(<BalanceStrip model={purchaseBalance([{ orderedQty: 100000, receivedQty: 12500, cancelledQty: 0, openQty: 87500 }])} />);
    expect(screen.getByRole('img')).toHaveAccessibleName('Sipariş 100.000 adet: kabul edilen 12.500, iptal 0, açık 87.500.');
    expect(figures()).toMatchObject({ Sipariş: '100.000', 'Kabul edilen': '12.500', Açık: '87.500' });
  });

  it('stok için eldeki = rezerve + kullanılabilir kimliğini gösterir', () => {
    render(<BalanceStrip model={stockBalance({ onHand: 150, reserved: 120, available: 30 })} />);
    expect(screen.getByRole('img')).toHaveAccessibleName('Eldeki 150 adet: rezerve 120, kullanılabilir 30.');
    expect(figures()).toEqual({ Eldeki: '150', Rezerve: '120', Kullanılabilir: '30' });
  });

  it('parçalar toplamı tutmuyorsa bunu gizlemez, uyarır', () => {
    const model = salesBalance([{ orderedQty: 10, shippedQty: 3, cancelledQty: 0, openQty: 5 }], true);
    expect(model.balanced).toBe(false);
    render(<BalanceStrip model={model} />);
    expect(within(screen.getByRole('alert')).getByText(/Miktarlar birbirini tutmuyor/)).toBeInTheDocument();
  });

  it('satırı olmayan taslakta boş çubuk ve sıfır rakamlar gösterir', () => {
    const { container } = render(<BalanceStrip model={salesBalance([], false)} />);
    expect(container.querySelectorAll('.balance__seg')).toHaveLength(0);
    expect(figures()).toEqual({ Sipariş: '0', 'Sevk edilen': '0', İptal: '0', Açık: '0' });
    expect(screen.queryByRole('alert')).toBeNull();
  });
});
