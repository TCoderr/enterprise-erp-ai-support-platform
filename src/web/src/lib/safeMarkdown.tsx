import type { ReactNode } from 'react';

/**
 * A deliberately small Markdown renderer for knowledge-base articles and assistance answers.
 *
 * It builds React elements only — no HTML string is ever produced or injected — so raw HTML in the
 * source is shown as literal text. Links are never made clickable: `[text](url)` is rendered as the
 * text followed by the URL in plain text, and bare URLs stay plain text. Content from the knowledge
 * base or from an answer provider can therefore neither run script (`javascript:`, `data:`) nor
 * navigate the user away.
 *
 *  - 'article': headings (# … #### → h3 … h6), paragraphs, lists, **bold**, *italic*, `code`, fenced code.
 *  - 'answer':  paragraphs, lists, **bold** and `code` only; everything else stays literal text.
 */
export type MarkdownMode = 'article' | 'answer';

export type Block =
  | { type: 'heading'; level: 1 | 2 | 3 | 4; text: string }
  | { type: 'paragraph'; text: string }
  | { type: 'list'; ordered: boolean; items: string[] }
  | { type: 'code'; text: string };

const FENCE = /^ {0,3}```/;
const HEADING = /^ {0,3}(#{1,4})[ \t]+(.*)$/;
const BULLET = /^ {0,3}[-*+][ \t]+(.*)$/;
const NUMBERED = /^ {0,3}\d{1,9}[.)][ \t]+(.*)$/;

function isBlank(line: string): boolean {
  return line.trim().length === 0;
}

export function parseBlocks(source: string, mode: MarkdownMode): Block[] {
  const article = mode === 'article';
  const lines = source.replace(/\r\n?/g, '\n').split('\n');
  const blocks: Block[] = [];
  let i = 0;

  const startsBlock = (line: string): boolean =>
    BULLET.test(line) || NUMBERED.test(line) || (article && (FENCE.test(line) || HEADING.test(line)));

  while (i < lines.length) {
    const line = lines[i] ?? '';

    if (isBlank(line)) {
      i += 1;
      continue;
    }

    if (article && FENCE.test(line)) {
      const body: string[] = [];
      i += 1;
      while (i < lines.length && !FENCE.test(lines[i] ?? '')) {
        body.push(lines[i] ?? '');
        i += 1;
      }

      i += 1; // closing fence (or end of input)
      blocks.push({ type: 'code', text: body.join('\n') });
      continue;
    }

    const heading = article ? HEADING.exec(line) : null;
    if (heading) {
      const level = (heading[1] ?? '#').length as 1 | 2 | 3 | 4;
      blocks.push({ type: 'heading', level, text: (heading[2] ?? '').replace(/[ \t]+#+[ \t]*$/, '').trim() });
      i += 1;
      continue;
    }

    const bullet = BULLET.exec(line);
    const numbered = bullet ? null : NUMBERED.exec(line);
    if (bullet || numbered) {
      const ordered = numbered !== null;
      const pattern = ordered ? NUMBERED : BULLET;
      const items: string[] = [];
      while (i < lines.length) {
        const current = lines[i] ?? '';
        const item = pattern.exec(current);
        if (item) {
          items.push((item[1] ?? '').trim());
          i += 1;
        } else if (!isBlank(current) && /^[ \t]+\S/.test(current) && !startsBlock(current) && items.length > 0) {
          // Indented continuation of the previous item.
          items[items.length - 1] = `${items[items.length - 1] ?? ''} ${current.trim()}`;
          i += 1;
        } else {
          break;
        }
      }

      blocks.push({ type: 'list', ordered, items });
      continue;
    }

    const text: string[] = [line.trim()];
    i += 1;
    while (i < lines.length) {
      const current = lines[i] ?? '';
      if (isBlank(current) || startsBlock(current)) {
        break;
      }

      text.push(current.trim());
      i += 1;
    }

    blocks.push({ type: 'paragraph', text: text.join(' ') });
  }

  return blocks;
}

/**
 * Inline formatting in a single left-to-right pass. Each delimiter search that fails is remembered,
 * so pathological input (thousands of unmatched `[` or `*`) stays linear.
 */
export function renderInline(text: string, mode: MarkdownMode, allowBold = true): ReactNode[] {
  const out: ReactNode[] = [];
  let plain = '';
  let key = 0;
  let noCode = false;
  let noBold = !allowBold;
  let noItalic = mode !== 'article';
  let noLink = false;
  let linkMid = 0;

  const flush = () => {
    if (plain.length > 0) {
      out.push(plain);
      plain = '';
    }
  };

  let i = 0;
  while (i < text.length) {
    const ch = text[i];

    if (ch === '`' && !noCode) {
      const end = text.indexOf('`', i + 1);
      if (end === -1) {
        noCode = true;
      } else if (end > i + 1) {
        flush();
        out.push(<code key={key++}>{text.slice(i + 1, end)}</code>);
        i = end + 1;
        continue;
      }
    }

    if (ch === '*' && text[i + 1] === '*' && !noBold) {
      const end = text.indexOf('**', i + 2);
      if (end === -1) {
        noBold = true;
      } else if (end > i + 2) {
        flush();
        out.push(<strong key={key++}>{renderInline(text.slice(i + 2, end), mode, false)}</strong>);
        i = end + 2;
        continue;
      }
    }

    if (ch === '*' && text[i + 1] !== '*' && !noItalic) {
      const end = text.indexOf('*', i + 1);
      if (end === -1) {
        noItalic = true;
      } else if (end > i + 1 && text[i + 1] !== ' ' && text[end - 1] !== ' ') {
        flush();
        out.push(<em key={key++}>{text.slice(i + 1, end)}</em>);
        i = end + 1;
        continue;
      }
    }

    if (ch === '[' && !noLink) {
      if (linkMid <= i) {
        linkMid = text.indexOf('](', i + 1);
      }

      if (linkMid === -1) {
        noLink = true;
      } else if (text.lastIndexOf('[', linkMid - 1) === i) {
        const end = text.indexOf(')', linkMid + 2);
        if (end === -1) {
          noLink = true;
        } else {
          // Never an anchor: the label, then the target spelled out so the reader sees where it points.
          const label = text.slice(i + 1, linkMid);
          const url = text.slice(linkMid + 2, end).trim();
          plain += url.length > 0 ? `${label} (${url})` : label;
          i = end + 1;
          continue;
        }
      }
    }

    plain += ch;
    i += 1;
  }

  flush();
  return out;
}

interface SafeMarkdownProps {
  source: string;
  mode?: MarkdownMode;
  className?: string;
}

const HEADING_TAGS = { 1: 'h3', 2: 'h4', 3: 'h5', 4: 'h6' } as const;

export function SafeMarkdown({ source, mode = 'article', className }: SafeMarkdownProps) {
  const blocks = parseBlocks(source, mode);
  return (
    <div className={className ?? (mode === 'answer' ? 'prose prose--compact' : 'prose')}>
      {blocks.map((block, index) => {
        switch (block.type) {
          case 'heading': {
            const Tag = HEADING_TAGS[block.level];
            return <Tag key={index}>{renderInline(block.text, mode)}</Tag>;
          }
          case 'code':
            return (
              <pre key={index} tabIndex={0}>
                <code>{block.text}</code>
              </pre>
            );
          case 'list': {
            const items = block.items.map((item, itemIndex) => <li key={itemIndex}>{renderInline(item, mode)}</li>);
            return block.ordered ? <ol key={index}>{items}</ol> : <ul key={index}>{items}</ul>;
          }
          default:
            return <p key={index}>{renderInline(block.text, mode)}</p>;
        }
      })}
    </div>
  );
}
