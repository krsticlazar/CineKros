export type Locale = 'sr' | 'en'
export type Movie = { title: string; year: number | null; imdbUrl: string; posterUrl: string | null }
export type BusinessAlertCode = 'INVALID_REQUEST' | 'QUERY_UNCLEAR' | 'NOT_MOVIE_REQUEST' | 'UNSUPPORTED_REQUEST' | 'NO_RESULTS'
export type TechnicalErrorCode = 'RATE_LIMITED' | 'PARSER_INVALID_RESPONSE' | 'PROVIDER_UNAVAILABLE' | 'SEARCH_UNAVAILABLE' | 'INTERNAL_ERROR'
export type RecommendationResponse =
  | { type: 'alert'; alert: { code: BusinessAlertCode; message: string } }
  | { type: 'movies'; movies: Movie[]; meta: { count: number; partial: boolean } }
  | { error: { code: TechnicalErrorCode | string } }
export type SearchRequest = { language: Locale; message: string }
export type HttpResult = { status: number; body: unknown }
export type Submit = (request: SearchRequest, signal: AbortSignal) => Promise<HttpResult | unknown>
