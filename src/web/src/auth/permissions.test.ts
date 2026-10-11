import { describe, expect, it } from 'vitest';
import type { Role } from '../api/types';
import { ANKARA, ISTANBUL, makeMe } from '../test/helpers';
import { abilitiesOf, homePathFor, navigationFor } from './permissions';

function groupsOf(roles: Role[], warehouses = [ANKARA]): Record<string, string[]> {
  const nav = navigationFor(makeMe({ roles, warehouses }));
  return Object.fromEntries(nav.map((group) => [group.label, group.items.map((item) => item.label)]));
}

describe('role göre gezinme (docs/02 §2.4)', () => {
  it('sistem yöneticisi Satış, Satın Alma ve Depo öğelerini görmez', () => {
    const groups = groupsOf(['SystemAdmin'], []);
    expect(Object.keys(groups)).toEqual(['Destek', 'Bilgi Bankası', 'Yönetim']);
    expect(groups['Yönetim']).toEqual(['Kullanıcılar', 'Ana veri', 'Denetim kaydı', 'Operasyon']);
    expect(groups).not.toHaveProperty('Satış');
    expect(groups).not.toHaveProperty('Satın Alma');
    expect(groups).not.toHaveProperty('Depo');
  });

  it('sistem yöneticisine depo yetkisi verilse bile stok ekranları açılmaz (rol + depo birlikte aranır)', () => {
    const groups = groupsOf(['SystemAdmin'], [ANKARA]);
    expect(groups).not.toHaveProperty('Depo');
  });

  it('depo görevlisi yalnız Depo ile Destek ve Bilgi Bankası gruplarını görür', () => {
    const groups = groupsOf(['WarehouseOperator'], [ANKARA]);
    expect(Object.keys(groups)).toEqual(['Depo', 'Destek', 'Bilgi Bankası']);
    expect(groups['Depo']).toEqual(['Stok bakiyeleri', 'Hareketler', 'Rezervasyonlar', 'Sevk bekleyenler', 'Kabul bekleyenler', 'Transfer', 'Düzeltme']);
    expect(groups['Destek']).toEqual(['Taleplerim', 'Yeni talep']);
    expect(groups['Bilgi Bankası']).toEqual(['Makaleler']);
  });

  it('düzeltme yalnız Sorumlu, sevk/kabul/transfer yalnız İşlem seviyesiyle sunulur', () => {
    expect(groupsOf(['WarehouseOperator'], [ISTANBUL])['Depo']).toEqual([
      'Stok bakiyeleri',
      'Hareketler',
      'Rezervasyonlar',
      'Sevk bekleyenler',
      'Kabul bekleyenler',
      'Transfer',
    ]);
    expect(groupsOf(['WarehouseOperator'], [{ ...ANKARA, level: 'View' }])['Depo']).toEqual(['Stok bakiyeleri', 'Hareketler', 'Rezervasyonlar']);
  });

  it('satış rolleri Satış grubunu ve yalnız stok okuma öğelerini görür', () => {
    const groups = groupsOf(['SalesAgent'], [{ ...ANKARA, level: 'View' }]);
    expect(Object.keys(groups)).toEqual(['Satış', 'Depo', 'Destek', 'Bilgi Bankası']);
    expect(groups['Satış']).toEqual(['Siparişler', 'Yeni sipariş']);
    expect(groups['Depo']).toEqual(['Stok bakiyeleri', 'Hareketler', 'Rezervasyonlar']);
  });

  it('satın alma uzmanı Satın Alma grubunu görür, Satış grubunu görmez', () => {
    const groups = groupsOf(['PurchasingAgent']);
    expect(groups).toHaveProperty('Satın Alma');
    expect(groups).not.toHaveProperty('Satış');
    expect(groups).not.toHaveProperty('Yönetim');
  });

  it('destek personeli kuyruğu ve belge yönetimini görür; stok ve ticari grupları görmez', () => {
    const groups = groupsOf(['SupportAgent'], []);
    expect(Object.keys(groups)).toEqual(['Destek', 'Bilgi Bankası']);
    expect(groups['Destek']).toEqual(['Taleplerim', 'Yeni talep', 'Talep kuyruğu']);
    expect(groups['Bilgi Bankası']).toEqual(['Makaleler', 'Belge yönetimi']);
  });

  it('rolü olmayan kullanıcı yalnız Destek ve Bilgi Bankası görür', () => {
    expect(Object.keys(groupsOf([], []))).toEqual(['Destek', 'Bilgi Bankası']);
  });
});

describe('açılış sayfası ve yetenekler', () => {
  it('açılış sayfası rolün ilk görev listesidir', () => {
    expect(homePathFor(makeMe({ roles: ['SalesAgent'] }))).toBe('/satis/siparisler');
    expect(homePathFor(makeMe({ roles: ['PurchasingAgent'] }))).toBe('/satin-alma/siparisler');
    expect(homePathFor(makeMe({ roles: ['WarehouseOperator'] }))).toBe('/depo/sevk');
    expect(homePathFor(makeMe({ roles: ['SupportManager'], warehouses: [] }))).toBe('/destek/kuyruk');
    expect(homePathFor(makeMe({ roles: ['SystemAdmin'], warehouses: [] }))).toBe('/yonetim/kullanicilar');
    expect(homePathFor(makeMe({ roles: [], warehouses: [] }))).toBe('/destek/talepler');
  });

  it('yayınlama yalnız destek yöneticisine sunulur', () => {
    expect(abilitiesOf(makeMe({ roles: ['SupportAgent'] })).supportManager).toBe(false);
    expect(abilitiesOf(makeMe({ roles: ['SupportManager'] })).supportManager).toBe(true);
  });
});
