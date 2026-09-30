/// <reference types="vitest/config" />
import { defineConfig, loadEnv, type Plugin } from 'vite'
import react from '@vitejs/plugin-react'
import fs from 'node:fs'
import path from 'node:path'

// Stempelt beim Build eine Build-ID in dist/sw.js (Platzhalter __BUILD_ID__).
// Ohne das wäre sw.js bei jedem Deployment byte-identisch, der Browser würde nie
// einen neuen Service-Worker installieren und alte Caches blieben bestehen.
function swBuildId(): Plugin {
  let outDir = 'dist'
  return {
    name: 'lager-sw-build-id',
    apply: 'build',
    configResolved(config) {
      outDir = path.resolve(config.root, config.build.outDir)
    },
    closeBundle() {
      const file = path.join(outDir, 'sw.js')
      if (!fs.existsSync(file)) return
      const buildId = Date.now().toString(36)
      fs.writeFileSync(file, fs.readFileSync(file, 'utf8').replaceAll('__BUILD_ID__', buildId))
    },
  }
}

// Erzwingt EINE React-Resolution — verhindert "Invalid hook call / two copies
// of React"-Bugs wenn ein transitive dep React als dependency (statt peer)
// mitbringt. dedupe sorgt zusätzlich dafür dass auch transitive Imports auf
// dieselbe Instanz auflösen.
export default defineConfig(({ mode }) => {
  // Ziel des Dev-Proxys für /api. Per Umgebungsvariable (oder .env.local)
  // überschreibbar, z. B. VITE_API_TARGET=http://192.168.1.20:5099
  const env = loadEnv(mode, import.meta.dirname, '')
  const apiTarget = env.VITE_API_TARGET || 'http://localhost:5099'

  return {
    plugins: [react(), swBuildId()],
    resolve: {
      alias: {
        react: path.resolve(import.meta.dirname, 'node_modules/react'),
        'react-dom': path.resolve(import.meta.dirname, 'node_modules/react-dom'),
      },
      dedupe: ['react', 'react-dom'],
    },
    optimizeDeps: {
      include: ['react', 'react-dom', 'react-dom/client'],
    },
    server: {
      port: 5173,
      proxy: {
        '/api': {
          target: apiTarget,
          changeOrigin: true,
        },
      },
    },
    // Vitest (npm test): DOM-Tests mit jsdom + Testing Library. Tests liegen
    // unter src/tests/<WP-ID>/ (*.test.ts / *.test.tsx).
    test: {
      environment: 'jsdom',
      globals: true,
      setupFiles: ['./src/tests/setup.ts'],
      include: ['src/**/*.test.{ts,tsx}'],
      css: false,
      restoreMocks: true,
      // Großzügige Timeouts: viele UI-Tests mit userEvent + lazy geladenen Seiten reißen auf langsamen
      // CI-Runnern oder unter Last sonst das 5-s-Standardlimit (kein Logikfehler).
      testTimeout: 30_000,
      hookTimeout: 30_000,
    },
  }
})
