// Empties the schemas the deploy test builds in, so every run starts from a database that has never been deployed to.
// Usage: node pgReset.cjs   with DBDATA_E2E_PG set to an Npgsql-style connection string (Host=...;Port=...;Username=...;Password=...;Database=...).
const { Client } = require('pg')

const parts = Object.fromEntries(process.env.DBDATA_E2E_PG.split(';').filter(Boolean).map((p) => [p.slice(0, p.indexOf('=')).trim().toLowerCase(), p.slice(p.indexOf('=') + 1)]))
const client = new Client({ host: parts.host, port: Number(parts.port ?? 5432), user: parts.username, password: parts.password, database: parts.database })
client.connect()
  .then(() => client.query('DROP SCHEMA IF EXISTS raw CASCADE; DROP SCHEMA IF EXISTS staging CASCADE; DROP SCHEMA IF EXISTS marts CASCADE; DROP SCHEMA IF EXISTS dbdatabuild CASCADE;'))
  .then(() => client.end())
  .catch((error) => { console.error(error.message); process.exit(1) })
