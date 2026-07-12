import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// Served from https://dsazykin.github.io/GitLoom/ — base must match the repo name.
export default defineConfig({
  base: '/GitLoom/',
  plugins: [react()],
});
