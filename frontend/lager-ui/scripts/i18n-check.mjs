#!/usr/bin/env node
// Qualitätsprüfung der Sprachdateien (npm run i18n:check). Dieselbe Logik ruft der Vitest-Test
// src/tests/WP29/i18nCheck.test.ts auf — ein Skript, ein Verhalten, kein Auseinanderlaufen.
//
// Geprüft wird:
//   1. Parität: jede Sprache hat dieselben Namespaces und dieselben Schlüssel wie die Quellsprache (de)
//   2. Platzhalter: {{name}} in de und en sind je Schlüssel dieselben (sonst fehlt in einer Sprache ein Wert)
//   3. Leere Werte (eine nicht ausgefüllte Übersetzung)
//   4. Fehlende Schlüssel: im Code benutzt ("ns:schluessel"), aber in de nicht definiert → die UI zeigt den Rohschlüssel
//   5. Ungenutzte Schlüssel: in de definiert, im Code nirgends benutzt
//
// Wie Schlüssel im Code gefunden werden (reine Textsuche, kein Parser): jedes String-Literal der Form 'namespace:pfad.zum.key'
// mit einem bekannten Namespace zählt als Benutzung — egal ob in t('…'), i18nKey="…" oder in einer Tabelle
// (labelKey: 'status:order.New'). Ein Template-Literal `namespace:prefix.${wert}` zählt als Benutzung ALLER Schlüssel mit
// diesem Präfix (dynamische Schlüssel, z. B. Statuswerte). Ein nacktes t('schluessel') ohne Namespace gilt als common:schluessel.
// Plural-Endungen (_one, _other …) gehören zum Basisschlüssel. Verweise $t(ns:key) in den Werten der JSON-Dateien zählen ebenfalls.

import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const HERE = path.dirname(fileURLToPath(import.meta.url))
export const DEFAULT_ROOT = path.resolve(HERE, '..')
/** Quelle aller Texte; die anderen Sprachen werden daran gemessen. */
export const SOURCE_LANGUAGE = 'de'
const DEFAULT_NAMESPACE = 'common'
const PLURAL_SUFFIX = /_(zero|one|two|few|many|other)$/
const SKIPPED_DIRS = new Set(['tests', 'locales', 'node_modules'])

const toPosix = (p) => p.split(path.sep).join('/')

/** { a: { b: "x" } } → Map("a.b" → "x"). */
export function flatten(value, prefix = '', into = new Map()) {
  for (const [key, child] of Object.entries(value)) {
    const full = prefix ? `${prefix}.${key}` : key
    if (child !== null && typeof child === 'object' && !Array.isArray(child)) flatten(child, full, into)
    else into.set(full, String(child))
  }
  return into
}

/** Liest src/locales/<sprache>/<namespace>.json → { de: { common: Map(key → text) }, en: … }. */
export function loadLocales(root = DEFAULT_ROOT) {
  const dir = path.join(root, 'src', 'locales')
  const locales = {}
  for (const lng of fs.readdirSync(dir, { withFileTypes: true }).filter((e) => e.isDirectory()).map((e) => e.name).sort()) {
    locales[lng] = {}
    for (const file of fs.readdirSync(path.join(dir, lng)).filter((f) => f.endsWith('.json')).sort()) {
      const ns = file.slice(0, -'.json'.length)
      const json = JSON.parse(fs.readFileSync(path.join(dir, lng, file), 'utf8'))
      locales[lng][ns] = flatten(json)
    }
  }
  return locales
}

function* sourceFiles(dir) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    if (entry.isDirectory()) {
      if (!SKIPPED_DIRS.has(entry.name)) yield* sourceFiles(path.join(dir, entry.name))
    } else if (/\.(ts|tsx)$/.test(entry.name) && !/\.d\.ts$/.test(entry.name)) {
      yield path.join(dir, entry.name)
    }
  }
}

/** Liest alle Quelltexte unter src/ (ohne Tests und Sprachdateien): { 'src/pfad/datei.tsx': text }. */
export function loadSources(root = DEFAULT_ROOT) {
  const sources = {}
  for (const file of sourceFiles(path.join(root, 'src'))) {
    sources[toPosix(path.relative(root, file))] = fs.readFileSync(file, 'utf8')
  }
  return sources
}

/**
 * Sucht die benutzten Schlüssel in den Quelltexten ({ datei: text }). `exact`: "ns:key" → Dateien; `prefixes`:
 * "ns:prefix." → Dateien (dynamische Schlüssel). `namespaces` = die bekannten Namespaces (nur die zählen als Schlüssel-Literal).
 */
export function scanSources(sources, namespaces = []) {
  const exact = new Map()
  const prefixes = new Map()
  const add = (map, key, file) => {
    if (!map.has(key)) map.set(key, new Set())
    map.get(key).add(file)
  }
  const nsGroup = namespaces.map((n) => n.replace(/[^A-Za-z0-9_-]/g, '')).join('|')
  const KEY_CHARS = '[A-Za-z0-9_.-]'
  const literal = nsGroup ? new RegExp(`(['"\`])((?:${nsGroup}):${KEY_CHARS}+)\\1`, 'g') : null
  const dynamic = nsGroup ? new RegExp(`\`((?:${nsGroup}):${KEY_CHARS}*)\\$\\{`, 'g') : null
  const bareCall = /\bt\(\s*(['"`])([^'"`$:\s]+)\1/g

  for (const [rel, text] of Object.entries(sources)) {
    if (literal) for (const m of text.matchAll(literal)) add(exact, m[2], rel)
    if (dynamic) for (const m of text.matchAll(dynamic)) add(prefixes, m[1], rel)
    for (const m of text.matchAll(bareCall)) add(exact, `${DEFAULT_NAMESPACE}:${m[2]}`, rel)
  }
  return { exact, prefixes }
}

const PLACEHOLDER = /\{\{\s*([^}\s,]+)[^}]*\}\}/g
const placeholders = (text) => [...new Set([...text.matchAll(PLACEHOLDER)].map((m) => m[1]))].sort()
const baseKey = (key) => key.replace(PLURAL_SUFFIX, '')

/** Die Prüfung auf der Platte: Sprachdateien und Quelltexte unter `root` (Standard: dieses Projekt). */
export function checkI18n({ root = DEFAULT_ROOT } = {}) {
  return analyze({ locales: loadLocales(root), sources: loadSources(root) })
}

/**
 * Führt alle Prüfungen aus (ohne Dateizugriff: `locales` wie von loadLocales, `sources` wie von loadSources).
 * Ergebnis: { ok, problems, stats } — `problems` ist je Kategorie eine Liste von Strings.
 */
export function analyze({ locales, sources }) {
  const languages = Object.keys(locales)
  const problems = {
    missingLanguage: [],
    missingNamespace: [],
    missingKey: [],
    extraKey: [],
    placeholderMismatch: [],
    emptyValue: [],
    undefinedKey: [],
    unusedKey: [],
  }

  const source = locales[SOURCE_LANGUAGE]
  if (!source) {
    problems.missingLanguage.push(`Quellsprache "${SOURCE_LANGUAGE}" fehlt (src/locales/${SOURCE_LANGUAGE}/)`)
    return { ok: false, problems, stats: { languages, namespaces: [], keys: 0 } }
  }
  const namespaces = Object.keys(source)

  // 1.–3. Parität, Platzhalter, leere Werte
  for (const lng of languages) {
    const own = locales[lng]
    for (const ns of namespaces) {
      if (!own[ns]) { problems.missingNamespace.push(`${lng}/${ns}.json fehlt`); continue }
      for (const [key, text] of own[ns]) {
        if (text.trim() === '') problems.emptyValue.push(`${lng} ${ns}:${key}`)
        if (lng === SOURCE_LANGUAGE) continue
        if (!source[ns].has(key)) { problems.extraKey.push(`${lng} ${ns}:${key} (in ${SOURCE_LANGUAGE} nicht vorhanden)`); continue }
        const want = placeholders(source[ns].get(key)).join(', ')
        const have = placeholders(text).join(', ')
        if (want !== have) problems.placeholderMismatch.push(`${lng} ${ns}:${key} — Platzhalter {{${have}}} statt {{${want}}}`)
      }
      if (lng === SOURCE_LANGUAGE) continue
      for (const key of source[ns].keys()) {
        if (!own[ns].has(key)) problems.missingKey.push(`${lng} ${ns}:${key} fehlt`)
      }
    }
    for (const ns of Object.keys(own)) {
      if (!namespaces.includes(ns)) problems.extraKey.push(`${lng} ${ns}.json (in ${SOURCE_LANGUAGE} nicht vorhanden)`)
    }
  }

  // 4.–5. Benutzung im Code
  const { exact, prefixes } = scanSources(sources, namespaces)
  const nested = new Set() // $t(ns:key)-Verweise innerhalb der Werte
  for (const [ns, map] of Object.entries(source)) {
    for (const text of map.values()) {
      for (const m of text.matchAll(/\$t\(\s*(?:([A-Za-z0-9_-]+):)?([A-Za-z0-9_.-]+)/g)) nested.add(`${m[1] ?? ns}:${m[2]}`)
    }
  }

  const definedByBase = new Set()
  for (const [ns, map] of Object.entries(source)) for (const key of map.keys()) definedByBase.add(`${ns}:${baseKey(key)}`)
  const definedExact = new Set()
  for (const [ns, map] of Object.entries(source)) for (const key of map.keys()) definedExact.add(`${ns}:${key}`)

  for (const [key, files] of exact) {
    if (!definedExact.has(key) && !definedByBase.has(key)) problems.undefinedKey.push(`${key} (${[...files].join(', ')})`)
  }
  for (const [ns, map] of Object.entries(source)) {
    for (const key of map.keys()) {
      const full = `${ns}:${key}`
      const base = `${ns}:${baseKey(key)}`
      const used = exact.has(full) || exact.has(base) || nested.has(full) || nested.has(base)
        || [...prefixes.keys()].some((prefix) => full.startsWith(prefix))
      if (!used) problems.unusedKey.push(full)
    }
  }

  let keys = 0
  for (const map of Object.values(source)) keys += map.size
  const ok = Object.values(problems).every((list) => list.length === 0)
  return { ok, problems, stats: { languages, namespaces, keys } }
}

const TITLES = {
  missingLanguage: 'Sprache fehlt',
  missingNamespace: 'Namespace-Datei fehlt',
  missingKey: 'Schlüssel fehlt in einer Sprache (Parität verletzt)',
  extraKey: 'Überzähliger Schlüssel (nicht in der Quellsprache)',
  placeholderMismatch: 'Platzhalter weichen ab',
  emptyValue: 'Leerer Text',
  undefinedKey: 'Im Code benutzt, aber nicht definiert (die UI zeigt den Rohschlüssel)',
  unusedKey: 'Definiert, aber im Code nicht benutzt',
}

/** Lesbarer Bericht zum Ergebnis von checkI18n. */
export function formatReport(result) {
  const { stats } = result
  const lines = [`i18n:check — ${stats.languages.join(', ')}; ${stats.namespaces.length} Namespaces, ${stats.keys} Schlüssel (${SOURCE_LANGUAGE})`]
  for (const [category, list] of Object.entries(result.problems)) {
    if (list.length === 0) continue
    lines.push('', `${TITLES[category]} (${list.length}):`, ...list.map((item) => `  - ${item}`))
  }
  lines.push('', result.ok ? 'OK: keine Abweichungen.' : 'FEHLER: siehe oben.')
  return lines.join('\n')
}

// CLI: node scripts/i18n-check.mjs
if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const result = checkI18n()
  console.log(formatReport(result))
  process.exit(result.ok ? 0 : 1)
}
