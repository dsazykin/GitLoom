import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// Served from https://mainguard.dev/ (GitHub Pages custom domain, public/CNAME) —
// a custom domain serves at the root, so the base is '/'.
export default defineConfig({
  base: '/',
  plugins: [react()],
});
