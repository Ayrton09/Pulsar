import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from './app/App.tsx';
import { initI18n } from './app/i18n.ts';
import './index.css';

const root = document.getElementById('root');
if (!root) {
  throw new Error('Root element #root not found');
}

await initI18n(navigator.language);

createRoot(root).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
