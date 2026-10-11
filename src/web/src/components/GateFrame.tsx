import type { ReactNode } from 'react';
import { BrandMark } from './Icon';

/** Frame of the screens outside the shell: session check, sign-in, forced password change. */
export function GateFrame({ children, wide = false, tools }: { children: ReactNode; wide?: boolean; tools?: ReactNode }) {
  return (
    <div className="gate">
      <header className="gate__bar topbar">
        <span className="brand" translate="no">
          <BrandMark />
          Kantar
        </span>
        {tools}
      </header>
      <main className={wide ? 'gate__body gate__body--wide' : 'gate__body'} id="icerik">
        {children}
      </main>
    </div>
  );
}
