// The `data` of the real `dbdatabuild project import crm.* --from-cache catalog-cache.yaml --write --format json` (tool_version 0.4.0, 2026-11-12) over the `starter` template and a catalog cache of a SQL Server connection with a table
// (one column of the type geography, which has no logical type), a view (money, widened) and a key; `files` is what the Workspace route adds: the text the tool wrote on its copy. Recorded by running the tool, not written by hand.
export const RECORDED_IMPORT: unknown = {
 "connection": "sqlserver",
 "mode": "write",
 "mapped_models": [
  {
   "name": "crm.account_totals",
   "object_kind": "view",
   "file": "models/crm/account_totals.yml",
   "status": "new",
   "grain": [],
   "indexes": [],
   "foreign_keys": [],
   "notes": [],
   "changes": [
    {
     "kind": "new",
     "detail": "2 column(s)"
    }
   ],
   "columns": [
    {
     "name": "id",
     "native_type": "int",
     "logical_type": "INTEGER",
     "fit": "exact",
     "nullable": false
    },
    {
     "name": "total",
     "native_type": "money",
     "logical_type": "DECIMAL(19, 4)",
     "fit": "widened",
     "reason": "money is a fixed-point type with 4 decimals",
     "nullable": true
    }
   ]
  },
  {
   "name": "crm.accounts",
   "object_kind": "table",
   "file": "models/crm/accounts.yml",
   "status": "new",
   "grain": [
    "id"
   ],
   "indexes": [],
   "foreign_keys": [],
   "notes": [],
   "changes": [
    {
     "kind": "new",
     "detail": "4 column(s)"
    }
   ],
   "columns": [
    {
     "name": "id",
     "native_type": "int",
     "logical_type": "INTEGER",
     "fit": "exact",
     "nullable": false
    },
    {
     "name": "name",
     "native_type": "nvarchar(80)",
     "logical_type": "VARCHAR(80)",
     "fit": "exact",
     "nullable": false
    },
    {
     "name": "balance",
     "native_type": "decimal(18, 2)",
     "logical_type": "DECIMAL(18, 2)",
     "fit": "exact",
     "nullable": true
    },
    {
     "name": "opened",
     "native_type": "datetime2(3)",
     "logical_type": "TIMESTAMP",
     "fit": "exact",
     "nullable": true
    },
    {
     "name": "area",
     "native_type": "geography",
     "fit": "none",
     "reason": "geography has no logical type",
     "nullable": true
    }
   ]
  }
 ],
 "skipped": [],
 "catalog_notes": [
  "the catalog cache holds columns, types, nullability and the primary key only: no index and no foreign key was read, so a new descriptor has none and a committed one keeps the ones it has as written; import from the live connection to bring them in"
 ],
 "files": [
  {
   "path": "models/crm/account_totals.yml",
   "content": "name: crm.account_totals\nkind: mapped\nconnections=: [sqlserver]\ncolumns:\n  - name: id\n    type: INTEGER\n    nullable: false\n  - name: total\n    type: DECIMAL(19, 4)\n",
   "exists": false,
   "truncated": false
  },
  {
   "path": "models/crm/accounts.yml",
   "content": "name: crm.accounts\nkind: mapped\nconnections=: [sqlserver]\ngrain: [id]\ncolumns:\n  - name: id\n    type: INTEGER\n    nullable: false\n  - name: name\n    type: VARCHAR(80)\n    nullable: false\n  - name: balance\n    type: DECIMAL(18, 2)\n  - name: opened\n    type: TIMESTAMP\n",
   "exists": false,
   "truncated": false
  }
 ]
}

export const RECORDED_IMPORT_DIAGNOSTICS = [
 {
  "severity": "warning",
  "code": "DDB-226",
  "message": "`crm.accounts.area` is geography: geography has no logical type. The column was left out of the descriptor.",
  "file": "models/crm/accounts.yml"
 }
]
