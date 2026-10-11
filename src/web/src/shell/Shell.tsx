import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { MouseEvent, ReactNode } from 'react';
import { useLocation } from 'react-router-dom';
import { opsApi } from '../api/endpoints';
import { navigationFor, roleLabel } from '../auth/permissions';
import { useMe, useSession } from '../auth/session';
import { AssistPanel } from '../assist/AssistPanel';
import { BrandMark, Icon } from '../components/Icon';
import { ProblemAlert } from '../components/states';
import { Button } from '../components/ui';
import { formatDateTime } from '../lib/format';
import { useEscape, useMediaQuery, usePopover } from '../lib/hooks';
import { useApiQuery, useInvalidate } from '../lib/query';
import { useCommand } from '../lib/useCommand';
import { asProblem } from '../api/problem';
import { KLink, KNavLink, useAssist, useWarehouse } from './context';

function WarehouseSelect() {
  const { warehouses, selected, select } = useWarehouse();
  if (selected === null) {
    return null;
  }

  if (warehouses.length === 1) {
    return (
      <p className="depot">
        <span className="sr-only">Depo: </span>
        <span className="code" translate="no">
          {selected.code}
        </span>
        <span>{selected.name}</span>
      </p>
    );
  }

  return (
    <div className="depot">
      <label htmlFor="kabuk-depo">Depo</label>
      <select id="kabuk-depo" name="depo" autoComplete="off" value={selected.code} onChange={(event) => select(event.target.value)}>
        {warehouses.map((warehouse) => (
          <option key={warehouse.id} value={warehouse.code}>
            {warehouse.code} · {warehouse.name}
          </option>
        ))}
      </select>
    </div>
  );
}

function Notifications() {
  const { open, anchorRef, buttonRef, toggle, close } = usePopover();
  const invalidate = useInvalidate();
  const query = useApiQuery(['notifications'], (signal) => opsApi.notifications(signal), { refetchInterval: 60_000 });
  const markRead = useCommand((notificationId: string) => opsApi.markNotificationRead(notificationId), {
    onSuccess: () => invalidate('notifications'),
  });
  const items = query.data ?? [];
  const unread = items.filter((item) => !item.isRead).length;
  const label = unread > 0 ? `Bildirimler, ${unread} okunmamış` : 'Bildirimler';

  return (
    <div className="popover-anchor" ref={anchorRef}>
      <button
        type="button"
        className="iconbtn"
        ref={buttonRef}
        aria-label={label}
        aria-expanded={open}
        aria-controls="bildirimler"
        onClick={toggle}
      >
        <Icon name="bell" />
        {unread > 0 && (
          <span className="iconbtn__count" aria-hidden="true">
            {unread}
          </span>
        )}
      </button>
      {open && (
        <section className="popover" id="bildirimler" aria-label="Bildirimler">
          <div className="popover__head">
            <h2>Bildirimler</h2>
            <button type="button" className="iconbtn iconbtn--panel" aria-label="Bildirimleri kapat" onClick={close}>
              <Icon name="close" />
            </button>
          </div>
          {query.isPending && (
            <p className="popover__body" role="status" aria-busy="true">
              Bildirimler yükleniyor…
            </p>
          )}
          {query.isError && (
            <div className="popover__body">
              <ProblemAlert problem={asProblem(query.error)} title="Bildirimler yüklenemedi" onRetry={() => void query.refetch()} headingLevel={3} />
            </div>
          )}
          {query.data !== undefined && items.length === 0 && (
            <p className="popover__body">Bildiriminiz yok. Size atanan talepler ve düşük stok uyarıları burada görünür.</p>
          )}
          {markRead.problem !== null && (
            <div className="popover__body">
              <ProblemAlert problem={markRead.problem} title="Bildirim okundu olarak işaretlenemedi" headingLevel={3} />
            </div>
          )}
          {items.length > 0 && (
            <ul className="popover__list">
              {items.map((item) => (
                <li key={item.id} data-unread={!item.isRead}>
                  <span className="wrap">{item.text}</span>
                  <span className="small date">
                    {formatDateTime(item.createdAt)}
                    {item.isRead ? ' · okundu' : ' · okunmadı'}
                  </span>
                  {!item.isRead && (
                    <span>
                      <Button size="small" onClick={() => void markRead.run(item.id)} pending={markRead.pending} pendingLabel="İşaretleniyor…">
                        Okundu olarak işaretle
                      </Button>
                    </span>
                  )}
                </li>
              ))}
            </ul>
          )}
        </section>
      )}
    </div>
  );
}

function UserMenu() {
  const me = useMe();
  const { logout } = useSession();
  const { open, anchorRef, buttonRef, toggle, close } = usePopover();
  const [leaving, setLeaving] = useState(false);

  const signOut = () => {
    if (leaving) {
      return;
    }

    setLeaving(true);
    void logout();
  };

  return (
    <div className="popover-anchor" ref={anchorRef}>
      <button
        type="button"
        className="iconbtn"
        ref={buttonRef}
        aria-label={`Kullanıcı menüsü: ${me.displayName}`}
        aria-expanded={open}
        aria-controls="kullanici-menusu"
        onClick={toggle}
      >
        <Icon name="user" />
      </button>
      {open && (
        <section className="popover" id="kullanici-menusu" aria-label="Kullanıcı menüsü">
          <div className="popover__body">
            <p className="wrap">
              <strong>{me.displayName}</strong>
              <br />
              <span className="code muted" translate="no">
                {me.userName}
              </span>
            </p>
            <p className="small">{me.roles.length > 0 ? me.roles.map(roleLabel).join(' · ') : 'Rol atanmamış'}</p>
          </div>
          <ul className="menu-list">
            <li>
              <KLink to="/parola" onClick={close}>
                Parolayı değiştir
              </KLink>
            </li>
            <li>
              <button type="button" onClick={signOut} aria-busy={leaving || undefined}>
                {leaving ? 'Çıkış yapılıyor…' : 'Çıkış yap'}
              </button>
            </li>
          </ul>
        </section>
      )}
    </div>
  );
}

/** Top bar, role-filtered navigation rail, content and the Kaynaklı Yardım side panel. */
export function Shell({ children }: { children: ReactNode }) {
  const me = useMe();
  const assist = useAssist();
  const location = useLocation();
  const groups = useMemo(() => navigationFor(me), [me]);
  const [navOpen, setNavOpen] = useState(false);
  const narrowNav = useMediaQuery('(max-width: 1023.98px)');
  const narrowAssist = useMediaQuery('(max-width: 1199.98px)');
  const navModal = navOpen && narrowNav;
  const assistModal = assist.isOpen && narrowAssist;
  const menuButton = useRef<HTMLButtonElement>(null);
  const closeButton = useRef<HTMLButtonElement>(null);
  const main = useRef<HTMLElement>(null);

  const returnToMenu = useRef(false);
  const closeNav = useCallback(() => {
    returnToMenu.current = true;
    setNavOpen(false);
  }, []);
  useEscape(navModal, closeNav);

  // Focus goes into the drawer when it opens and back to the menu button when it closes. The return
  // happens after the commit, because until then the top bar is still inert behind the drawer.
  useEffect(() => {
    if (navModal) {
      closeButton.current?.focus();
    } else if (returnToMenu.current) {
      returnToMenu.current = false;
      menuButton.current?.focus();
    }
  }, [navModal]);

  // After a route change, focus moves to the content so keyboard and screen-reader users start there.
  // Compared by value (not "first run") so that a freshly loaded page never has its focus moved.
  const lastPath = useRef(location.pathname);
  useEffect(() => {
    if (lastPath.current === location.pathname) {
      return;
    }

    lastPath.current = location.pathname;

    main.current?.focus();
    window.scrollTo(0, 0);
  }, [location.pathname]);

  const onRailClick = (event: MouseEvent<HTMLElement>) => {
    if (event.target instanceof Element && event.target.closest('a') !== null) {
      setNavOpen(false);
    }
  };

  return (
    <>
      {/* Inert together with the page while a modal layer is open, so focus cannot leave that layer. */}
      <a className="skip" href="#icerik" inert={navModal || assistModal}>
        İçeriğe geç
      </a>
      <div className="shell" data-assist={assist.isOpen ? 'open' : undefined}>
        <header className="topbar" inert={navModal || assistModal}>
          <button
            type="button"
            className="iconbtn menu-btn"
            ref={menuButton}
            aria-expanded={navOpen}
            aria-controls="gezinme"
            onClick={() => setNavOpen((value) => !value)}
          >
            <Icon name="menu" />
            <span className="iconbtn__label--wide">Menü</span>
          </button>
          <KLink className="brand" to="/" translate="no" aria-label="Kantar — açılış sayfası">
            <BrandMark />
            Kantar
          </KLink>
          <WarehouseSelect />
          <div className="topbar__tools">
            <p className="userline">
              {me.displayName}
              <small>{me.roles.map(roleLabel).join(' · ')}</small>
            </p>
            <button
              type="button"
              className="iconbtn"
              aria-expanded={assist.isOpen}
              aria-controls="yardim"
              onClick={() => (assist.isOpen ? assist.close() : assist.open())}
            >
              <Icon name="assist" />
              <span className="iconbtn__label--wide">Kaynaklı Yardım</span>
            </button>
            <Notifications />
            <UserMenu />
          </div>
        </header>

        <nav className="rail" id="gezinme" aria-label="Ana gezinme" data-open={navModal} inert={assistModal} onClick={onRailClick}>
          <button type="button" className="iconbtn rail-close" ref={closeButton} onClick={closeNav}>
            <Icon name="close" />
            <span>Menüyü kapat</span>
          </button>
          {groups.map((group) => (
            <div key={group.id}>
              <p className="rail-group" id={`gezinme-${group.id}`}>
                {group.label}
              </p>
              <ul aria-labelledby={`gezinme-${group.id}`}>
                {group.items.map((item) => (
                  <li key={item.to}>
                    <KNavLink to={item.to} end={item.end ?? false}>
                      {item.label}
                    </KNavLink>
                  </li>
                ))}
              </ul>
            </div>
          ))}
        </nav>
        {navModal && <div className="backdrop" data-open="true" onClick={closeNav} />}

        <main id="icerik" tabIndex={-1} ref={main} inert={navModal || assistModal}>
          {children}
        </main>

        {assist.isOpen && <AssistPanel key={assist.session} modal={assistModal} focusQuestion={assist.session > 0} />}
        {assistModal && <div className="assist-backdrop" onClick={assist.close} />}
      </div>
    </>
  );
}
