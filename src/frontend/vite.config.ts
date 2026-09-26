import { defineConfig } from 'vitest/config'
import react from '@vitejs/plugin-react'
import { resolve } from 'node:path'

export default defineConfig({ plugins: [react()], server: { fs: { allow: [resolve(import.meta.dirname, '../../assets')] }, proxy: { '/api': 'http://127.0.0.1:5179' } }, test: { environment: 'jsdom', setupFiles: './src/testSetup.ts' } })
