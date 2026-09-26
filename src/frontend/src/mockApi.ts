import type { RecommendationResponse, SearchRequest, Submit } from './types'

// Synthetic values for frontend-local tests and manual fixture use only.
export const syntheticTen: RecommendationResponse = {
  type: 'movies',
  movies: Array.from({ length: 10 }, (_, index) => ({
    title: `Synthetic Film ${String(index + 1).padStart(2, '0')}`,
    year: index === 2 || index === 5 || index === 8 ? null : 2001 + index,
    imdbUrl: `https://www.imdb.com/title/tt${String(index + 1).padStart(7, '0')}/`,
    posterUrl: index % 2 === 1 ? null : `https://image.tmdb.org/t/p/w500/synthetic-${String(index + 1).padStart(2, '0')}.jpg`,
  })),
  meta: { count: 10, partial: false },
}

export type MockSubmit = Submit
export const submitMock: MockSubmit = (_request: SearchRequest, signal) => new Promise((resolve, reject) => {
  const onAbort = () => reject(new DOMException('Cancelled', 'AbortError'))
  signal.addEventListener('abort', onAbort, { once: true })
  const timer = window.setTimeout(() => {
    signal.removeEventListener('abort', onAbort)
    resolve(syntheticTen)
  }, 80)
  signal.addEventListener('abort', () => window.clearTimeout(timer), { once: true })
})
