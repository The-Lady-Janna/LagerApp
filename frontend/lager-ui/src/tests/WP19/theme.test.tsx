import { afterEach, describe, expect, it, vi } from 'vitest'
import { act, renderHook } from '@testing-library/react'
import { applyTheme, initTheme, resolveTheme, useEffectiveTheme, useTheme } from '../../state/theme'
import { canvasColors, CANVAS_DARK, CANVAS_LIGHT, pickPointColor } from '../../lib/canvasColors'

/** Steuerbares matchMedia: `dark` bestimmt prefers-color-scheme, `emit()` löst die change-Listener aus. */
function stubSystemTheme(initialDark: boolean) {
  const state = { dark: initialDark }
  const listeners = new Set<() => void>()
  vi.stubGlobal('matchMedia', (query: string) => ({
    media: query,
    get matches() { return query.includes('dark') ? state.dark : !state.dark },
    addEventListener: (_: string, listener: () => void) => listeners.add(listener),
    removeEventListener: (_: string, listener: () => void) => listeners.delete(listener),
  }))
  return {
    setDark: (dark: boolean) => { state.dark = dark; listeners.forEach((listener) => listener()) },
    listenerCount: () => listeners.size,
  }
}

afterEach(() => {
  vi.unstubAllGlobals()
  useTheme.setState({ theme: 'auto' })
  document.documentElement.removeAttribute('data-theme')
  document.documentElement.style.removeProperty('color-scheme')
})

describe('Theme', () => {
  it('setzt beim Wechsel data-theme UND color-scheme auf <html> (native Controls folgen dem Theme, nicht dem Betriebssystem)', () => {
    stubSystemTheme(false)

    act(() => useTheme.getState().setTheme('dark'))
    expect(document.documentElement.dataset.theme).toBe('dark')
    expect(document.documentElement.style.colorScheme).toBe('dark')

    act(() => useTheme.getState().setTheme('light'))
    expect(document.documentElement.dataset.theme).toBe('light')
    expect(document.documentElement.style.colorScheme).toBe('light')
  })

  it('ein manuell gewähltes Theme gilt auch gegen das Betriebssystem', () => {
    stubSystemTheme(true)   // System: dunkel

    applyTheme('light')

    expect(document.documentElement.dataset.theme).toBe('light')
    expect(document.documentElement.style.colorScheme).toBe('light')
  })

  it('folgt im Auto-Modus prefers-color-scheme — auch live, wenn das Betriebssystem wechselt', () => {
    const system = stubSystemTheme(false)
    useTheme.setState({ theme: 'auto' })
    const stop = initTheme()

    expect(document.documentElement.dataset.theme).toBe('light')
    act(() => system.setDark(true))
    expect(document.documentElement.dataset.theme).toBe('dark')
    expect(document.documentElement.style.colorScheme).toBe('dark')

    // Ein fest gewähltes Theme wird vom Systemwechsel nicht überschrieben.
    act(() => useTheme.getState().setTheme('light'))
    act(() => system.setDark(false))
    act(() => system.setDark(true))
    expect(document.documentElement.dataset.theme).toBe('light')
    stop()
  })

  it('registriert den Systemwechsel-Listener nur einmal (erneutes initTheme ersetzt ihn) und räumt ihn ab', () => {
    const system = stubSystemTheme(false)

    initTheme()
    const stop = initTheme()
    expect(system.listenerCount()).toBe(1)

    stop()
    expect(system.listenerCount()).toBe(0)
  })

  it('kommt ohne matchMedia aus (Tests, alte Browser): "auto" gilt dann als hell', () => {
    vi.stubGlobal('matchMedia', undefined)

    expect(resolveTheme('auto')).toBe('light')
    expect(() => applyTheme('auto')).not.toThrow()
    expect(() => initTheme()()).not.toThrow()
  })

  it('useEffectiveTheme meldet den aufgelösten Wert an Komponenten (z. B. Canvas-Farben) und aktualisiert sich beim Wechsel', () => {
    stubSystemTheme(false)
    applyTheme('light')
    const { result } = renderHook(() => useEffectiveTheme())
    expect(result.current).toBe('light')

    act(() => applyTheme('dark'))

    expect(result.current).toBe('dark')
    expect(canvasColors(result.current)).toBe(CANVAS_DARK)
  })
})

describe('Canvas-Farben', () => {
  it('hell und dunkel sind vollständig und verschieden; Pickpunkte haben je Typ eine eigene Farbe', () => {
    expect(Object.keys(CANVAS_DARK).sort()).toEqual(Object.keys(CANVAS_LIGHT).sort())
    expect(CANVAS_DARK.surface).not.toBe(CANVAS_LIGHT.surface)
    const colors = ['Start', 'End', 'Both'].map((t) => pickPointColor(CANVAS_LIGHT, t as 'Start' | 'End' | 'Both'))
    expect(new Set(colors).size).toBe(3)
  })
})
