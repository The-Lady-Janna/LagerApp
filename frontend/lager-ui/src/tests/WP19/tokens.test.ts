/// <reference types="node" />
import { readFileSync } from 'node:fs'
import { resolve as resolvePath } from 'node:path'
import { describe, expect, it } from 'vitest'

// Kontrast-Stichprobe der Design-Tokens: index.css wird gelesen (nicht gerendert) und für Hell und Dunkel
// jedes Text/Hintergrund-Paar nach WCAG 2.x berechnet. Fällt ein Token unter 4,5:1 (Text) bzw. 3:1 (Fokusring),
// schlägt der Test an — statt dass erst jemand im Dark-Mode unleserliche Seiten meldet.

// Über das Dateisystem gelesen: vitest verarbeitet CSS-Importe (css: false) nicht, wir brauchen den Quelltext.
const css = readFileSync(resolvePath(process.cwd(), 'src/index.css'), 'utf8').replace(/\/\*[\s\S]*?\*\//g, '')

function blockOf(selector: string): string {
  const start = css.indexOf(`${selector} {`)
  if (start < 0) throw new Error(`Block ${selector} nicht gefunden`)
  return css.slice(css.indexOf('{', start) + 1, css.indexOf('}', start))
}

function tokensOf(block: string): Record<string, string> {
  const tokens: Record<string, string> = {}
  for (const [, name, value] of block.matchAll(/(--[\w-]+)\s*:\s*([^;]+);/g)) tokens[name] = value.trim()
  return tokens
}

const light = tokensOf(blockOf(':root'))
const dark = { ...light, ...tokensOf(blockOf("[data-theme='dark']")) }

/** Löst var(--x) über die Tokens des Themes auf. */
function resolve(theme: Record<string, string>, value: string): string {
  let current = value
  for (let i = 0; i < 5; i++) {
    const match = /^var\((--[\w-]+)\)$/.exec(current)
    if (!match) return current
    current = theme[match[1]]
    if (current === undefined) throw new Error(`Token ${match[1]} fehlt`)
  }
  return current
}

function luminance(hex: string): number {
  const h = hex.replace('#', '')
  const full = h.length === 3 ? [...h].map((c) => c + c).join('') : h
  const [r, g, b] = [0, 2, 4].map((i) => parseInt(full.slice(i, i + 2), 16) / 255)
    .map((v) => (v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4))
  return 0.2126 * r + 0.7152 * g + 0.0722 * b
}

function contrast(fg: string, bg: string): number {
  const [hi, lo] = [luminance(fg), luminance(bg)].sort((a, b) => b - a)
  return (hi + 0.05) / (lo + 0.05)
}

const WHITE = '#ffffff'
const colorOf = (theme: Record<string, string>, ref: string) => (ref.startsWith('#') ? ref : resolve(theme, theme[ref] ?? `var(${ref})`))

const TONES = ['neutral', 'info', 'success', 'warning', 'danger', 'accent', 'special']

/** [Vordergrund, Hintergrund] — Text muss ≥ 4,5:1 erreichen. */
const TEXT_PAIRS: [string, string][] = [
  ['--c-text', '--c-bg'], ['--c-text', '--c-surface'], ['--c-text', '--c-surface-alt'], ['--c-text', '--c-info-bg'],
  ['--c-muted', '--c-bg'], ['--c-muted', '--c-surface'], ['--c-muted', '--c-surface-alt'],
  ['--c-link', '--c-bg'], ['--c-link', '--c-surface'],
  ['--c-on-primary', '--c-primary'], ['--c-on-primary', '--c-primary-hover'],
  [WHITE, '--c-danger'], [WHITE, '--c-danger-hover'],
  ['--c-danger-text', '--c-surface'], ['--c-danger-text', '--c-bg'], ['--c-danger-text', '--c-danger-bg'],
  ['--c-success', '--c-surface'], ['--c-success', '--c-bg'], ['--c-success', '--c-success-bg'],
  ['--c-warning', '--c-surface'], ['--c-warning', '--c-bg'], ['--c-warning', '--c-warning-bg'],
  ['--c-info', '--c-surface'], ['--c-info', '--c-bg'], ['--c-info', '--c-info-bg'],
  ['--c-sidebar-text', '--c-sidebar-bg'], ['--c-sidebar-muted', '--c-sidebar-bg'],
  ['--c-sidebar-text', '--c-sidebar-hover'], ['--c-sidebar-muted', '--c-sidebar-hover'],
  ...TONES.map((tone): [string, string] => [`--pill-${tone}-fg`, `--pill-${tone}-bg`]),
]

describe('Design-Tokens: Kontrast in Hell und Dunkel', () => {
  for (const [name, theme] of [['hell', light], ['dunkel', dark]] as const) {
    it.each(TEXT_PAIRS)(`${name}: %s auf %s ≥ 4,5:1`, (fg, bg) => {
      expect(contrast(colorOf(theme, fg), colorOf(theme, bg))).toBeGreaterThanOrEqual(4.5)
    })

    it(`${name}: der Fokusring hat ≥ 3:1 gegen die Flächen (WCAG 1.4.11)`, () => {
      for (const surface of ['--c-bg', '--c-surface']) {
        expect(contrast(colorOf(theme, '--c-focus'), colorOf(theme, surface))).toBeGreaterThanOrEqual(3)
      }
    })
  }

  it('der feste Dunkel-Modus des Mobile-Pickers (--pk-*) ist in sich lesbar', () => {
    const pairs: [string, string][] = [
      ['--pk-text', '--pk-bg'], ['--pk-text', '--pk-surface'], ['--pk-soft', '--pk-bg'], ['--pk-soft', '--pk-surface'],
      ['--pk-muted', '--pk-bg'], ['--pk-muted', '--pk-surface'], ['--pk-ok-text', '--pk-surface'], ['--pk-warn-text', '--pk-surface'],
      ['--pk-warn-fg', '--pk-warn-bg'], ['--pk-error-fg', '--pk-error-bg'], ['--pk-error-text', '--pk-bg'],
      [WHITE, '--pk-primary'], [WHITE, '--pk-ok'], [WHITE, '--pk-scan'], [WHITE, '--pk-bin'], [WHITE, '--pk-surface-strong'], [WHITE, '--pk-border'],
    ]
    for (const [fg, bg] of pairs) {
      expect(contrast(colorOf(light, fg), colorOf(light, bg)), `${fg} auf ${bg}`).toBeGreaterThanOrEqual(4.5)
    }
  })
})

describe('index.css: Theme-Grundlagen', () => {
  it('color-scheme folgt dem Theme (light/dark) statt statisch "light dark"', () => {
    expect(css).toMatch(/:root\s*\{\s*color-scheme:\s*light;/)
    expect(blockOf("[data-theme='dark']")).toMatch(/color-scheme:\s*dark;/)
    expect(css).not.toMatch(/color-scheme:\s*light dark/)
  })

  it('definiert jeden Pill-Ton, die Status-Kacheln und die Modal-/Drawer-Klassen', () => {
    for (const tone of TONES) expect(css).toContain(`.pill--${tone}`)
    for (const selector of ['.stat-tile', '.modal-backdrop', '.modal-dialog', '.skip-link', '.app-topbar', '.table-wrap', '.visually-hidden', ':focus-visible']) {
      expect(css).toContain(selector)
    }
  })

  it('bricht unter 900 px die Sidebar zum Drawer um und lässt Formular-Raster umbrechen (Media-Queries)', () => {
    expect(css).toMatch(/@media \(max-width: 900px\)[\s\S]*\.sidebar[\s\S]*transform: translateX\(-100%\)/)
    expect(css).toMatch(/@media \(max-width: 600px\)[\s\S]*\.grid-2, \.grid-3 \{ grid-template-columns: 1fr; \}/)
    // minmax(0, 1fr): der Inhalt darf die Spalte nicht aufspreizen (kein horizontaler Seiten-Scroll)
    expect(css).toMatch(/\.app \{[^}]*grid-template-columns: 220px minmax\(0, 1fr\)/)
  })

  it('.visually-hidden ist absolut positioniert: Seite, Karte und Tabellen-Wrapper sind Bezugsfläche (position: relative)', () => {
    // Sonst bezieht sich ein verstecktes <span> in der letzten Spalte einer breiten Tabelle auf das Fenster und ragt über den
    // Seitenrand: bei 375 px gäbe es horizontalen Seiten-Scroll (im Browser gemessen: scrollWidth 493 statt 375 auf /orders).
    expect(css).toMatch(/\.visually-hidden \{[^}]*position: absolute/)
    for (const selector of ['\\.main', '\\.card', '\\.table-wrap']) {
      expect(css, `${selector} braucht position: relative`).toMatch(new RegExp(`(^|\\n)${selector} \\{[^}]*position: relative`))
    }
  })
})
