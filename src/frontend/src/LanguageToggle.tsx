import { useRef } from 'react'
import type { Locale } from './types'

interface LanguageToggleProps {
  locale: Locale
  setLocale: (next: Locale) => void
}

export function LanguageToggle({ locale, setLocale }: LanguageToggleProps) {
  const dragStartX = useRef<number | null>(null)

  const handlePointerDown = (e: React.PointerEvent) => {
    dragStartX.current = e.clientX
  }

  const handlePointerMove = (e: React.PointerEvent) => {
    if (dragStartX.current === null) return
    const delta = e.clientX - dragStartX.current
    if (delta > 22 && locale !== 'en') {
      setLocale('en')
      dragStartX.current = null
    } else if (delta < -22 && locale !== 'sr') {
      setLocale('sr')
      dragStartX.current = null
    }
  }

  const handlePointerUp = () => {
    dragStartX.current = null
  }

  return (
    <div
      className={`locale-switcher ${locale === 'en' ? 'is-en' : 'is-sr'}`}
      aria-label="Language"
      onPointerDown={handlePointerDown}
      onPointerMove={handlePointerMove}
      onPointerUp={handlePointerUp}
      onPointerCancel={handlePointerUp}
    >
      <div className="locale-thumb" aria-hidden="true" />
      <button
        type="button"
        className="locale-btn"
        aria-pressed={locale === 'sr'}
        onClick={() => setLocale('sr')}
      >
        <span className="locale-flag" aria-hidden="true">
          <svg viewBox="0 0 24 24" width="18" height="18" focusable="false">
            <clipPath id="sr-flag-circle">
              <circle cx="12" cy="12" r="11" />
            </clipPath>
            <g clipPath="url(#sr-flag-circle)">
              <rect x="0" y="1" width="24" height="7.33" fill="#C6363C" />
              <rect x="0" y="8.33" width="24" height="7.33" fill="#0C4076" />
              <rect x="0" y="15.66" width="24" height="7.34" fill="#FFFFFF" />
              <path
                d="M7 8.5C7 7.8 8 7 9.5 7s2.5.8 2.5 1.5v4c0 1.8-1.2 3.2-2.5 3.8-1.3-.6-2.5-2-2.5-3.8v-4z"
                fill="#C6363C"
                stroke="#FFFFFF"
                strokeWidth="0.6"
              />
              <path d="M8.5 9.5h2M9.5 8.5v3.5" stroke="#FFFFFF" strokeWidth="0.6" />
            </g>
            <circle cx="12" cy="12" r="11" fill="none" stroke="rgba(5, 51, 94, 0.18)" strokeWidth="1" />
          </svg>
        </span>
        <span className="locale-label">SR</span>
      </button>

      <button
        type="button"
        className="locale-btn"
        aria-pressed={locale === 'en'}
        onClick={() => setLocale('en')}
      >
        <span className="locale-flag" aria-hidden="true">
          <svg viewBox="0 0 24 24" width="18" height="18" focusable="false">
            <clipPath id="uk-flag-circle">
              <circle cx="12" cy="12" r="11" />
            </clipPath>
            <g clipPath="url(#uk-flag-circle)">
              <rect width="24" height="24" fill="#012169" />
              <path d="M0 0 L24 24 M24 0 L0 24" stroke="#FFFFFF" strokeWidth="4" />
              <path d="M0 0 L24 24 M24 0 L0 24" stroke="#C8102E" strokeWidth="1.8" />
              <path d="M12 0 v24 M0 12 h24" stroke="#FFFFFF" strokeWidth="6" />
              <path d="M12 0 v24 M0 12 h24" stroke="#C8102E" strokeWidth="3.6" />
            </g>
            <circle cx="12" cy="12" r="11" fill="none" stroke="rgba(5, 51, 94, 0.18)" strokeWidth="1" />
          </svg>
        </span>
        <span className="locale-label">EN</span>
      </button>
    </div>
  )
}
