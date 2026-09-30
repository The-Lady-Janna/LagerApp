import { useCallback, useEffect, useRef, useState } from 'react'

/** So lange bleibt eine Statusmeldung des Editors sichtbar. */
const FLASH_MS = 2500

/// <summary>
/// Kurze Statusmeldung, die sich selbst ausblendet. Der Timer liegt in einer Ref und wird beim Unmount
/// aufgeräumt; jede neue Meldung startet ihn neu — so löscht der Timer einer älteren Meldung nie eine
/// neuere vorzeitig (vorher setzte jeder Aufruf einen eigenen, nie gestoppten Timeout).
/// </summary>
export function useFlashMessage(durationMs = FLASH_MS) {
  const [message, setMessage] = useState<string | null>(null)
  const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined)

  const flash = useCallback((next: string) => {
    clearTimeout(timer.current)
    setMessage(next)
    timer.current = setTimeout(() => setMessage(null), durationMs)
  }, [durationMs])

  useEffect(() => () => clearTimeout(timer.current), [])

  return { message, flash }
}
