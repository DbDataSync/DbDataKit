import { describe, expect, it } from 'vitest'
import { hitPlace, modesOffered, searchModeLine, vectorPercent, vectorProgressLine, type KnowledgeIndexState, type VectorState } from './knowledge'

// Plan 27a KN28 with KN07 and KN08: what the Knowledge page says about the index, its search modes and its vectors. The server decides everything; these are the words and the choices the screen derives from its answer.

const bound = { provider: 'ollama-local', model: 'nomic-embed-text', modelId: 'ollama-local/nomic-embed-text', usable: true, problem: null }
const vectors: VectorState = { readyModel: 'ollama-local/nomic-embed-text', readyDimension: 768, buildingModel: null, buildingDimension: null, chunks: 40, embedded: 40, buildingEmbedded: 0, failed: 0 }
const state = (over: Partial<KnowledgeIndexState> = {}): KnowledgeIndexState => ({
  channel: 'main', opened: true, running: false, items: 12, chunks: 40, wordsMode: 'tsvector', vectorsAvailable: true, vectorsProblem: null, embedding: bound, vectors, failures: [], lastRun: null, message: '12 items in 40 chunks.', ...over,
})

describe('the ways of searching the index offers', () => {
  it('offers nothing before the index is built', () => {
    expect(modesOffered(state({ opened: false }))).toEqual([])
  })
  it('offers words, vectors and both when a model is bound and its vectors are ready', () => {
    expect(modesOffered(state())).toEqual(['words', 'vectors', 'both'])
  })
  it('offers words only where the engine cannot hold vectors or no index is ready yet', () => {
    expect(modesOffered(state({ vectorsAvailable: false, vectors: null }))).toEqual(['words'])
    expect(modesOffered(state({ vectors: { ...vectors, readyModel: null, readyDimension: null } }))).toEqual(['words'])
  })
})

describe('the search mode in a line', () => {
  it('says words, vectors or both together when everything is ready', () => {
    expect(searchModeLine(state())).toBe('Words, vectors, or both together.')
  })
  it('says why vectors are missing: the engine, no model bound, a model that cannot be used, no vectors yet', () => {
    expect(searchModeLine(state({ vectorsAvailable: false, vectorsProblem: "The module 'knowledge_vectors' needs the engine feature 'vector'." }))).toContain("'knowledge_vectors'")
    expect(searchModeLine(state({ embedding: { ...bound, provider: null, model: null, modelId: null } }))).toBe('Words only for now: no embedding model is bound.')
    expect(searchModeLine(state({ embedding: { ...bound, usable: false, problem: 'The key is not set.' } }))).toBe('Words only for now: The key is not set.')
    expect(searchModeLine(state({ vectors: { ...vectors, readyModel: null, readyDimension: null } }))).toBe('Words only until the vectors are built.')
    expect(searchModeLine(state({ opened: false }))).toBe('Not built.')
  })
})

describe('the progress of the vectors', () => {
  it('counts the percentage, whole for an empty index and never over a hundred', () => {
    expect(vectorPercent(0, 0)).toBe(100)
    expect(vectorPercent(10, 40)).toBe(25)
    expect(vectorPercent(41, 40)).toBe(100)
  })
  it('says the ready model and the one being built beside it, which keeps answering, and the failures', () => {
    expect(vectorProgressLine(vectors)).toBe('ollama-local/nomic-embed-text (768 dimensions): 40 of 40 chunks.')
    const changing = vectorProgressLine({ ...vectors, buildingModel: 'ollama-local/bge-m3', buildingDimension: 1024, buildingEmbedded: 10, failed: 2 })
    expect(changing).toContain('building ollama-local/bge-m3 (1024 dimensions) beside it: 10 of 40 chunks')
    expect(changing).toContain('keeps answering until the new one is complete')
    expect(changing).toContain('2 chunks failed')
    expect(vectorProgressLine(null)).toBe('No vectors.')
  })
})

describe('a hit', () => {
  it('shows its place as the heading path, or the title when there is none', () => {
    const hit = { itemId: 'project:a.md', title: 'Revenue', headingPath: ['Revenue', 'Recognition'], excerpt: '', source: 'project', version: 'abc', score: 1, chunks: 1 }
    expect(hitPlace(hit)).toBe('Revenue > Recognition')
    expect(hitPlace({ ...hit, headingPath: [] })).toBe('Revenue')
  })
})
