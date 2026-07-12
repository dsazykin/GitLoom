import { createContext, useCallback, useContext, useEffect, useState, type ReactNode } from 'react';
import { DEFAULT_THEME, THEMES, THEME_STORAGE_KEY } from './themes';

interface ThemeContextValue {
  theme: string;
  setTheme: (id: string) => void;
}

const ThemeContext = createContext<ThemeContextValue>({
  theme: DEFAULT_THEME,
  setTheme: () => {},
});

function readStoredTheme(): string {
  try {
    const stored = localStorage.getItem(THEME_STORAGE_KEY);
    if (stored && THEMES.some((t) => t.id === stored)) return stored;
  } catch {
    // storage unavailable (private mode) — fall through
  }
  return DEFAULT_THEME;
}

export function ThemeProvider({ children }: { children: ReactNode }) {
  const [theme, setThemeState] = useState(readStoredTheme);

  useEffect(() => {
    document.documentElement.dataset.theme = theme;
    const info = THEMES.find((t) => t.id === theme);
    document
      .querySelector('meta[name="theme-color"]')
      ?.setAttribute('content', info?.surface ?? '#0F1115');
  }, [theme]);

  const setTheme = useCallback((id: string) => {
    if (!THEMES.some((t) => t.id === id)) return;
    setThemeState(id);
    try {
      localStorage.setItem(THEME_STORAGE_KEY, id);
    } catch {
      // best-effort persistence only
    }
  }, []);

  return <ThemeContext.Provider value={{ theme, setTheme }}>{children}</ThemeContext.Provider>;
}

// eslint-disable-next-line react-refresh/only-export-components
export function useTheme(): ThemeContextValue {
  return useContext(ThemeContext);
}
