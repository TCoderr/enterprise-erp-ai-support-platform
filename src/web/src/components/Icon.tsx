import type { ReactNode } from 'react';

// One 16 px grid, 1.5 px stroke, drawn inline. Icons are decorative by default: the word next to
// them carries the meaning.
export type IconName =
  | 'draft'
  | 'full'
  | 'half'
  | 'done'
  | 'cancel'
  | 'warn'
  | 'error'
  | 'info'
  | 'lock'
  | 'menu'
  | 'close'
  | 'assist'
  | 'tray'
  | 'bell'
  | 'user'
  | 'print'
  | 'plus'
  | 'trash';

const stroke = { fill: 'none', stroke: 'currentColor', strokeWidth: 1.5 } as const;
const round = { ...stroke, strokeLinecap: 'round', strokeLinejoin: 'round' } as const;

const SHAPES: Record<IconName, ReactNode> = {
  draft: <circle cx="8" cy="8" r="6" {...stroke} />,
  full: <circle cx="8" cy="8" r="6" fill="currentColor" stroke="currentColor" strokeWidth={1.5} />,
  half: (
    <>
      <circle cx="8" cy="8" r="6" {...stroke} />
      <path d="M8 2a6 6 0 0 0 0 12z" fill="currentColor" />
    </>
  ),
  done: (
    <>
      <circle cx="8" cy="8" r="6" {...stroke} />
      <path d="M5.2 8.2l2 2 3.7-4.2" {...round} />
    </>
  ),
  cancel: (
    <>
      <circle cx="8" cy="8" r="6" {...stroke} />
      <path d="M3.9 12.1l8.2-8.2" {...round} />
    </>
  ),
  warn: (
    <>
      <path d="M8 2.2l6.4 11.3H1.6z" {...stroke} strokeLinejoin="round" />
      <path d="M8 6.6v3.1" {...round} />
      <circle cx="8" cy="11.6" r=".9" fill="currentColor" />
    </>
  ),
  error: (
    <>
      <path d="M5.5 1.8h5l3.7 3.7v5l-3.7 3.7h-5l-3.7-3.7v-5z" {...stroke} strokeLinejoin="round" />
      <path d="M5.9 5.9l4.2 4.2M10.1 5.9l-4.2 4.2" {...round} />
    </>
  ),
  info: (
    <>
      <circle cx="8" cy="8" r="6" {...stroke} />
      <path d="M8 7.3v3.9" {...round} />
      <circle cx="8" cy="4.9" r=".9" fill="currentColor" />
    </>
  ),
  lock: (
    <>
      <rect x="3" y="7" width="10" height="7" rx="1" {...stroke} />
      <path d="M5.3 7V5.2a2.7 2.7 0 0 1 5.4 0V7" {...stroke} />
    </>
  ),
  menu: <path d="M2.5 4.5h11M2.5 8h11M2.5 11.5h11" {...round} />,
  close: <path d="M3.8 3.8l8.4 8.4M12.2 3.8l-8.4 8.4" {...round} />,
  assist: (
    <path
      d="M8 4.6c0-.7-.6-1.3-1.3-1.3H2.5v8.4h4.4c.6 0 1.1.5 1.1 1.1zM8 4.6c0-.7.6-1.3 1.3-1.3h4.2v8.4H9.1c-.6 0-1.1.5-1.1 1.1z"
      {...stroke}
      strokeLinejoin="round"
    />
  ),
  tray: (
    <>
      <path d="M2 9.5l1.8-6h8.4l1.8 6v3H2z" {...stroke} strokeLinejoin="round" />
      <path d="M2 9.5h3.5l.8 1.5h3.4l.8-1.5H14" {...stroke} strokeLinejoin="round" />
    </>
  ),
  bell: (
    <>
      <path d="M4 11V7.4a4 4 0 0 1 8 0V11l1.2 1.5H2.8z" {...stroke} strokeLinejoin="round" />
      <path d="M6.6 14h2.8" {...round} />
    </>
  ),
  user: (
    <>
      <circle cx="8" cy="5.4" r="2.6" {...stroke} />
      <path d="M2.8 13.6c.6-2.4 2.7-3.9 5.2-3.9s4.6 1.5 5.2 3.9" {...round} />
    </>
  ),
  print: (
    <>
      <path d="M4.5 6V2.5h7V6" {...stroke} strokeLinejoin="round" />
      <rect x="2" y="6" width="12" height="5.5" rx="1" {...stroke} />
      <path d="M4.5 9.5h7v4h-7z" {...stroke} strokeLinejoin="round" />
    </>
  ),
  plus: <path d="M8 3v10M3 8h10" {...round} />,
  trash: (
    <>
      <path d="M3 4.5h10M6.2 4.5V3h3.6v1.5M4.3 4.5l.6 8.5h6.2l.6-8.5" {...round} />
      <path d="M6.8 7v3.6M9.2 7v3.6" {...round} />
    </>
  ),
};

interface IconProps {
  name: IconName;
  /** Larger variant for empty states. */
  size?: 16 | 32;
}

export function Icon({ name, size = 16 }: IconProps) {
  return (
    <svg className={size === 16 ? 'i' : undefined} viewBox="0 0 16 16" width={size} height={size} aria-hidden="true" focusable="false">
      {SHAPES[name]}
    </svg>
  );
}

export function BrandMark() {
  return (
    <svg viewBox="0 0 28 28" width="28" height="28" aria-hidden="true" focusable="false">
      <path d="M3 9h22" stroke="currentColor" strokeWidth={2} strokeLinecap="round" />
      <path d="M14 9v10" stroke="currentColor" strokeWidth={2} />
      <path d="M8.5 23h11l-5.5-5z" fill="currentColor" />
      <path d="M3 9l-1.5 6h7L7 9M21 9l-1.5 6h7L25 9" fill="none" stroke="currentColor" strokeWidth={1.5} strokeLinejoin="round" />
    </svg>
  );
}
