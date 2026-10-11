// All numbers and dates are formatted for Turkish readers in Istanbul time, independent of the
// machine's own locale and time zone.

const LOCALE = 'tr-TR';
const ZONE = 'Europe/Istanbul';

const quantityFormat = new Intl.NumberFormat(LOCALE, { maximumFractionDigits: 0 });
const signedFormat = new Intl.NumberFormat(LOCALE, { maximumFractionDigits: 0, signDisplay: 'exceptZero' });
const moneyFormat = new Intl.NumberFormat(LOCALE, { style: 'currency', currency: 'TRY' });
const decimalFormat = new Intl.NumberFormat(LOCALE, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
const dateTimeFormat = new Intl.DateTimeFormat(LOCALE, { dateStyle: 'short', timeStyle: 'short', timeZone: ZONE });
const dateFormat = new Intl.DateTimeFormat(LOCALE, { dateStyle: 'short', timeZone: ZONE });
const isoDayFormat = new Intl.DateTimeFormat('en-CA', { timeZone: ZONE, year: 'numeric', month: '2-digit', day: '2-digit' });

export function formatQuantity(value: number): string {
  return quantityFormat.format(value);
}

/** +90 / -80 / 0 — for journal deltas. */
export function formatSigned(value: number): string {
  return signedFormat.format(value);
}

export function formatMoney(value: number): string {
  return moneyFormat.format(value);
}

/** 1.234,50 without a currency sign — for values typed back into an input. */
export function formatDecimal(value: number): string {
  return decimalFormat.format(value);
}

/**
 * The API writes UTC instants. A value without an offset is still UTC (the server never sends
 * local times), so it is read as such instead of in the browser's zone.
 */
export function parseServerDate(value: string): Date {
  const hasZone = /(?:Z|[+-]\d{2}:?\d{2})$/i.test(value);
  return new Date(hasZone ? value : `${value}Z`);
}

export function formatDateTime(value: string | undefined): string {
  if (value === undefined) {
    return '—';
  }

  const date = parseServerDate(value);
  return Number.isNaN(date.getTime()) ? '—' : dateTimeFormat.format(date);
}

export function formatDate(value: string | undefined): string {
  if (value === undefined) {
    return '—';
  }

  const date = parseServerDate(value);
  return Number.isNaN(date.getTime()) ? '—' : dateFormat.format(date);
}

/** Today's calendar day in Istanbul as YYYY-MM-DD (value format of a date input). */
export function istanbulDay(now: Date): string {
  return isoDayFormat.format(now);
}

/**
 * Start of an Istanbul calendar day as an ISO instant with offset. Türkiye has been on UTC+03:00
 * all year since 2016, so the offset is fixed.
 */
export function istanbulDayStart(day: string, addDays = 0): string | undefined {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(day);
  if (!match) {
    return undefined;
  }

  const utc = new Date(Date.UTC(Number(match[1]), Number(match[2]) - 1, Number(match[3]) + addDays));
  if (Number.isNaN(utc.getTime())) {
    return undefined;
  }

  const y = utc.getUTCFullYear().toString().padStart(4, '0');
  const m = (utc.getUTCMonth() + 1).toString().padStart(2, '0');
  const d = utc.getUTCDate().toString().padStart(2, '0');
  return `${y}-${m}-${d}T00:00:00+03:00`;
}

/** A whole number of units typed by the user; rejects decimals, signs and separators. */
export function parseQuantity(text: string): number | null {
  const trimmed = text.trim();
  if (!/^\d{1,9}$/.test(trimmed)) {
    return null;
  }

  return Number(trimmed);
}

// U+2212, the minus sign some keyboards and spreadsheets produce instead of the hyphen-minus.
const TYPOGRAPHIC_MINUS = String.fromCharCode(0x2212);

/** A signed whole number (stock adjustment): "+5", "-3", "−3", "12". */
export function parseSignedQuantity(text: string): number | null {
  const trimmed = text.trim().replace(TYPOGRAPHIC_MINUS, '-');
  if (!/^[+-]?\d{1,9}$/.test(trimmed)) {
    return null;
  }

  return Number(trimmed);
}

/**
 * An amount in TRY typed the Turkish way ("1.234,50", "1234,5") or with a decimal point ("1234.50").
 * Returns null when it is not a non-negative amount with at most two decimals.
 */
export function parseMoney(text: string): number | null {
  const trimmed = text.trim().replace(/\s/g, '').replace(/₺|TL$/i, '');
  let normalized: string;
  if (/^\d{1,3}(\.\d{3})*(,\d{1,2})?$/.test(trimmed) || /^\d+(,\d{1,2})?$/.test(trimmed)) {
    normalized = trimmed.replace(/\./g, '').replace(',', '.');
  } else if (/^\d+\.\d{1,2}$/.test(trimmed)) {
    normalized = trimmed;
  } else {
    return null;
  }

  const value = Number(normalized);
  return Number.isFinite(value) && value >= 0 ? value : null;
}
