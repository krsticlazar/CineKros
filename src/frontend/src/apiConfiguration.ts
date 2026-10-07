const recommendationPath = '/api/recommendations'

export function recommendationEndpoint(configuredOrigin: string | undefined, isDevelopment: boolean): string {
  if (configuredOrigin === undefined || configuredOrigin === '') return recommendationPath

  try {
    if (configuredOrigin !== configuredOrigin.trim() || configuredOrigin.startsWith('//')) throw new Error()

    const url = new URL(configuredOrigin)
    const normalizedOrigin = url.origin
    if (url.username || url.password || url.search || url.hash || url.pathname !== '/') throw new Error()
    if (configuredOrigin !== normalizedOrigin && configuredOrigin !== `${normalizedOrigin}/`) throw new Error()

    if (url.protocol === 'https:') return `${normalizedOrigin}${recommendationPath}`

    const hostname = url.hostname.replace(/^\[|\]$/g, '').toLowerCase()
    const loopback = hostname === 'localhost' || hostname === '127.0.0.1' || hostname === '::1'
    if (isDevelopment && url.protocol === 'http:' && loopback) return `${normalizedOrigin}${recommendationPath}`
  } catch {
    throw new Error('Invalid API origin configuration.')
  }

  throw new Error('Invalid API origin configuration.')
}
