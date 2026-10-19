import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

export default defineConfig({
  plugins: [react()],
  // Must match the client's DevUrl (Program.cs); never 3000, so sibling apps' dev servers cannot collide.
  server: { port: 3920, strictPort: true },
  // The client embeds this output (gitignored).
  build: { outDir: '../wwwroot', emptyOutDir: true },
});
