import { render } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { SafeMarkdown, parseBlocks } from './safeMarkdown';

function renderMarkdown(source: string, mode: 'article' | 'answer' = 'article') {
  const { container } = render(<SafeMarkdown source={source} mode={mode} />);
  return container;
}

describe('SafeMarkdown — ham HTML ve bağlantılar', () => {
  it('script etiketini öğe olarak değil, düz metin olarak gösterir', () => {
    const container = renderMarkdown('Önce\n\n<script>window.hacked = true</script>\n\nSonra');
    expect(container.querySelector('script')).toBeNull();
    expect(container.textContent).toContain('<script>window.hacked = true</script>');
    expect((window as unknown as { hacked?: boolean }).hacked).toBeUndefined();
  });

  it('img onerror içeren HTML’i etkisiz metin olarak gösterir', () => {
    const container = renderMarkdown('<img src=x onerror="alert(1)">');
    expect(container.querySelector('img')).toBeNull();
    expect(container.querySelectorAll('[onerror]')).toHaveLength(0);
    expect(container.textContent).toBe('<img src=x onerror="alert(1)">');
  });

  it('javascript: bağlantısını tıklanabilir yapmaz', () => {
    const container = renderMarkdown('[buraya tıklayın](javascript:alert(document.cookie))');
    expect(container.querySelector('a')).toBeNull();
    expect(container.textContent).toContain('buraya tıklayın (javascript:alert(document.cookie)');
  });

  it('Markdown bağlantısını metin + adres olarak, çıplak adresi düz metin olarak gösterir', () => {
    const container = renderMarkdown('Bkz. [stok rehberi](https://example.test/stok) ve https://example.test/duz ile data:text/html,<b>x</b>');
    expect(container.querySelectorAll('a')).toHaveLength(0);
    expect(container.querySelector('b')).toBeNull();
    expect(container.textContent).toBe('Bkz. stok rehberi (https://example.test/stok) ve https://example.test/duz ile data:text/html,<b>x</b>');
  });

  it('HTML varlıklarını çözmeden olduğu gibi gösterir', () => {
    const container = renderMarkdown('&lt;script&gt;alert(1)&lt;/script&gt; &amp; &#x3C;b&#x3E;');
    expect(container.querySelector('script')).toBeNull();
    expect(container.textContent).toBe('&lt;script&gt;alert(1)&lt;/script&gt; &amp; &#x3C;b&#x3E;');
  });

  it('kalın metnin içindeki HTML’i de metin olarak bırakır', () => {
    const container = renderMarkdown('**<iframe src="https://evil.test"></iframe>**');
    expect(container.querySelector('iframe')).toBeNull();
    expect(container.querySelector('strong')?.textContent).toBe('<iframe src="https://evil.test"></iframe>');
  });
});

describe('SafeMarkdown — biçimler', () => {
  it('başlıkları sayfa hiyerarşisi içinde h3–h6 olarak üretir', () => {
    const container = renderMarkdown('# Bir\n\n## İki\n\n### Üç\n\n#### Dört\n\n##### Beş değil');
    expect(container.querySelector('h1, h2')).toBeNull();
    expect(container.querySelector('h3')?.textContent).toBe('Bir');
    expect(container.querySelector('h4')?.textContent).toBe('İki');
    expect(container.querySelector('h5')?.textContent).toBe('Üç');
    expect(container.querySelector('h6')?.textContent).toBe('Dört');
    expect(container.querySelector('p')?.textContent).toBe('##### Beş değil');
  });

  it('paragraf, sıralı/sırasız liste, kalın, italik ve satır içi kodu işler', () => {
    const container = renderMarkdown(
      ['Sevk miktarı **açık miktarı** aşamaz; *kalan* sonraya kalır.', '', '- `STK-409` kodu', '- ikinci madde', '', '1. Siparişi açın', '2. Miktarı girin'].join('\n'),
    );
    expect(container.querySelector('p strong')?.textContent).toBe('açık miktarı');
    expect(container.querySelector('p em')?.textContent).toBe('kalan');
    expect([...container.querySelectorAll('ul > li')].map((li) => li.textContent)).toEqual(['STK-409 kodu', 'ikinci madde']);
    expect(container.querySelector('ul code')?.textContent).toBe('STK-409');
    expect([...container.querySelectorAll('ol > li')].map((li) => li.textContent)).toEqual(['Siparişi açın', 'Miktarı girin']);
  });

  it('çitli kod bloğunu pre içinde, içeriğini biçimlendirmeden gösterir', () => {
    const container = renderMarkdown('```\n**kalın değil** <b>html değil</b>\n# başlık değil\n```\n\nSonraki paragraf');
    const pre = container.querySelector('pre');
    expect(pre?.textContent).toBe('**kalın değil** <b>html değil</b>\n# başlık değil');
    expect(pre?.querySelector('strong, b, h3')).toBeNull();
    expect(container.querySelector('p')?.textContent).toBe('Sonraki paragraf');
  });

  it('Türkçe karakterleri bozmadan korur', () => {
    const container = renderMarkdown('## Çelik dübel ğüşıİöç');
    expect(container.querySelector('h4')?.textContent).toBe('Çelik dübel ğüşıİöç');
  });
});

describe('SafeMarkdown — yanıt kipi (kısıtlı)', () => {
  it('yalnız paragraf, liste, kalın ve satır içi kod üretir', () => {
    const container = renderMarkdown(
      ['# Başlık gibi', '', '**STK-409**, miktarın *açık* miktarı aştığını gösterir.', '', '- `Açık` sütununa bakın', '', '```', 'kod bloğu', '```'].join('\n'),
      'answer',
    );
    expect(container.querySelector('h1, h2, h3, h4, h5, h6, pre, em, a')).toBeNull();
    expect(container.querySelector('strong')?.textContent).toBe('STK-409');
    expect(container.querySelector('li code')?.textContent).toBe('Açık');
    expect(container.textContent).toContain('# Başlık gibi');
    expect(container.textContent).toContain('*açık*');
    expect([...container.querySelectorAll('*')].every((el) => ['DIV', 'P', 'UL', 'OL', 'LI', 'STRONG', 'CODE'].includes(el.tagName))).toBe(true);
  });

  it('yanıttaki script ve bağlantıyı etkisiz bırakır', () => {
    const container = renderMarkdown('Yanıt <script>alert(1)</script> [kaynak](javascript:void(0)) https://example.test', 'answer');
    expect(container.querySelector('script, a')).toBeNull();
    expect(container.textContent).toContain('<script>alert(1)</script>');
    expect(container.textContent).toContain('kaynak (javascript:void(0)');
  });
});

describe('SafeMarkdown — uzun ve kötü niyetli girdi', () => {
  it('çok uzun metni doğrusal sürede işler', () => {
    const paragraph = 'Sevk miktarı açık miktarı aşamaz. '.repeat(40);
    const source = Array.from({ length: 600 }, (_, i) => `## Bölüm ${i + 1}\n\n${paragraph}`).join('\n\n');
    expect(source.length).toBeGreaterThan(800_000);
    const started = performance.now();
    const blocks = parseBlocks(source, 'article');
    const container = renderMarkdown(source);
    expect(performance.now() - started).toBeLessThan(5000);
    expect(blocks).toHaveLength(1200);
    expect(container.querySelectorAll('h4')).toHaveLength(600);
  });

  it('eşleşmeyen binlerce ayraçta takılmaz ve metni kaybetmez', () => {
    const hostile = `${'['.repeat(20_000)}${'*'.repeat(20_001)}${'`'.repeat(1)}${'[a]('.repeat(5_000)}`;
    const started = performance.now();
    const container = renderMarkdown(hostile);
    expect(performance.now() - started).toBeLessThan(5000);
    expect(container.querySelector('a, script')).toBeNull();
    expect(container.textContent?.length).toBeGreaterThan(40_000);
  });

  it('boş girdide boş çıktı üretir', () => {
    const container = renderMarkdown('   \n\n  ');
    expect(container.textContent).toBe('');
  });
});
