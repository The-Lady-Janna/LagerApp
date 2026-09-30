import { create } from 'zustand'
import { persist } from 'zustand/middleware'

/// <summary>
/// Active-warehouse selection for the UI. Persisted to localStorage so the
/// selection survives page reloads. Pages that are warehouse-scoped (Layout,
/// Walls, PickPoints, etc.) read from this store and filter their queries.
/// </summary>
interface ActiveWarehouseState {
  /** null = "all warehouses" (initial state until the user picks one). */
  activeId: string | null
  setActive: (id: string | null) => void
}

export const useActiveWarehouse = create<ActiveWarehouseState>()(
  persist(
    (set) => ({
      activeId: null,
      setActive: (id) => set({ activeId: id }),
    }),
    { name: 'lager.activeWarehouse' }
  )
)
