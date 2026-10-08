import { useEffect, useRef, useState } from 'react'
import './App.css'
import type { BusinessAlertCode, Locale, Movie, SearchRequest, Submit, TechnicalErrorCode } from './types'
import { NeuralNetworkCanvas } from './NeuralNetworkCanvas'
import { LanguageToggle } from './LanguageToggle'
import logoUrl from './assets/CineKros_logo.svg'
import logoStrokeUrl from './assets/CineKros_logo_stroke.svg'
import cameraUrl from './assets/film-camera.svg'
import { recommendationEndpoint } from './apiConfiguration'

const copy = {
  sr: { subtitle: 'VI-Pretraga filmova', label: 'Opiši kakve filmove tražiš', queryLabel: 'Upit:', submit: 'Pošalji', loading: 'Tražimo filmove…', fallback: 'Poster nije dostupan', open: (title: string) => `Otvori ${title} na IMDb-u`, partial: 'Prikazani su svi pronađeni filmovi koji ispunjavaju uslove.', retry: 'Pokušaj ponovo', newSearch: 'Nova pretraga', rulesClosed: '↓ Pravila i Ograničenja ↓', rulesOpen: '↑ Pravila i Ograničenja ↑', limitations: ['Katalog je ograničen na 9.730 filmova iz istraživačkog skupa podataka i ne predstavlja kompletnu bazu svih filmova.', 'Katalog se ne ažurira u realnom vremenu, pa nova filmska izdanja mogu nedostajati.', 'Preporuke se zasnivaju na dostupnim metapodacima, Tag Genome oznakama i semantičkoj sličnosti. Sistem ne analizira sam video-sadržaj filma.', 'Strogi kriterijumi navedeni u upitu primenjuju se kao obavezni filteri. Ako nijedan film ne ispunjava sve uslove, rezultat može biti prazan.', 'Po jednom upitu prikazuje se najviše 10 preporučenih filmova.', 'Za pojedine filmove mogu nedostajati spoljni metapodaci, kao što su trajanje, originalni jezik ili poster.', 'Trenutna verzija ne koristi korisnički profil, istoriju gledanja niti višekružni razgovor za personalizaciju preporuka.', 'Ocene i podaci o popularnosti potiču iz MovieLens skupa podataka i ne moraju odgovarati trenutnim ocenama na drugim filmskim servisima.', 'MovieLens ocene su na skali od 1 do 5. Ako unesete ocenu veću od 5, sistem je deli sa 2 i koristi tako prilagođen prag za pretragu.'], footerBefore: '© 2026 CineKros. Sva prava zadržana. CineKros je ', footerAfter: ' projekat.', errors: { RATE_LIMITED: 'Trenutno ima previše zahteva. Probaj malo kasnije.', PARSER_INVALID_RESPONSE: 'Nismo uspeli da obradimo odgovor servisa. Probaj ponovo.', PROVIDER_UNAVAILABLE: 'Servis trenutno nije dostupan. Probaj kasnije.', SEARCH_UNAVAILABLE: 'Pretraga trenutno nije dostupna. Probaj kasnije.', INTERNAL_ERROR: 'Došlo je do greške. Probaj ponovo.' } },
  en: { subtitle: 'AI-Powered Film Search', label: 'Describe the movies you are looking for', queryLabel: 'Query:', submit: 'Send', loading: 'Finding movies…', fallback: 'Poster unavailable', open: (title: string) => `Open ${title} on IMDb`, partial: 'Showing all available movies that meet your requirements.', retry: 'Try again', newSearch: 'New search', rulesClosed: '↓ Rules & Limitations ↓', rulesOpen: '↑ Rules & Limitations ↑', limitations: ['The catalogue is limited to 9,730 movies from the research dataset and does not represent a complete database of all movies.', 'The catalogue is not updated in real time, so newly released movies may be missing.', 'Recommendations are based on available metadata, Tag Genome tags and semantic similarity. The system does not analyse the actual video content of a movie.', 'Strict criteria stated in the query are applied as mandatory filters. If no movie satisfies all conditions, the result may be empty.', 'A maximum of 10 recommended movies is displayed per query.', 'Some movies may have missing external metadata, such as runtime, original language or poster artwork.', 'The current version does not use a user profile, viewing history or multi-turn conversation to personalize recommendations.', 'Ratings and popularity data originate from the MovieLens dataset and may differ from current ratings on other movie services.', 'MovieLens ratings use a 1–5 scale. If you enter a rating above 5, the system divides it by two and uses the adjusted threshold for the search.'], footerBefore: '© 2026 CineKros. All rights reserved. CineKros is an ', footerAfter: ' project.', errors: { RATE_LIMITED: 'There are too many requests right now. Please try again later.', PARSER_INVALID_RESPONSE: 'We could not process the service response. Please try again.', PROVIDER_UNAVAILABLE: 'The service is currently unavailable. Please try again later.', SEARCH_UNAVAILABLE: 'Search is currently unavailable. Please try again later.', INTERNAL_ERROR: 'Something went wrong. Please try again.' } },
} as const

function errorMessage(code: string, locale: Locale) {
  return copy[locale].errors[code as TechnicalErrorCode] ?? copy[locale].errors.INTERNAL_ERROR
}

async function submitHttp(request: SearchRequest, signal: AbortSignal): Promise<{ status: number; body: unknown }> {
  const endpoint = recommendationEndpoint(import.meta.env.VITE_API_BASE_URL, import.meta.env.DEV)
  const response = await fetch(endpoint, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(request),
    signal,
  })
  return { status: response.status, body: await response.json() }
}

function isMovie(value: unknown): value is Movie {
  if (!value || typeof value !== 'object') return false
  const movie = value as Record<string, unknown>
  return typeof movie.title === 'string' && movie.title.trim().length > 0
    && (movie.year === null || Number.isInteger(movie.year))
    && typeof movie.imdbUrl === 'string' && /^https:\/\/www\.imdb\.com\/title\/tt\d+\/$/.test(movie.imdbUrl)
    && (movie.posterUrl === null || (typeof movie.posterUrl === 'string' && /^https:\/\/image\.tmdb\.org\/t\/p\/(?:w\d+|original)\/[A-Za-z0-9._~-]+$/.test(movie.posterUrl)))
}

const businessMessages: Record<Locale, Record<BusinessAlertCode, string>> = {
  sr: {
    INVALID_REQUEST: 'Unesi ispravan zahtev za filmove.',
    QUERY_UNCLEAR: 'Napiši malo jasnije kakav film tražiš.',
    LANGUAGE_MISMATCH: 'Upit nije na izabranom jeziku. Promenite jezik ili preformulišite upit.',
    NOT_MOVIE_REQUEST: 'Napiši zahtev za preporuku filma.',
    UNSUPPORTED_REQUEST: 'Jedan obavezan uslov trenutno ne možemo pouzdano da proverimo. Izmeni upit.',
    NO_RESULTS: 'Nema filmova koji ispunjavaju sve obavezne uslove.',
  },
  en: {
    INVALID_REQUEST: 'Enter a valid movie request.',
    QUERY_UNCLEAR: 'Describe the movie you want more clearly.',
    LANGUAGE_MISMATCH: 'The query is not in the selected language. Change the language or rephrase your query.',
    NOT_MOVIE_REQUEST: 'Enter a movie recommendation request.',
    UNSUPPORTED_REQUEST: 'We cannot reliably check one required condition yet. Please revise your request.',
    NO_RESULTS: 'No movies meet all the required conditions.',
  },
}

const businessStatus: Record<BusinessAlertCode, number> = {
  INVALID_REQUEST: 400,
  QUERY_UNCLEAR: 422,
  LANGUAGE_MISMATCH: 422,
  NOT_MOVIE_REQUEST: 422,
  UNSUPPORTED_REQUEST: 422,
  NO_RESULTS: 200,
}

const technicalStatus: Record<TechnicalErrorCode, number> = {
  RATE_LIMITED: 429,
  PARSER_INVALID_RESPONSE: 502,
  PROVIDER_UNAVAILABLE: 503,
  SEARCH_UNAVAILABLE: 503,
  INTERNAL_ERROR: 500,
}

function MovieCard({ movie, locale, index }: { movie: Movie; locale: Locale; index: number }) {
  const [failed, setFailed] = useState(!movie.posterUrl)
  return (
    <a className="movie-card" style={{ animationDelay: `${Math.min(index, 9) * 45}ms` }} href={movie.imdbUrl} target="_blank" rel="noopener noreferrer" aria-label={copy[locale].open(movie.title)}>
      <div className="poster-frame">
        {!failed && <img src={movie.posterUrl ?? undefined} alt="" onError={() => setFailed(true)} />}
        {failed && <div className="poster-fallback"><img src={logoStrokeUrl} alt="" /><span>{copy[locale].fallback}</span></div>}
        <span className="movie-title">{movie.title}{movie.year === null ? '' : ` (${movie.year})`}</span>
      </div>
    </a>
  )
}

type View = 'prompt' | 'loading' | 'movies' | 'leaving-results' | 'error'

export function App({ submit = submitHttp }: { submit?: Submit }) {
  const [locale, setLocale] = useState<Locale>('sr')
  const [rulesOpen, setRulesOpen] = useState(false)
  const [query, setQuery] = useState('')
  const [submittedQuery, setSubmittedQuery] = useState('')
  const [movies, setMovies] = useState<Movie[]>([])
  const [partial, setPartial] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [businessAlert, setBusinessAlert] = useState<string | null>(null)
  const [view, setView] = useState<View>('prompt')
  const sequence = useRef(0)
  const controller = useRef<AbortController | null>(null)
  const queryRef = useRef<HTMLTextAreaElement>(null)
  const retryRef = useRef<HTMLButtonElement>(null)
  const restorePromptFocus = useRef(false)
  const newSearchTimer = useRef<number | undefined>(undefined)
  const t = copy[locale]

  useEffect(() => {
    if (businessAlert !== null) retryRef.current?.focus()
    else if (restorePromptFocus.current && view === 'prompt') {
      queryRef.current?.focus()
      restorePromptFocus.current = false
    }
  }, [businessAlert, locale, view])
  useEffect(() => () => {
    controller.current?.abort()
    window.clearTimeout(newSearchTimer.current)
  }, [])

  const send = () => {
    controller.current?.abort()
    const current = ++sequence.current
    const next = new AbortController()
    controller.current = next
    const request: SearchRequest = { language: locale, message: query.trim() }
    setSubmittedQuery(query)
    setView('loading')
    setMovies([])
    setPartial(false)
    setError(null)
    setBusinessAlert(null)
    void submit(request, next.signal).then((payload: unknown) => {
      if (current !== sequence.current) return
      if (!payload || typeof payload !== 'object') throw new Error('malformed')
      const result = payload as Record<string, unknown>
      const status = typeof result.status === 'number' && 'body' in result ? result.status : 200
      const response = (typeof result.status === 'number' && 'body' in result ? result.body : payload) as Record<string, unknown>
      if (!response || typeof response !== 'object') throw new Error('malformed')
      const alert = response.alert && typeof response.alert === 'object' ? response.alert as Record<string, unknown> : null
      if (response.type === 'alert' && alert && typeof alert.code === 'string' && typeof alert.message === 'string') {
        const code = alert.code as BusinessAlertCode
        if (!(code in businessStatus) || status !== businessStatus[code] || alert.message !== businessMessages[request.language][code]) throw new Error('malformed')
        setBusinessAlert(alert.message)
        setView('prompt')
        return
      }
      if (response.type === 'movies' && Array.isArray(response.movies) && response.meta && typeof response.meta === 'object') {
        if (status !== 200) throw new Error('malformed')
        const meta = response.meta as Record<string, unknown>
        const count = response.movies.length
        const imdbUrls = new Set<string>()
        if (count < 1 || count > 10 || !response.movies.every((value) => {
          if (!isMovie(value) || imdbUrls.has(value.imdbUrl)) return false
          imdbUrls.add(value.imdbUrl)
          return true
        }) || meta.count !== count || meta.partial !== (count < 10)) throw new Error('malformed')
        setMovies(response.movies as Movie[])
        setPartial(meta.partial as boolean)
        setView('movies')
        return
      }
      if (response.error && typeof response.error === 'object' && typeof (response.error as Record<string, unknown>).code === 'string') {
        const code = (response.error as Record<string, string>).code as TechnicalErrorCode
        if (!(code in technicalStatus) || status !== technicalStatus[code]) throw new Error('malformed')
        setError(code)
        setView('error')
        return
      }
      throw new Error('malformed')
    }).catch((reason: unknown) => {
      if (current !== sequence.current || (reason instanceof DOMException && reason.name === 'AbortError')) return
      setError('INTERNAL_ERROR')
      setView('error')
    })
  }

  const retry = () => {
    restorePromptFocus.current = true
    setBusinessAlert(null)
    setError(null)
    setView('prompt')
  }

  const newSearch = () => {
    setView('leaving-results')
    newSearchTimer.current = window.setTimeout(() => {
      setMovies([])
      setPartial(false)
      setQuery('')
      setView('prompt')
      queryRef.current?.focus()
    }, 220)
  }

  return (
    <>
      <NeuralNetworkCanvas />
      <div className="camera-decorations" aria-hidden="true">
        <img src={cameraUrl} alt="" className="camera-corner camera-corner-bl" />
        <img src={cameraUrl} alt="" className="camera-corner camera-corner-tr" />
      </div>
      <main className="app-shell" inert={businessAlert !== null}>
        <div className="primary-content">
          <header className="site-header">
            <h1 className="brand-name">CineKros</h1>
            <LanguageToggle locale={locale} setLocale={setLocale} />
          </header>
          {view === 'prompt' && businessAlert === null && (
            <section className="search-panel prompt-enter" aria-labelledby="search-heading">
              <h2 id="search-heading">{t.subtitle}</h2>
              <form onSubmit={(event) => { event.preventDefault(); send() }}>
                <label htmlFor="query">{t.label}</label>
                <div className="search-row">
                  <textarea ref={queryRef} id="query" rows={3} value={query} onChange={(event) => setQuery(event.target.value)} placeholder={t.label} onKeyDown={(event) => {
                    if (event.key === 'Enter' && !event.shiftKey) { event.preventDefault(); event.currentTarget.form?.requestSubmit() }
                  }} />
                  <button type="submit">{t.submit}</button>
                </div>
              </form>
            </section>
          )}
          {view === 'loading' && <>
            <section className="search-panel prompt-exit" aria-labelledby="search-heading">
              <h2 id="search-heading">{t.subtitle}</h2>
              <form onSubmit={(event) => { event.preventDefault(); send() }}>
                <label htmlFor="query-loading">{t.label}</label>
                <div className="search-row"><textarea id="query-loading" rows={3} value={query} readOnly disabled /><button type="submit" disabled>{t.submit}</button></div>
              </form>
            </section>
            <div className="loading loading-enter" role="status"><img src={logoUrl} alt="" />{t.loading}</div>
          </>}
          {(view === 'movies' || view === 'leaving-results') && <>
            <p className="submitted-query">{t.queryLabel} {submittedQuery}</p>
            {partial && <p className="results-summary">{t.partial}</p>}
            <section className={`movie-grid${view === 'leaving-results' ? ' movies-exit' : ''}`} aria-label={t.subtitle}>
              {movies.map((movie, index) => <MovieCard key={movie.imdbUrl} movie={movie} locale={locale} index={index} />)}
            </section>
            {view === 'movies' && <div className="new-search-row"><button type="button" className="new-search-button" onClick={newSearch}>{t.newSearch}</button></div>}
          </>}
          {view === 'error' && error && <div className="technical-error"><p className="message error" role="alert">{errorMessage(error, locale)}</p><button type="button" className="retry-button" onClick={retry}>{t.retry}</button></div>}
        </div>
        <div className="page-bottom">
          <section className="rules-section">
            <button type="button" className="rules-toggle" aria-expanded={rulesOpen} aria-controls="rules-content" onClick={() => setRulesOpen((open) => !open)}>{rulesOpen ? t.rulesOpen : t.rulesClosed}</button>
            <div id="rules-content" className={`rules-content${rulesOpen ? ' is-open' : ''}`} aria-hidden={!rulesOpen} inert={!rulesOpen}><ol>{t.limitations.map((limitation) => <li key={limitation}>{limitation}</li>)}</ol></div>
          </section>
          <footer className="site-footer">{t.footerBefore}<a href="https://github.com/krsticlazar/CineKros" target="_blank" rel="noopener noreferrer">open-source</a>{t.footerAfter}</footer>
        </div>
      </main>
      {businessAlert !== null && <div className="dialog-backdrop"><section className="business-dialog" role="alertdialog" aria-modal="true" aria-labelledby="business-alert-message" onKeyDown={(event) => {
        if (event.key === 'Escape') { event.preventDefault(); retry() }
        if (event.key === 'Tab') { event.preventDefault(); retryRef.current?.focus() }
      }}><p id="business-alert-message">{businessAlert}</p><button ref={retryRef} type="button" className="retry-button" onClick={retry}>{t.retry}</button></section></div>}
    </>
  )
}
