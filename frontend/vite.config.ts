import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig({
  plugins: [react()],
  // Mismo origen para la cookie de sesión (SameSite=Strict).
  server: { proxy: { '/api': 'http://localhost:5080' } },
})
