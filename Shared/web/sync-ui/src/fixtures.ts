/** A sync draft for the tests: two connections, a replication with one mapping, and a catalog cache of both connections. */

export const CATALOG = `schema: catalog-cache/2
generated_at: '2026-11-20T10:00:00Z'
connections:
  dw:
    engine: postgres
    read: {server: runners-eu, product: runner, channel: dev, at: '2026-11-20T10:00:00Z'}
    databases:
      - {name: warehouse, schemas: [dbo]}
    tables:
      - database: warehouse
        schema: dbo
        name: orders
        kind: table
        columns:
          - {name: id, type: bigint, nullable: false, key: true}
          - {name: customer_id, type: bigint, nullable: true}
          - {name: total, type: numeric, nullable: true}
  erp:
    engine: sqlserver
    read: {server: runners-eu, product: runner, channel: dev, at: '2026-11-20T10:00:00Z'}
    databases:
      - {name: sales, schemas: [dbo]}
    tables:
      - database: sales
        schema: dbo
        name: customers
        kind: table
        columns:
          - {name: id, type: int, nullable: false, key: true}
          - {name: name, type: nvarchar(100), nullable: true}
      - database: sales
        schema: dbo
        name: orders
        kind: table
        columns:
          - {name: id, type: int, nullable: false, key: true}
          - {name: customer_id, type: int, nullable: true}
          - {name: total, type: 'decimal(18,2)', nullable: true}
          - {name: placed_at, type: datetime2, nullable: true}
`

export const DRAFT: Record<string, string> = {
  'config/connections/erp.yaml': 'schema: connection/1\nname: erp\nengine: sqlserver\naddress:\n  host: erp.example.com\n  database: sales\nauth:\n  mode: sql\n  user: sync\n  password_secret: secret:dbdatakit:sync:connection:erp\n',
  'config/connections/dw.yaml': 'schema: connection/1\nname: dw\nengine: postgres\naddress:\n  host: dw.example.com\n  database: warehouse\nauth:\n  mode: sql\n  user: sync\n  password_secret: secret:dbdatakit:sync:connection:dw\n',
  'config/replications/orders/task.yaml': `# the nightly orders copy
schemaVersion: 2
name: orders
scheduling:
  mode: Continuous
  frequencySeconds: 60
changeProcessing:
  reader: {kind: MsSqlChangeTracking, options: {}}
  cache: {kind: MsSqlStagingTable, options: {}}
  writer: {kind: PostgresMerge, options: {}}
endpoints:
  source: {connectionName: erp, database: sales}
  target: {connectionName: dw, database: warehouse}
owner: data-team
`,
  'config/replications/orders/table-mappings/dbo.orders.yaml': 'schemaVersion: 1\nname: dbo.orders\nsources:\n- schema: dbo\n  table: orders\ntargets:\n- schema: dbo\n  table: orders\ncolumnMappings:\n- sourceColumn: id\n  targetColumn: id\nprimaryKey: []\n',
  'catalog-cache.yaml': CATALOG,
}
