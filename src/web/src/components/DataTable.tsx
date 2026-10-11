import type { ReactNode } from 'react';

export interface Column<T> {
  id: string;
  header: string;
  cell: (row: T, index: number) => ReactNode;
  /** num: right-aligned tabular figures · code: plate type · date: tabular · name: wraps, leads the stacked row. */
  kind?: 'text' | 'num' | 'code' | 'date' | 'name';
  /** The cell that identifies the row (rendered as a row header). */
  rowHeader?: boolean;
  /** Keep the header for assistive technology but hide it visually (action columns). */
  headerHidden?: boolean;
}

interface FooterSpec {
  label: string;
  /** How many leading columns the label spans. */
  span: number;
  cells: Record<string, ReactNode>;
}

interface DataTableProps<T> {
  caption: ReactNode;
  captionNote?: ReactNode;
  captionHidden?: boolean;
  columns: Column<T>[];
  rows: readonly T[];
  rowKey: (row: T, index: number) => string;
  /**
   * stacked: below 720 px every row becomes a labelled stack (no column is dropped).
   * scroll: stays tabular inside its own focusable, horizontally scrollable region with a sticky first column.
   */
  layout?: 'stacked' | 'scroll';
  footer?: FooterSpec | undefined;
  /** Accessible name of the scroll region (scroll layout). */
  scrollLabel?: string;
}

const KIND_CLASS: Record<NonNullable<Column<unknown>['kind']>, string | undefined> = {
  text: undefined,
  num: 'num',
  code: 'code',
  date: 'date',
  name: 'name',
};

/**
 * A real <table>. ARIA roles are stated explicitly because the stacked layout changes `display`,
 * which would otherwise strip table semantics in some browsers.
 */
export function DataTable<T>({
  caption,
  captionNote,
  captionHidden = false,
  columns,
  rows,
  rowKey,
  layout = 'stacked',
  footer,
  scrollLabel,
}: DataTableProps<T>) {
  const table = (
    <table className={layout === 'stacked' ? 'stacked' : undefined} role="table">
      <caption className={captionHidden ? 'sr-only' : undefined}>
        {caption}
        {captionNote !== undefined && <small>{captionNote}</small>}
      </caption>
      <thead role="rowgroup">
        <tr role="row">
          {columns.map((column) => (
            <th key={column.id} scope="col" role="columnheader" className={column.kind === 'num' ? 'num' : undefined}>
              {column.headerHidden === true ? <span className="sr-only">{column.header}</span> : column.header}
            </th>
          ))}
        </tr>
      </thead>
      <tbody role="rowgroup">
        {rows.map((row, index) => (
          <tr key={rowKey(row, index)} role="row">
            {columns.map((column) => {
              const className = KIND_CLASS[column.kind ?? 'text'];
              return column.rowHeader === true ? (
                <th key={column.id} scope="row" role="rowheader" className={className}>
                  {column.cell(row, index)}
                </th>
              ) : (
                <td key={column.id} role="cell" className={className} data-label={column.header}>
                  {column.cell(row, index)}
                </td>
              );
            })}
          </tr>
        ))}
      </tbody>
      {footer !== undefined && (
        <tfoot role="rowgroup">
          <tr role="row">
            <th scope="row" role="rowheader" colSpan={footer.span}>
              {footer.label}
            </th>
            {columns.slice(footer.span).map((column) => (
              <td key={column.id} role="cell" className={KIND_CLASS[column.kind ?? 'text']} data-label={column.header}>
                {footer.cells[column.id]}
              </td>
            ))}
          </tr>
        </tfoot>
      )}
    </table>
  );

  if (layout === 'scroll') {
    return (
      <>
        <div className="scroller" role="region" aria-label={scrollLabel ?? 'Tablo'} tabIndex={0}>
          {table}
        </div>
        <p className="scroll-hint">Tablo yana kaydırılabilir; ilk sütun sabit kalır.</p>
      </>
    );
  }

  return table;
}
