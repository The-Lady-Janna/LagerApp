import js from '@eslint/js'
import globals from 'globals'
import reactHooks from 'eslint-plugin-react-hooks'
import reactRefresh from 'eslint-plugin-react-refresh'
import tseslint from 'typescript-eslint'
import { defineConfig, globalIgnores } from 'eslint/config'

const HEX_COLOR = '#[0-9a-fA-F]{6}\\b|#[0-9a-fA-F]{3}\\b'

export default defineConfig([
  globalIgnores(['dist']),
  {
    files: ['**/*.{ts,tsx}'],
    extends: [
      js.configs.recommended,
      tseslint.configs.recommended,
      reactHooks.configs.flat.recommended,
      reactRefresh.configs.vite,
    ],
    languageOptions: {
      globals: globals.browser,
    },
  },
  {
    // route.tsx meldet eine Seite nur an (Daten, keine Fast-Refresh-Grenze): lazy()-Komponenten neben dem Export sind gewollt.
    files: ['src/features/*/route.tsx'],
    rules: { 'react-refresh/only-export-components': 'off' },
  },
  {
    // Farben kommen aus den Design-Tokens (src/index.css: var(--c-…), .pill--…). Hex-Werte in Komponenten
    // brechen den Dark-Mode; Zeichenflächen (Konva) beziehen ihre Farben aus src/lib/canvasColors.ts (.ts, nicht betroffen).
    // Ausnahme OrderStatusPill: die Statusfarbe ist dort nur Basis für color-mix() (Rand/Tönung), der Text folgt dem Theme.
    files: ['src/**/*.tsx'],
    ignores: ['src/tests/**', 'src/pages/orders/OrderStatusPill.tsx'],
    rules: {
      'no-restricted-syntax': [
        'error',
        {
          selector: `Literal[value=/${HEX_COLOR}/]`,
          message: 'Keine Hex-Farben in Komponenten: Design-Token nutzen (var(--c-…) bzw. CSS-Klasse aus index.css, Canvas: lib/canvasColors.ts).',
        },
        {
          selector: `TemplateElement[value.raw=/${HEX_COLOR}/]`,
          message: 'Keine Hex-Farben in Komponenten: Design-Token nutzen (var(--c-…) bzw. CSS-Klasse aus index.css, Canvas: lib/canvasColors.ts).',
        },
      ],
    },
  },
])
