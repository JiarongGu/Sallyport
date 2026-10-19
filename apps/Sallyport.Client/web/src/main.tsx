import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { installDevInterceptor } from '@shenora/react';
import { App } from './App';
import './styles.css';

// Development only: window.__shenora, for driving the page from DevTools.
if (import.meta.env.DEV) installDevInterceptor();

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
