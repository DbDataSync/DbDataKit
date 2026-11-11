import { describe, expect, it } from 'vitest'
import { isFinished, isRunning, offersUpgrade, outcomeSentence, progressLines, upgradeBase, type UpgradeStatus } from './upgradeRun'

const status = (over: Partial<UpgradeStatus>): UpgradeStatus => ({ product: 'dbdatagateway', version: '1.0.0', generation: 1, install_method: 'Archive', can_apply: true, reason: null, state: 'none', problem: null, draining: false, active_work: 0, ...over })

describe('upgradeRun helpers', () => {
  it('reaches a server through the relay', () => {
    expect(upgradeBase('a b')).toBe('/relay/a%20b/api/v1/upgrade')
  })
  it('offers the upgrade for the products that mount it', () => {
    expect(offersUpgrade('dbdatagateway')).toBe(true)
    expect(offersUpgrade('dbdatasync')).toBe(false)
    expect(offersUpgrade(null)).toBe(false)
  })
  it('knows when to keep asking and when it is over', () => {
    expect(isRunning('draining')).toBe(true)
    expect(isRunning('applying')).toBe(true)
    expect(isRunning('prepared')).toBe(false)
    expect(isFinished('rolled_back')).toBe(true)
    expect(isFinished('applying')).toBe(false)
  })
  it('shows the drain waiting, then the helper steps', () => {
    const draining = progressLines(status({ state: 'draining', active_work: 3 }))
    expect(draining).toEqual([{ key: 'drain', text: 'Stop taking new work and wait for 3 piece(s) of work running', state: 'running' }])
    const applying = progressLines(status({ state: 'applying', plan: { id: 'p', from: '1.0.0', to: '2.0.0', state: 'InProgress', reason: null, steps: [{ name: 'Stop', state: 'Done', error: null, at: null }, { name: 'HealthCheck', state: 'Running', error: null, at: null }, { name: 'Commit', state: 'Pending', error: null, at: null }] } }))
    expect(applying.map((l) => l.state)).toEqual(['done', 'done', 'running', 'pending'])
    expect(applying[2].text).toBe('Wait for the new version to answer')
  })
  it('says how it ended in the server words', () => {
    expect(outcomeSentence(status({ state: 'rolled_back', message: 'did not become healthy' }))).toBe('did not become healthy')
    expect(outcomeSentence(status({ state: 'applying' }))).toBeNull()
  })
})
