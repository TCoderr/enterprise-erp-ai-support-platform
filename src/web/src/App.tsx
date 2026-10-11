import type { QueryClient } from '@tanstack/react-query';
import { QueryClientProvider } from '@tanstack/react-query';
import { useCallback, useEffect, useRef, useState } from 'react';
import { BrowserRouter, Navigate, useLocation, useNavigate } from 'react-router-dom';
import { MeProvider, SessionProvider, takeIntendedPath, useSession } from './auth/session';
import { GateFrame } from './components/GateFrame';
import { ProblemAlert } from './components/states';
import { createQueryClient } from './lib/query';
import { AppRoutes } from './routes';
import { LoginScreen } from './screens/auth/LoginScreen';
import { PasswordScreen } from './screens/auth/PasswordScreen';
import { AssistProvider, WarehouseProvider } from './shell/context';
import { Shell } from './shell/Shell';

/** After signing in, return to where the user was heading (kept in memory only). */
function PostLoginRedirect() {
  const navigate = useNavigate();
  const done = useRef(false);
  useEffect(() => {
    if (!done.current) {
      done.current = true;
      void navigate(takeIntendedPath() ?? '/', { replace: true });
    }
  }, [navigate]);
  return null;
}

function Gate() {
  const { state, retry } = useSession();
  const location = useLocation();

  switch (state.status) {
    case 'loading':
      return (
        <GateFrame>
          <p role="status" aria-busy="true">
            Oturum denetleniyor…
          </p>
        </GateFrame>
      );
    case 'unreachable':
      return (
        <GateFrame wide>
          <ProblemAlert problem={state.problem} title="Kantar’a şu anda ulaşılamıyor" onRetry={retry} />
        </GateFrame>
      );
    case 'anonymous':
      return location.pathname === '/giris' ? <LoginScreen notice={state.notice} /> : <Navigate to="/giris" replace />;
    case 'authenticated': {
      const { me } = state;
      if (me.mustChangePassword) {
        return <PasswordScreen forced me={me} />;
      }

      if (location.pathname === '/giris') {
        return <PostLoginRedirect />;
      }

      // Keyed by user id: no component state of one user survives a switch to another.
      return (
        <MeProvider me={me} key={me.id}>
          <WarehouseProvider>
            <AssistProvider>
              <Shell>
                <AppRoutes />
              </Shell>
            </AssistProvider>
          </WarehouseProvider>
        </MeProvider>
      );
    }
  }
}

/** Session gate + routes. Exported separately so tests can mount it inside a memory router. */
export function AppRoot({ queryClient }: { queryClient: QueryClient }) {
  const location = useLocation();
  const path = useRef('/');
  useEffect(() => {
    path.current = `${location.pathname}${location.search}`;
  }, [location]);
  const currentPath = useCallback(() => path.current, []);

  return (
    <SessionProvider queryClient={queryClient} currentPath={currentPath}>
      <Gate />
    </SessionProvider>
  );
}

export function App() {
  const [queryClient] = useState(createQueryClient);
  return (
    <QueryClientProvider client={queryClient}>
      <BrowserRouter>
        <AppRoot queryClient={queryClient} />
      </BrowserRouter>
    </QueryClientProvider>
  );
}
