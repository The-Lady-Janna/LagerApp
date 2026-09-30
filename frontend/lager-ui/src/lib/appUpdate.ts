import { create } from 'zustand'

/// <summary>
/// Merker "neue App-Version verfügbar". Wird von der Service-Worker-
/// Registrierung gesetzt, sobald ein neuer Worker die Kontrolle übernimmt;
/// das Banner (components/UpdateBanner.tsx) liest ihn.
/// </summary>
export const useAppUpdate = create<{ updateAvailable: boolean; setUpdateAvailable: (v: boolean) => void }>()((set) => ({
  updateAvailable: false,
  setUpdateAvailable: (v) => set({ updateAvailable: v }),
}))
