import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from './App';
import './styles/fonts.css';
import './styles/tokens.css';
import './styles/base.css';
import './styles/shell.css';
import './styles/components.css';
import './styles/print.css';

const container = document.getElementById('root');
if (container === null) {
  throw new Error('Kök öğe bulunamadı');
}

createRoot(container).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
