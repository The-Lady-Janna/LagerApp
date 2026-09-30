import { useId, useState, type ReactNode } from 'react'

interface Props {
  title: ReactNode
  defaultOpen?: boolean
  /** Persistence key — when set, open/closed state survives reloads via localStorage. */
  storageKey?: string
  /** Optional content rendered to the right of the title (e.g. counts, action). */
  headerRight?: ReactNode
  children: ReactNode
}

/**
 * A `.card` wrapper whose title bar toggles the body open/closed.
 * Der Titel ist ein echter Button (Tastatur: Tab + Enter/Leertaste) mit aria-expanded/aria-controls;
 * `headerRight` steht daneben, nicht darin (kein Button im Button).
 */
export function CollapsibleCard({ title, defaultOpen = true, storageKey, headerRight, children }: Props) {
  const bodyId = useId()
  const initial = (() => {
    if (!storageKey) return defaultOpen
    try {
      const stored = window.localStorage.getItem(`collapse:${storageKey}`)
      if (stored === 'open') return true
      if (stored === 'closed') return false
    } catch { /* private mode etc. */ }
    return defaultOpen
  })()

  const [open, setOpen] = useState(initial)

  const toggle = () => {
    setOpen((v) => {
      const next = !v
      if (storageKey) {
        try { window.localStorage.setItem(`collapse:${storageKey}`, next ? 'open' : 'closed') } catch { /* ignore */ }
      }
      return next
    })
  }

  return (
    <div className="card">
      <div className="collapsible-header">
        <h3 style={{ margin: 0, flex: 1, minWidth: 0 }}>
          <button type="button" className="collapsible-toggle" onClick={toggle} aria-expanded={open} aria-controls={open ? bodyId : undefined}>
            <span className="collapsible-caret" data-open={open} aria-hidden="true">▶</span>
            {title}
          </button>
        </h3>
        {headerRight && <div>{headerRight}</div>}
      </div>
      {open && <div id={bodyId} className="collapsible-body">{children}</div>}
    </div>
  )
}
