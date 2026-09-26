import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { App } from './App'
import type { Movie, RecommendationResponse } from './types'
import { readFileSync } from 'node:fs'

const appStyles = readFileSync('src/App.css', 'utf8')
const movie = (number: number): Movie => ({ title: `Film ${number}`, year: number === 3 ? null : 2000 + number, imdbUrl: `https://www.imdb.com/title/tt${String(number).padStart(7, '0')}/`, posterUrl: number === 2 ? null : `https://image.tmdb.org/t/p/w500/${number}.jpg` })
const response = (count: number): RecommendationResponse => ({ type: 'movies', movies: Array.from({ length: count }, (_, index) => movie(index + 1)), meta: { count, partial: count > 0 && count < 10 } })
const deferred = <T,>() => { let resolve!: (value: T) => void; let reject!: (reason: unknown) => void; const promise = new Promise<T>((next, fail) => { resolve = next; reject = fail }); return { promise, resolve, reject } }

describe('v2 recommendation frontend', () => {
  afterEach(() => { cleanup(); vi.unstubAllGlobals(); vi.restoreAllMocks() })

  it('starts in Serbian and switches locale with pressed state', async () => {
    const user = userEvent.setup(); render(<App submit={vi.fn(() => Promise.resolve(response(10)))} />)
    expect(screen.getByRole('heading', { name: 'VI-Pretraga filmova' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'SR' })).toHaveAttribute('aria-pressed', 'true')
    await user.click(screen.getByRole('button', { name: 'EN' }))
    expect(screen.getByRole('heading', { name: 'AI-Powered Film Search' })).toBeInTheDocument()
  })

  it('uses the relative HTTP endpoint and exact English v2 body without provider calls', async () => {
    const user = userEvent.setup(); const fetchMock = vi.fn().mockResolvedValue({ status: 200, json: () => Promise.resolve(response(10)) }); vi.stubGlobal('fetch', fetchMock)
    render(<App />); await user.click(screen.getByRole('button', { name: 'EN' })); await user.type(screen.getByRole('textbox'), '  quiet mystery  '); await user.click(screen.getByRole('button', { name: 'Send' }))
    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(1))
    expect(fetchMock).toHaveBeenCalledWith('/api/recommendations', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ language: 'en', message: 'quiet mystery' }), signal: expect.any(AbortSignal) })
    expect(JSON.stringify(fetchMock.mock.calls)).not.toMatch(/gemini|tmdb/i)
    await screen.findByRole('link', { name: 'Open Film 1 on IMDb' })
  })

  it('runs the approved SR trigger through loading, ten cards, and New search', async () => {
    let resolveFetch!: (value: { status: number; json: () => Promise<RecommendationResponse> }) => void
    const fetchMock = vi.fn(() => new Promise<{ status: number; json: () => Promise<RecommendationResponse> }>((resolve) => { resolveFetch = resolve }))
    vi.stubGlobal('fetch', fetchMock)
    const user = userEvent.setup(); render(<App />)
    const trigger = 'Hoću mračan SF posle 2010. do dva sata.'
    await user.type(screen.getByRole('textbox'), trigger); await user.click(screen.getByRole('button', { name: 'Pošalji' }))
    expect(screen.getByRole('status')).toHaveTextContent('Tražimo filmove…')
    expect(fetchMock).toHaveBeenCalledWith('/api/recommendations', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ language: 'sr', message: trigger }), signal: expect.any(AbortSignal) })
    resolveFetch({ status: 200, json: () => Promise.resolve(response(10)) })
    await waitFor(() => expect(screen.getAllByRole('link', { name: /Otvori Film/ })).toHaveLength(10))
    expect(screen.getAllByRole('link', { name: /Otvori Film/ }).every((card) => card.classList.contains('movie-card'))).toBe(true)
    expect(screen.queryByText('Prikazani su svi pronađeni filmovi koji ispunjavaju uslove.')).not.toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'EN' })); await user.click(screen.getByRole('button', { name: 'New search' }))
    expect(await screen.findByRole('heading', { name: 'AI-Powered Film Search' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'EN' })).toHaveAttribute('aria-pressed', 'true')
    expect(await screen.findByRole('textbox')).toHaveValue('')
  })

  it('keeps idle rotation on the logo and hover scale on its wrapper', () => {
    expect(appStyles).toContain('animation: logo-idle 7s ease-in-out infinite;')
    expect(appStyles).not.toMatch(/\.brand\s*\{[^}]*animation:\s*logo-idle/s)
    expect(appStyles).toContain('.brand:hover {')
    expect(appStyles).toContain('transform: scale(1.04)')
    expect(appStyles).not.toContain('.brand:hover .brand-mark')
    expect(appStyles).toContain('0%, 30% {')
    expect(appStyles).toContain('12% {')
    expect(appStyles).toContain('20% {')
    expect(appStyles).toContain('24% {')
    expect(appStyles).toContain('100% {')
    expect(appStyles).toContain('.brand-mark,')
    expect(appStyles).toContain('transition: none;')
  })

  it('sends immediately on Enter, keeps Shift+Enter as newline, and displays loading transition', async () => {
    const pending = deferred<RecommendationResponse>(); const submit = vi.fn(() => pending.promise); const user = userEvent.setup(); render(<App submit={submit} />)
    const input = screen.getByRole('textbox'); await user.type(input, 'quiet{Shift>}{Enter}{/Shift}mystery'); expect(input).toHaveValue('quiet\nmystery')
    await user.keyboard('{Enter}')
    expect(submit).toHaveBeenCalledWith({ language: 'sr', message: 'quiet\nmystery' }, expect.any(AbortSignal))
    expect(screen.getByRole('status')).toHaveTextContent('Tražimo filmove…')
    expect(screen.getByRole('textbox')).toHaveAttribute('readonly')
    pending.resolve(response(10))
    await screen.findByRole('link', { name: 'Otvori Film 1 na IMDb-u' })
  })

  it('shows backend localized alert verbatim and retry restores input, locale, and keyboard focus', async () => {
    const user = userEvent.setup(); const submit = vi.fn().mockResolvedValue({ status: 422, body: { type: 'alert', alert: { code: 'UNSUPPORTED_REQUEST', message: 'We cannot reliably check one required condition yet. Please revise your request.' } } }); render(<App submit={submit} />)
    await user.click(screen.getByRole('button', { name: 'EN' })); const input = screen.getByRole('textbox'); await user.type(input, 'film after 2020'); await user.click(screen.getByRole('button', { name: 'Send' }))
    const dialog = await screen.findByRole('alertdialog'); expect(dialog).toHaveTextContent('We cannot reliably check one required condition yet. Please revise your request.')
    await user.click(screen.getByRole('button', { name: 'SR' })); expect(dialog).toHaveTextContent('We cannot reliably check one required condition yet. Please revise your request.')
    const retry = screen.getByRole('button', { name: 'Pokušaj ponovo' }); expect(retry).toHaveFocus(); await user.keyboard('{Enter}')
    expect(screen.getByRole('textbox')).toHaveValue('film after 2020'); expect(screen.getByRole('textbox')).toHaveFocus()
  })

  it('parses business alerts on non-2xx HTTP responses', async () => {
    const user = userEvent.setup(); const fetchMock = vi.fn().mockResolvedValue({ ok: false, status: 422, json: () => Promise.resolve({ type: 'alert', alert: { code: 'QUERY_UNCLEAR', message: 'Napiši malo jasnije kakav film tražiš.' } }) }); vi.stubGlobal('fetch', fetchMock)
    render(<App />); await user.type(screen.getByRole('textbox'), 'film'); await user.click(screen.getByRole('button', { name: 'Pošalji' }))
    expect(await screen.findByRole('alertdialog')).toHaveTextContent('Napiši malo jasnije kakav film tražiš.')
  })

  it('rejects HTTP/body status mismatches and unknown business alerts', async () => {
    const scenarios = [
      { status: 503, body: response(1) },
      { status: 200, body: { type: 'alert', alert: { code: 'QUERY_UNCLEAR', message: 'untrusted message' } } },
      { status: 422, body: { type: 'alert', alert: { code: 'NO_RESULTS', message: 'untrusted message' } } },
      { status: 422, body: { type: 'alert', alert: { code: 'NEW_CODE', message: 'untrusted message' } } },
      { status: 400, body: { type: 'alert', alert: { code: 'INVALID_REQUEST', message: '' } } },
    ]
    for (const scenario of scenarios) {
      const user = userEvent.setup(); const fetchMock = vi.fn().mockResolvedValue({ status: scenario.status, json: () => Promise.resolve(scenario.body) }); vi.stubGlobal('fetch', fetchMock)
      const { unmount } = render(<App />); await user.type(screen.getByRole('textbox'), 'film'); await user.click(screen.getByRole('button', { name: 'Pošalji' }))
      expect(await screen.findByRole('alert')).toHaveTextContent('Došlo je do greške.')
      expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument(); unmount(); vi.unstubAllGlobals()
    }
  })

  it.each([
    [400, 'INVALID_REQUEST', 'Unesi ispravan zahtev za filmove.'],
    [422, 'NOT_MOVIE_REQUEST', 'Napiši zahtev za preporuku filma.'],
    [200, 'NO_RESULTS', 'Nema filmova koji ispunjavaju sve obavezne uslove.'],
  ])('accepts business alert %s/%s only with its contract status and copy', async (status, code, message) => {
    const user = userEvent.setup(); render(<App submit={() => Promise.resolve({ status, body: { type: 'alert', alert: { code, message } } })} />)
    await user.type(screen.getByRole('textbox'), 'film'); await user.click(screen.getByRole('button', { name: 'Pošalji' }))
    expect(await screen.findByRole('alertdialog')).toHaveTextContent(message)
  })

  it.each([
    [429, 'RATE_LIMITED'],
    [502, 'PARSER_INVALID_RESPONSE'],
    [503, 'PROVIDER_UNAVAILABLE'],
    [503, 'SEARCH_UNAVAILABLE'],
    [500, 'INTERNAL_ERROR'],
  ])('accepts technical error %s/%s only at its contract status', async (status, code) => {
    const user = userEvent.setup(); render(<App submit={() => Promise.resolve({ status, body: { error: { code } } })} />)
    await user.type(screen.getByRole('textbox'), 'film'); await user.click(screen.getByRole('button', { name: 'Pošalji' }))
    expect(await screen.findByRole('alert')).not.toHaveTextContent(code)
  })

  it('keeps the business alert modal-contained, handles Escape, and restores prompt focus', async () => {
    const user = userEvent.setup(); render(<App submit={() => Promise.resolve({ status: 422, body: { type: 'alert', alert: { code: 'QUERY_UNCLEAR', message: 'Napiši malo jasnije kakav film tražiš.' } } })} />)
    await user.type(screen.getByRole('textbox'), 'film'); await user.click(screen.getByRole('button', { name: 'Pošalji' }))
    const dialog = await screen.findByRole('alertdialog'); expect(screen.getByRole('main')).toHaveAttribute('inert')
    const retry = screen.getByRole('button', { name: 'Pokušaj ponovo' }); expect(retry).toHaveFocus(); await user.tab(); expect(retry).toHaveFocus()
    await user.keyboard('{Escape}'); expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument(); expect(screen.getByRole('main')).not.toHaveAttribute('inert'); expect(screen.getByRole('textbox')).toHaveFocus()
    expect(dialog).not.toBeInTheDocument()
  })

  it('localizes sanitized technical failures and never exposes raw response details', async () => {
    const user = userEvent.setup(); render(<App submit={() => Promise.resolve({ status: 429, body: { error: { code: 'RATE_LIMITED', detail: 'secret stack payload' } } })} />)
    await user.type(screen.getByRole('textbox'), 'film'); await user.click(screen.getByRole('button', { name: 'Pošalji' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Trenutno ima previše zahteva.')
    expect(screen.getByRole('alert')).not.toHaveTextContent(/RATE_LIMITED|secret stack payload/)
    await user.click(screen.getByRole('button', { name: 'EN' })); expect(screen.getByRole('alert')).toHaveTextContent('There are too many requests right now.')
  })

  it.each([1, 9, 10])('renders %s valid results and follows meta.partial', async (count) => {
    const user = userEvent.setup(); render(<App submit={() => Promise.resolve(response(count))} />); await user.type(screen.getByRole('textbox'), 'film'); await user.click(screen.getByRole('button', { name: 'Pošalji' }))
    await waitFor(() => expect(screen.getAllByRole('link', { name: /Otvori Film/ })).toHaveLength(count))
    expect(screen.getAllByRole('link', { name: /Otvori Film/ })[0]).toHaveClass('movie-card')
    const notice = screen.queryByText('Prikazani su svi pronađeni filmovi koji ispunjavaju uslove.')
    if (count < 10) expect(notice).toBeInTheDocument(); else expect(notice).not.toBeInTheDocument()
  })

  it.each([0, 11])('rejects invalid movie count %s as a generic technical response', async (count) => {
    const user = userEvent.setup(); render(<App submit={() => Promise.resolve(response(count))} />); await user.type(screen.getByRole('textbox'), 'film'); await user.click(screen.getByRole('button', { name: 'Pošalji' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Došlo je do greške.')
    expect(screen.queryByRole('link', { name: /Otvori Film/ })).not.toBeInTheDocument()
  })

  it('rejects server metadata inconsistency instead of guessing partial status', async () => {
    const user = userEvent.setup(); render(<App submit={() => Promise.resolve({ ...response(1), meta: { count: 1, partial: false } })} />)
    await user.type(screen.getByRole('textbox'), 'film'); await user.click(screen.getByRole('button', { name: 'Pošalji' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Došlo je do greške.')
  })

  it('rejects duplicate IMDb URLs and posters outside the TMDB image path', async () => {
    const duplicate = [movie(1), { ...movie(1), title: 'Different identity' }]
    const badHost = [{ ...movie(1), posterUrl: 'https://evil.example/t/p/w500/poster.jpg' }]
    const badPath = [{ ...movie(1), posterUrl: 'https://image.tmdb.org/unsafe/w500/poster.jpg' }]
    for (const movies of [duplicate, badHost, badPath]) {
      const user = userEvent.setup(); render(<App submit={() => Promise.resolve({ type: 'movies', movies, meta: { count: movies.length, partial: true } })} />)
      await user.type(screen.getByRole('textbox'), 'film'); await user.click(screen.getByRole('button', { name: 'Pošalji' }))
      expect(await screen.findByRole('alert')).toHaveTextContent('Došlo je do greške.')
      cleanup()
    }
  })

  it('asserts prompt exit animation override comes after the baseline panel animation', () => {
    const baseline = appStyles.lastIndexOf('.search-panel {')
    const override = appStyles.lastIndexOf('.search-panel.prompt-exit')
    expect(baseline).toBeGreaterThan(-1); expect(override).toBeGreaterThan(baseline)
    expect(appStyles.slice(override, appStyles.indexOf('}', override))).toContain('animation: prompt-exit')
  })

  it('restores prompt after New search without reload and preserves locale', async () => {
    const user = userEvent.setup(); render(<App submit={() => Promise.resolve(response(1))} />)
    await user.click(screen.getByRole('button', { name: 'EN' })); await user.type(screen.getByRole('textbox'), 'film'); await user.click(screen.getByRole('button', { name: 'Send' }))
    await screen.findByRole('button', { name: 'New search' }); await user.click(screen.getByRole('button', { name: 'New search' }))
    expect(await screen.findByRole('heading', { name: 'AI-Powered Film Search' })).toBeInTheDocument()
    expect(await screen.findByRole('textbox')).toHaveValue('')
    expect(screen.queryByRole('link', { name: /Open Film/ })).not.toBeInTheDocument()
  })

  it('keeps secure IMDb links, null-year behavior, and poster fallback', async () => {
    const user = userEvent.setup(); render(<App submit={() => Promise.resolve(response(3))} />); await user.type(screen.getByRole('textbox'), 'film'); await user.click(screen.getByRole('button', { name: 'Pošalji' }))
    const link = await screen.findByRole('link', { name: 'Otvori Film 3 na IMDb-u' }); expect(link).toHaveAttribute('rel', 'noopener noreferrer'); expect(link).not.toHaveTextContent('(null)'); expect(screen.getByText('Poster nije dostupan')).toBeInTheDocument()
  })

  it('uses generic fallback for malformed payloads without exposing them', async () => {
    const user = userEvent.setup(); render(<App submit={() => Promise.resolve({ raw: 'do not show this' })} />); await user.type(screen.getByRole('textbox'), 'film'); await user.click(screen.getByRole('button', { name: 'Pošalji' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Došlo je do greške. Probaj ponovo.')
    expect(screen.getByRole('alert')).not.toHaveTextContent('do not show this')
  })

  it('aborts an active request on unmount and ignores its cancellation', async () => {
    let signal: AbortSignal | undefined; const submit = vi.fn((_request, requestSignal: AbortSignal) => { signal = requestSignal; return new Promise((_resolve, reject) => requestSignal.addEventListener('abort', () => reject(new DOMException('Cancelled', 'AbortError')), { once: true })) })
    const { unmount } = render(<App submit={submit} />); fireEvent.change(screen.getByRole('textbox'), { target: { value: 'film' } }); fireEvent.submit(screen.getByRole('textbox').closest('form')!); expect(signal?.aborted).toBe(false); unmount(); expect(signal?.aborted).toBe(true)
  })

  it('prevents a stale A response from replacing newer request B', async () => {
    const first = deferred<RecommendationResponse>(); const second = deferred<RecommendationResponse>(); const signals: AbortSignal[] = []
    const submit = vi.fn((_request, signal: AbortSignal) => { signals.push(signal); return signals.length === 1 ? first.promise : second.promise })
    render(<App submit={submit} />); fireEvent.change(screen.getByRole('textbox'), { target: { value: 'film' } }); const form = screen.getByRole('textbox').closest('form')!
    fireEvent.submit(form)
    const loadingForm = screen.getByLabelText('Opiši kakve filmove tražiš').closest('form')!
    fireEvent.submit(loadingForm)
    expect(signals[0].aborted).toBe(true); expect(submit).toHaveBeenCalledTimes(2)
    second.resolve(response(1)); await screen.findByRole('link', { name: 'Otvori Film 1 na IMDb-u' })
    first.resolve(response(10)); await new Promise((resolve) => setTimeout(resolve, 0))
    expect(screen.getAllByRole('link', { name: /Otvori Film/ })).toHaveLength(1)
  })

  it('keeps keyboard focus and reduced-motion support', () => {
    render(<App />); const input = screen.getByRole('textbox'); input.focus(); expect(document.activeElement).toBe(input)
    expect(appStyles).toContain('@media (prefers-reduced-motion: reduce)'); expect(appStyles).toContain('.loading img'); expect(appStyles).toContain('.movies-exit')
  })

  it('preserves the existing rules panel and localized footer', async () => {
    const user = userEvent.setup(); render(<App />); await user.click(screen.getByRole('button', { name: '↓ Pravila i ograničenja ↓' }))
    expect(screen.getByText('Katalog je ograničen na 9.730 filmova iz istraživačkog skupa podataka i ne predstavlja kompletnu bazu svih filmova.')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'EN' })); expect(screen.getByRole('contentinfo')).toHaveTextContent('© 2026 CineKros. All rights reserved. CineKros is an open-source project.')
    expect(screen.getByRole('link', { name: 'open-source' })).toHaveAttribute('rel', 'noopener noreferrer')
  })
})
