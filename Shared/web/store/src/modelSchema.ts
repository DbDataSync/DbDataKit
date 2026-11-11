import type { SchemaNode } from '@dbdatakit/shell'

/** The name the form knows the model schema by. */
export const MODEL_SCHEMA = 'store-model.schema.json'

/**
 * The shape of a `model/1` file, for the form (`DbDataKit.DataModel`'s loader is the authority and its diagnostics are what the screen shows as problems: this schema only says which settings exist, so the form can offer them,
 * and which values a setting takes). It keeps no rule the loader does not have, and leaves the sections of `model/2` (`cache`, `checks`) to the YAML tab.
 */
export const modelSchema: SchemaNode = {
  $id: MODEL_SCHEMA,
  type: 'object',
  description: 'A DbDataStore model: entities, keys, attributes, references and rules.',
  required: ['schemaVersion', 'name', 'entities'],
  properties: {
    schemaVersion: { description: 'The format version of the file.', enum: [1, 2] },
    name: { description: 'The model name; the same as the file name.', type: 'string' },
    title: { description: 'A name for people.', type: 'string' },
    storage: {
      type: 'object',
      description: 'Where the tables live: a connection of the server (connections/<name>.yaml) and, optionally, how instances are kept apart.',
      properties: {
        connection: { description: 'The name of a connection file of the server.', type: 'string' },
        instances: { description: 'How copies (instances) of the model are kept apart.', enum: ['schema', 'connection', 'column'] },
      },
    },
    approval: {
      type: 'object',
      description: 'Which writes wait for a person to approve them.',
      properties: {
        changes: { description: 'Whose changes wait: none, a steward (an author approves) or the owner (an administrator approves).', enum: ['none', 'steward', 'owner'] },
        agents: { description: 'An agent\'s write waits as a request (the default) or goes through directly.', enum: ['request', 'direct'] },
      },
    },
    entities: {
      type: 'object',
      description: 'The entities (tables) of the model, by name.',
      additionalProperties: {
        type: 'object',
        required: ['key', 'attributes'],
        properties: {
          title: { type: 'string' },
          key: { description: 'The attributes that identify a row.', type: 'array', items: { type: 'string' } },
          history: { description: 'What is kept of earlier versions of a row.', enum: ['none', 'audit', 'effective_dated'] },
          role: { description: 'What the entity is for in the semantic layer.', enum: ['table', 'dimension', 'mapping', 'calendar'] },
          attributes: {
            type: 'object',
            description: 'The attributes (columns) of the entity, by name.',
            additionalProperties: {
              type: 'object',
              required: ['type'],
              properties: {
                type: { description: 'A SQL type or the canonical text of the dialect, such as VARCHAR(10) or DECIMAL(10,2).', type: 'string' },
                title: { type: 'string' },
                required: { description: 'A value must be given.', type: 'boolean' },
                unique: { description: 'No two rows hold the same value.', type: 'boolean' },
                default: { description: 'The value used when none is given.' },
                min: { type: 'number' },
                max: { type: 'number' },
                pattern: { description: 'A regular expression the whole value must match.', type: 'string' },
                allowed: { description: 'The only values the attribute may hold.', type: 'array', items: { type: 'string' } },
                references: {
                  type: 'object',
                  description: 'The attribute holds the key of a row of another entity.',
                  required: ['entity'],
                  properties: {
                    model: { description: 'Another model; leave out for an entity of this one.', type: 'string' },
                    entity: { type: 'string' },
                    attribute: { type: 'string' },
                  },
                },
              },
            },
          },
          rules: {
            type: 'array',
            description: 'Rules across rows.',
            items: {
              type: 'object',
              required: ['kind'],
              properties: {
                kind: { enum: ['sum_equals', 'no_overlap', 'no_cycle'] },
                attribute: { type: 'string' },
                group_by: { type: 'array', items: { type: 'string' } },
                value: { type: 'number' },
                tolerance: { type: 'number' },
              },
            },
          },
        },
      },
    },
  },
}
