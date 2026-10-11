import { describe, expect, it } from 'vitest';
import {
  formatDate,
  formatDateTime,
  formatDecimal,
  formatMoney,
  formatQuantity,
  formatSigned,
  istanbulDay,
  istanbulDayStart,
  parseMoney,
  parseQuantity,
  parseServerDate,
  parseSignedQuantity,
} from './format';

// Non-breaking spaces that ICU may insert are normalised so the assertions read naturally.
const SPACES = new RegExp(`[${String.fromCharCode(0xa0, 0x202f)}]`, 'g');
const plain = (text: string) => text.replace(SPACES, ' ');

describe('tr-TR biçimleyiciler', () => {
  it('miktarları binlik ayırıcıyla yazar', () => {
    expect(formatQuantity(0)).toBe('0');
    expect(formatQuantity(450)).toBe('450');
    expect(formatQuantity(100000)).toBe('100.000');
    expect(formatQuantity(1234567)).toBe('1.234.567');
  });

  it('işaretli değişimleri artı/eksi ile yazar', () => {
    expect(formatSigned(90)).toBe('+90');
    expect(formatSigned(-80)).toBe('-80');
    expect(formatSigned(0)).toBe('0');
    expect(formatSigned(-12500)).toBe('-12.500');
  });

  it('tutarları TRY olarak, virgüllü ondalıkla yazar', () => {
    expect(plain(formatMoney(4850))).toBe('₺4.850,00');
    expect(plain(formatMoney(142.5))).toBe('₺142,50');
    expect(plain(formatMoney(236190))).toBe('₺236.190,00');
    expect(plain(formatMoney(0))).toBe('₺0,00');
    expect(formatDecimal(1234.5)).toBe('1.234,50');
  });

  it('tarihleri makinenin saat diliminden bağımsız olarak İstanbul saatiyle yazar', () => {
    expect(formatDateTime('2026-10-09T11:12:00Z')).toBe('9.10.2026 14:12');
    expect(formatDateTime('2026-10-09T14:12:00+03:00')).toBe('9.10.2026 14:12');
    // 21:30 UTC is already the next calendar day in Istanbul.
    expect(formatDateTime('2026-12-31T21:30:00Z')).toBe('1.01.2027 00:30');
    expect(formatDate('2026-12-31T21:30:00Z')).toBe('1.01.2027');
    expect(formatDateTime(undefined)).toBe('—');
    expect(formatDateTime('tarih değil')).toBe('—');
  });

  it('saat dilimi taşımayan sunucu zamanını UTC kabul eder', () => {
    expect(parseServerDate('2026-10-09T11:12:00').toISOString()).toBe('2026-10-09T11:12:00.000Z');
    expect(parseServerDate('2026-10-09T11:12:00.1234567').getTime()).toBe(Date.parse('2026-10-09T11:12:00.123Z'));
    expect(formatDateTime('2026-10-09T11:12:00')).toBe('9.10.2026 14:12');
  });

  it('İstanbul gününü ve gün başlangıcını üretir', () => {
    expect(istanbulDay(new Date('2026-12-31T21:30:00Z'))).toBe('2027-01-01');
    expect(istanbulDayStart('2026-10-09')).toBe('2026-10-09T00:00:00+03:00');
    expect(istanbulDayStart('2026-10-31', 1)).toBe('2026-11-01T00:00:00+03:00');
    expect(istanbulDayStart('2026-12-31', 1)).toBe('2027-01-01T00:00:00+03:00');
    expect(istanbulDayStart('31.12.2026')).toBeUndefined();
  });
});

describe('girdi ayrıştırıcılar', () => {
  it('miktar yalnız tam sayıdır', () => {
    expect(parseQuantity('40')).toBe(40);
    expect(parseQuantity(' 7 ')).toBe(7);
    expect(parseQuantity('0')).toBe(0);
    for (const bad of ['', '1.0', '1,5', '-3', '+3', '1e3', 'on', '1 000']) {
      expect(parseQuantity(bad)).toBeNull();
    }
  });

  it('işaretli miktar artı, eksi ve tipografik eksiyi kabul eder', () => {
    expect(parseSignedQuantity('+5')).toBe(5);
    expect(parseSignedQuantity('-3')).toBe(-3);
    expect(parseSignedQuantity(`${String.fromCharCode(0x2212)}3`)).toBe(-3);
    expect(parseSignedQuantity('12')).toBe(12);
    expect(parseSignedQuantity('1.5')).toBeNull();
    expect(parseSignedQuantity('--3')).toBeNull();
  });

  it('tutarı Türkçe yazımla ve noktalı ondalıkla okur, ikiden fazla ondalığı reddeder', () => {
    expect(parseMoney('142,50')).toBe(142.5);
    expect(parseMoney('1.234,50')).toBe(1234.5);
    expect(parseMoney('1.234')).toBe(1234);
    expect(parseMoney('1234.5')).toBe(1234.5);
    expect(parseMoney('0')).toBe(0);
    expect(parseMoney('₺ 96,00')).toBe(96);
    for (const bad of ['', '-5', '1,234', '12,345', '1.2.3', 'abc', '1,2,3']) {
      expect(parseMoney(bad)).toBeNull();
    }
  });
});
