import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig({
  plugins: [react()],
  // Same origin for the session cookie (SameSite=Strict).
  server: { proxy: { '/api': 'http://localhost:5080' } },
})
