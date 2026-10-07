import { describe, expect, it } from 'vitest'
import { recommendationEndpoint } from './apiConfiguration'

describe('recommendationEndpoint', () => {
  it('keeps the relative local endpoint when the origin is unset or empty', () => {
    expect(recommendationEndpoint(undefined, false)).toBe('/api/recommendations')
    expect(recommendationEndpoint('', false)).toBe('/api/recommendations')
  })

  it.each(['https://api.example.com', 'https://api.example.com/'])('normalizes HTTPS origin %s to the recommendation endpoint', (origin) => {
    expect(recommendationEndpoint(origin, false)).toBe('https://api.example.com/api/recommendations')
  })

  it.each([
    'https://user:password@api.example.com',
    'https://api.example.com/path',
    'https://api.example.com?query=1',
    'https://api.example.com#fragment',
    '//api.example.com',
    'http://api.example.com',
    'http://localhost:5179',
  ])('rejects invalid production origin %s', (origin) => {
    expect(() => recommendationEndpoint(origin, false)).toThrow('Invalid API origin configuration.')
  })

  it.each(['http://localhost:5179', 'http://127.0.0.1:5179', 'http://[::1]:5179'])('allows development loopback origin %s', (origin) => {
    expect(recommendationEndpoint(origin, true)).toBe(`${origin}/api/recommendations`)
  })

  it('rejects non-loopback HTTP origins in development', () => {
    expect(() => recommendationEndpoint('http://api.example.com', true)).toThrow('Invalid API origin configuration.')
  })
})
