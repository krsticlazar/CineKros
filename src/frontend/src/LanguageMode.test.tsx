import { cleanup, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { App } from './App'

describe('language-aware v2.1 recommendation boundary', () => {
  afterEach(() => { cleanup(); vi.restoreAllMocks() })

  it('sends Serbian Cyrillic unchanged with sr and displays the localized mismatch', async () => {
    const submit = vi.fn().mockResolvedValue({ status: 422, body: { type: 'alert', alert: {
      code: 'LANGUAGE_MISMATCH', message: 'Upit nije na izabranom jeziku. Promenite jezik ili preformulišite upit.',
    } } })
    const user = userEvent.setup()
    render(<App submit={submit} />)
    await user.type(screen.getByRole('textbox'), 'Желим нешто као Fight Club са Brad Pittom')
    await user.click(screen.getByRole('button', { name: 'Pošalji' }))
    await waitFor(() => expect(submit).toHaveBeenCalledTimes(1))
    expect(submit.mock.calls[0][0]).toEqual({ language: 'sr', message: 'Желим нешто као Fight Club са Brad Pittom' })
    expect(await screen.findByText('Upit nije na izabranom jeziku. Promenite jezik ili preformulišite upit.')).toBeInTheDocument()
  })

  it('uses the selected English mode and exact English mismatch copy', async () => {
    const submit = vi.fn().mockResolvedValue({ status: 422, body: { type: 'alert', alert: {
      code: 'LANGUAGE_MISMATCH', message: 'The query is not in the selected language. Change the language or rephrase your query.',
    } } })
    const user = userEvent.setup()
    render(<App submit={submit} />)
    await user.click(screen.getByRole('button', { name: 'EN' }))
    await user.type(screen.getByRole('textbox'), 'Želim mračan film')
    await user.click(screen.getByRole('button', { name: 'Send' }))
    expect(await screen.findByText('The query is not in the selected language. Change the language or rephrase your query.')).toBeInTheDocument()
    expect(submit.mock.calls[0][0]).toEqual({ language: 'en', message: 'Želim mračan film' })
  })

  it.each([
    { status: 200, message: 'Upit nije na izabranom jeziku. Promenite jezik ili preformulišite upit.' },
    { status: 422, message: 'Untrusted provider details must never be rendered' },
  ])('rejects a malformed mismatch status or message', async ({ status, message }) => {
    const user = userEvent.setup()
    render(<App submit={vi.fn().mockResolvedValue({ status, body: { type: 'alert', alert: {
      code: 'LANGUAGE_MISMATCH', message,
    } } })} />)
    await user.type(screen.getByRole('textbox'), 'quiet movie')
    await user.click(screen.getByRole('button', { name: 'Pošalji' }))
    expect(await screen.findByText('Došlo je do greške. Probaj ponovo.')).toBeInTheDocument()
    expect(screen.queryByText(message)).not.toBeInTheDocument()
  })
})
