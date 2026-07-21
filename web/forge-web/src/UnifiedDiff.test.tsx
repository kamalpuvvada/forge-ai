// @vitest-environment jsdom

import { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, describe, expect, it } from 'vitest'
import { UnifiedDiff } from './UnifiedDiff'

(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true

describe('UnifiedDiff', () => {
  let host: HTMLDivElement | null = null

  afterEach(() => {
    host?.remove()
    host = null
  })

  it('classifies review lines without changing the exact diff text', async () => {
    const diff = [
      'diff --git a/src/App.cs b/src/App.cs',
      'index 1111111..2222222 100644',
      '--- a/src/App.cs',
      '+++ b/src/App.cs',
      '@@ -1,2 +1,2 @@',
      '-old value',
      '+new value',
      ' context value',
    ].join('\n')
    host = document.createElement('div')
    document.body.append(host)
    const root = createRoot(host)

    await act(async () => root.render(<UnifiedDiff diff={diff} filePath="src/App.cs" />))

    const pre = host.querySelector('pre')!
    expect(pre.textContent).toBe(diff)
    expect(pre.querySelector('.diff-line-added')?.textContent).toBe('+new value\n')
    expect(pre.querySelector('.diff-line-added')?.getAttribute('aria-label')).toBe('Added line: +new value')
    expect(pre.querySelector('.diff-line-deleted')?.textContent).toBe('-old value\n')
    expect(pre.querySelector('.diff-line-deleted')?.getAttribute('aria-label')).toBe('Deleted line: -old value')
    expect(pre.querySelector('.diff-line-hunk')?.textContent).toBe('@@ -1,2 +1,2 @@\n')
    expect(pre.querySelectorAll('.diff-line-metadata')).toHaveLength(4)
    expect([...pre.querySelectorAll('.diff-line-metadata')].map(line => line.textContent)).toEqual([
      'diff --git a/src/App.cs b/src/App.cs\n',
      'index 1111111..2222222 100644\n',
      '--- a/src/App.cs\n',
      '+++ b/src/App.cs\n',
    ])
    expect(pre.querySelector('.diff-line-context')?.textContent).toBe(' context value')
    await act(async () => root.unmount())
  })

  it('renders provider-controlled text safely instead of interpreting HTML', async () => {
    const diff = '+<img src=x onerror=alert(1)>'
    host = document.createElement('div')
    document.body.append(host)
    const root = createRoot(host)

    await act(async () => root.render(<UnifiedDiff diff={diff} filePath="src/App.cs" />))

    expect(host.querySelector('img')).toBeNull()
    expect(host.querySelector('pre')?.textContent).toBe(diff)
    expect(host.innerHTML).toContain('&lt;img')
    await act(async () => root.unmount())
  })
})
