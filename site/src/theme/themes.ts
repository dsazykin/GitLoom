/** The five Mainguard themes, in the app's canonical order. */
export interface ThemeInfo {
  id: string;
  label: string;
  scheme: 'dark' | 'light';
  /** Swatch colors shown in the switcher: window surface + accent. */
  surface: string;
  accent: string;
}

export const THEMES: ThemeInfo[] = [
  { id: 'midnight', label: 'Midnight Watch', scheme: 'dark', surface: '#0F1115', accent: '#8B8BF5' },
  { id: 'daylight', label: 'Day Watch', scheme: 'light', surface: '#EDEFF4', accent: '#6467E8' },
  { id: 'commanddeck', label: 'Command Deck', scheme: 'dark', surface: '#0A0C0E', accent: '#2DD4BF' },
  { id: 'atelier', label: 'Atelier', scheme: 'dark', surface: '#171512', accent: '#D8A25A' },
  { id: 'aurora', label: 'Aurora', scheme: 'dark', surface: '#111322', accent: '#4FD1C5' },
];

export const DEFAULT_THEME = 'midnight';
export const THEME_STORAGE_KEY = 'mainguard-theme';
/** Pre-rename storage key — still read (never written) so early visitors keep their theme. */
export const LEGACY_THEME_STORAGE_KEY = 'mainguard-theme';
