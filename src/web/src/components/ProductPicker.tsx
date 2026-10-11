import { useState } from 'react';
import type { KeyboardEvent } from 'react';
import { inventoryApi, masterApi } from '../api/endpoints';
import { asProblem } from '../api/problem';
import { formatMoney, formatQuantity } from '../lib/format';
import { useApiQuery } from '../lib/query';
import { fieldDomId } from './forms';
import { ProblemAlert } from './states';
import { Button, Code } from './ui';

export interface PickedProduct {
  productId: string;
  sku: string;
  name: string;
  unit: string;
  /** Server list price; present only for roles that may read prices. */
  listPrice?: number | undefined;
  /** Quantity available in the source warehouse (stock source only). */
  available?: number | undefined;
  onHand?: number | undefined;
  reserved?: number | undefined;
}

interface ProductPickerProps {
  formId: string;
  /** catalog: active products (/api/products). stock: balances of one warehouse, with availability. */
  source: { kind: 'catalog' } | { kind: 'stock'; warehouseId: string };
  /** Products already on the document; a product can appear on a document only once. */
  excluded: ReadonlySet<string>;
  onPick: (product: PickedProduct) => void;
  label?: string;
}

const PAGE = 20;

/**
 * Search-and-add product picker. Results are plain buttons in a list: no custom combobox, so it
 * works the same with keyboard, touch and screen readers. Enter in the search box searches; it
 * never submits the surrounding document form.
 */
export function ProductPicker({ formId, source, excluded, onPick, label = 'Ürün ekle: SKU veya ad ile ara' }: ProductPickerProps) {
  const [text, setText] = useState('');
  const [term, setTerm] = useState<string | null>(null);
  const inputId = fieldDomId(formId, 'urun-ara');
  const scope = source.kind === 'stock' ? source.warehouseId : 'catalog';

  const query = useApiQuery<PickedProduct[]>(
    ['product-picker', scope, term],
    async (signal) => {
      const search = term !== null && term.length > 0 ? term : undefined;
      if (source.kind === 'stock') {
        const page = await inventoryApi.balances({ warehouseId: source.warehouseId, search, limit: PAGE }, signal);
        return page.items.map((b) => ({
          productId: b.productId,
          sku: b.sku,
          name: b.name,
          unit: b.unit,
          available: b.available,
          onHand: b.onHand,
          reserved: b.reserved,
        }));
      }

      const page = await masterApi.products({ search, active: true, limit: PAGE }, signal);
      return page.items.map((p) => ({ productId: p.id, sku: p.sku, name: p.name, unit: p.unit, listPrice: p.listPrice }));
    },
    { enabled: term !== null },
  );

  const run = () => setTerm(text.trim());
  const onKeyDown = (event: KeyboardEvent<HTMLInputElement>) => {
    if (event.key === 'Enter') {
      event.preventDefault();
      run();
    }
  };

  const results = query.data ?? [];
  return (
    <div className="picker">
      <div className="field">
        <label htmlFor={inputId}>{label}</label>
        <div className="actions">
          <input
            id={inputId}
            name="productSearch"
            type="search"
            className="input measure-form"
            autoComplete="off"
            spellCheck={false}
            maxLength={64}
            value={text}
            placeholder="Örn. ELK-NYM veya kablo…"
            onChange={(event) => setText(event.target.value)}
            onKeyDown={onKeyDown}
            aria-describedby={`${inputId}-ipucu`}
          />
          <Button onClick={run} pending={term !== null && query.isFetching} pendingLabel="Aranıyor…">
            Ürün ara
          </Button>
        </div>
        <span className="hint" id={`${inputId}-ipucu`}>
          {source.kind === 'stock'
            ? 'Seçili depodaki bakiyelerde arar. Boş bırakıp aratırsanız ilk kayıtlar listelenir.'
            : 'Aktif ürünlerde arar. Boş bırakıp aratırsanız ilk kayıtlar listelenir.'}
        </span>
      </div>

      <div aria-live="polite">
        {term !== null && query.isPending && (
          <p className="picker__status" role="status" aria-busy="true">
            Ürünler aranıyor…
          </p>
        )}
        {term !== null && query.isError && (
          <ProblemAlert problem={asProblem(query.error)} title="Ürünler aranamadı" onRetry={() => void query.refetch()} headingLevel={3} />
        )}
        {term !== null && query.data !== undefined && results.length === 0 && (
          <p className="picker__status">
            {term.length > 0 ? `“${term}” ile eşleşen ürün yok. SKU’nun bir bölümünü ya da ürün adından bir sözcük deneyin.` : 'Listelenecek ürün yok.'}
          </p>
        )}
        {results.length > 0 && (
          <>
            <ul className="picker__results" aria-label="Arama sonuçları">
              {results.map((product) => {
                const added = excluded.has(product.productId);
                return (
                  <li key={product.productId}>
                    <button type="button" className="picker__option" disabled={added} onClick={() => onPick(product)}>
                      <Code>{product.sku}</Code>
                      <span className="wrap">
                        {product.name} <span className="muted">· {product.unit}</span>
                      </span>
                      <span className="num">
                        {added
                          ? 'Belgede var'
                          : product.available !== undefined
                            ? `Kullanılabilir ${formatQuantity(product.available)}`
                            : product.listPrice !== undefined
                              ? formatMoney(product.listPrice)
                              : 'Ekle'}
                      </span>
                    </button>
                  </li>
                );
              })}
            </ul>
            {results.length === PAGE && <p className="picker__status">İlk {PAGE} sonuç gösteriliyor; aramayı daraltın.</p>}
          </>
        )}
      </div>
    </div>
  );
}
