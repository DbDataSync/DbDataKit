// The `data` of two answers of the real `dbdatabuild project show graph --columns --format json` (tool_version 0.4.0, 2026-11-12) over the `starter` template of `dbdatabuild project create`:
// the whole project, and with `--column marts.customers.number_of_orders`. Recorded by running the tool, not written by hand; the reader of `buildLineage.ts` and the screen are tested over them.
export const RECORDED_LINEAGE: unknown = {
 "nodes": [
  {
   "name": "raw.customers",
   "category": "model",
   "kind": "mapped",
   "level": 0,
   "file": "models/raw/customers.yml",
   "connections": [
    "sqlserver"
   ]
  },
  {
   "name": "raw.orders",
   "category": "model",
   "kind": "mapped",
   "level": 0,
   "file": "models/raw/orders.yml",
   "connections": [
    "sqlserver"
   ]
  },
  {
   "name": "raw.payments",
   "category": "model",
   "kind": "mapped",
   "level": 0,
   "file": "models/raw/payments.yml",
   "connections": [
    "sqlserver"
   ]
  },
  {
   "name": "staging.customers",
   "category": "model",
   "kind": "view",
   "level": 1,
   "file": "models/staging/customers.sql",
   "connections": [
    "sqlserver"
   ]
  },
  {
   "name": "staging.orders",
   "category": "model",
   "kind": "view",
   "level": 1,
   "file": "models/staging/orders.sql",
   "connections": [
    "sqlserver"
   ]
  },
  {
   "name": "staging.payments",
   "category": "model",
   "kind": "view",
   "level": 1,
   "file": "models/staging/payments.sql",
   "connections": [
    "sqlserver"
   ]
  },
  {
   "name": "marts.orders",
   "category": "model",
   "kind": "full",
   "level": 2,
   "file": "models/marts/orders.sql",
   "connections": [
    "sqlserver"
   ]
  },
  {
   "name": "marts.customers",
   "category": "model",
   "kind": "full",
   "level": 3,
   "file": "models/marts/customers.sql",
   "connections": [
    "sqlserver"
   ]
  }
 ],
 "edges": [
  {
   "from": "marts.orders",
   "to": "marts.customers"
  },
  {
   "from": "staging.customers",
   "to": "marts.customers"
  },
  {
   "from": "staging.orders",
   "to": "marts.orders"
  },
  {
   "from": "staging.payments",
   "to": "marts.orders"
  },
  {
   "from": "raw.customers",
   "to": "staging.customers"
  },
  {
   "from": "raw.orders",
   "to": "staging.orders"
  },
  {
   "from": "raw.payments",
   "to": "staging.payments"
  }
 ],
 "unknown_tables": [],
 "column_edges": [
  {
   "from_table": "staging.customers",
   "from_column": "customer_id",
   "to_model": "marts.customers",
   "to_column": "customer_id",
   "transform": "direct"
  },
  {
   "from_table": "staging.customers",
   "from_column": "first_name",
   "to_model": "marts.customers",
   "to_column": "first_name",
   "transform": "direct"
  },
  {
   "from_table": "staging.customers",
   "from_column": "last_name",
   "to_model": "marts.customers",
   "to_column": "last_name",
   "transform": "direct"
  },
  {
   "from_table": "marts.orders",
   "from_column": "order_id",
   "to_model": "marts.customers",
   "to_column": "number_of_orders",
   "transform": "aggregation"
  },
  {
   "from_table": "marts.orders",
   "from_column": "order_date",
   "to_model": "marts.customers",
   "to_column": "first_order_date",
   "transform": "aggregation"
  },
  {
   "from_table": "marts.orders",
   "from_column": "order_date",
   "to_model": "marts.customers",
   "to_column": "most_recent_order_date",
   "transform": "aggregation"
  },
  {
   "from_table": "marts.orders",
   "from_column": "amount",
   "to_model": "marts.customers",
   "to_column": "lifetime_value",
   "transform": "cast"
  },
  {
   "from_table": "staging.orders",
   "from_column": "order_id",
   "to_model": "marts.orders",
   "to_column": "order_id",
   "transform": "direct"
  },
  {
   "from_table": "staging.orders",
   "from_column": "customer_id",
   "to_model": "marts.orders",
   "to_column": "customer_id",
   "transform": "direct"
  },
  {
   "from_table": "staging.orders",
   "from_column": "order_date",
   "to_model": "marts.orders",
   "to_column": "order_date",
   "transform": "direct"
  },
  {
   "from_table": "staging.orders",
   "from_column": "status",
   "to_model": "marts.orders",
   "to_column": "status",
   "transform": "direct"
  },
  {
   "from_table": "staging.orders",
   "from_column": "status_group",
   "to_model": "marts.orders",
   "to_column": "status_group",
   "transform": "direct"
  },
  {
   "from_table": "staging.payments",
   "from_column": "amount",
   "to_model": "marts.orders",
   "to_column": "credit_card_amount",
   "transform": "cast"
  },
  {
   "from_table": "staging.payments",
   "from_column": "payment_method",
   "to_model": "marts.orders",
   "to_column": "credit_card_amount",
   "transform": "cast"
  },
  {
   "from_table": "staging.payments",
   "from_column": "amount",
   "to_model": "marts.orders",
   "to_column": "bank_transfer_amount",
   "transform": "cast"
  },
  {
   "from_table": "staging.payments",
   "from_column": "payment_method",
   "to_model": "marts.orders",
   "to_column": "bank_transfer_amount",
   "transform": "cast"
  },
  {
   "from_table": "staging.payments",
   "from_column": "amount",
   "to_model": "marts.orders",
   "to_column": "coupon_amount",
   "transform": "cast"
  },
  {
   "from_table": "staging.payments",
   "from_column": "payment_method",
   "to_model": "marts.orders",
   "to_column": "coupon_amount",
   "transform": "cast"
  },
  {
   "from_table": "staging.payments",
   "from_column": "amount",
   "to_model": "marts.orders",
   "to_column": "gift_card_amount",
   "transform": "cast"
  },
  {
   "from_table": "staging.payments",
   "from_column": "payment_method",
   "to_model": "marts.orders",
   "to_column": "gift_card_amount",
   "transform": "cast"
  },
  {
   "from_table": "staging.payments",
   "from_column": "amount",
   "to_model": "marts.orders",
   "to_column": "amount",
   "transform": "cast"
  },
  {
   "from_table": "raw.customers",
   "from_column": "id",
   "to_model": "staging.customers",
   "to_column": "customer_id",
   "transform": "direct"
  },
  {
   "from_table": "raw.customers",
   "from_column": "first_name",
   "to_model": "staging.customers",
   "to_column": "first_name",
   "transform": "direct"
  },
  {
   "from_table": "raw.customers",
   "from_column": "last_name",
   "to_model": "staging.customers",
   "to_column": "last_name",
   "transform": "direct"
  },
  {
   "from_table": "raw.customers",
   "from_column": "email",
   "to_model": "staging.customers",
   "to_column": "email",
   "transform": "direct"
  },
  {
   "from_table": "raw.orders",
   "from_column": "id",
   "to_model": "staging.orders",
   "to_column": "order_id",
   "transform": "direct"
  },
  {
   "from_table": "raw.orders",
   "from_column": "user_id",
   "to_model": "staging.orders",
   "to_column": "customer_id",
   "transform": "direct"
  },
  {
   "from_table": "raw.orders",
   "from_column": "order_date",
   "to_model": "staging.orders",
   "to_column": "order_date",
   "transform": "direct"
  },
  {
   "from_table": "raw.orders",
   "from_column": "status",
   "to_model": "staging.orders",
   "to_column": "status",
   "transform": "direct"
  },
  {
   "from_table": "raw.orders",
   "from_column": "status",
   "to_model": "staging.orders",
   "to_column": "status_group",
   "transform": "expression"
  },
  {
   "from_table": "raw.payments",
   "from_column": "id",
   "to_model": "staging.payments",
   "to_column": "payment_id",
   "transform": "direct"
  },
  {
   "from_table": "raw.payments",
   "from_column": "order_id",
   "to_model": "staging.payments",
   "to_column": "order_id",
   "transform": "direct"
  },
  {
   "from_table": "raw.payments",
   "from_column": "payment_method",
   "to_model": "staging.payments",
   "to_column": "payment_method",
   "transform": "direct"
  },
  {
   "from_table": "raw.payments",
   "from_column": "amount_cents",
   "to_model": "staging.payments",
   "to_column": "amount",
   "transform": "expression"
  }
 ],
 "diagram": null
}

export const RECORDED_FOLLOW: unknown = {
 "nodes": [
  {
   "name": "raw.customers",
   "category": "model",
   "kind": "mapped",
   "level": 0,
   "file": "models/raw/customers.yml",
   "connections": [
    "sqlserver"
   ]
  },
  {
   "name": "raw.orders",
   "category": "model",
   "kind": "mapped",
   "level": 0,
   "file": "models/raw/orders.yml",
   "connections": [
    "sqlserver"
   ]
  },
  {
   "name": "raw.payments",
   "category": "model",
   "kind": "mapped",
   "level": 0,
   "file": "models/raw/payments.yml",
   "connections": [
    "sqlserver"
   ]
  },
  {
   "name": "staging.customers",
   "category": "model",
   "kind": "view",
   "level": 1,
   "file": "models/staging/customers.sql",
   "connections": [
    "sqlserver"
   ]
  },
  {
   "name": "staging.orders",
   "category": "model",
   "kind": "view",
   "level": 1,
   "file": "models/staging/orders.sql",
   "connections": [
    "sqlserver"
   ]
  },
  {
   "name": "staging.payments",
   "category": "model",
   "kind": "view",
   "level": 1,
   "file": "models/staging/payments.sql",
   "connections": [
    "sqlserver"
   ]
  },
  {
   "name": "marts.orders",
   "category": "model",
   "kind": "full",
   "level": 2,
   "file": "models/marts/orders.sql",
   "connections": [
    "sqlserver"
   ]
  },
  {
   "name": "marts.customers",
   "category": "model",
   "kind": "full",
   "level": 3,
   "file": "models/marts/customers.sql",
   "connections": [
    "sqlserver"
   ]
  }
 ],
 "edges": [
  {
   "from": "marts.orders",
   "to": "marts.customers"
  },
  {
   "from": "staging.customers",
   "to": "marts.customers"
  },
  {
   "from": "staging.orders",
   "to": "marts.orders"
  },
  {
   "from": "staging.payments",
   "to": "marts.orders"
  },
  {
   "from": "raw.customers",
   "to": "staging.customers"
  },
  {
   "from": "raw.orders",
   "to": "staging.orders"
  },
  {
   "from": "raw.payments",
   "to": "staging.payments"
  }
 ],
 "unknown_tables": [],
 "column_edges": [
  {
   "from_table": "staging.customers",
   "from_column": "customer_id",
   "to_model": "marts.customers",
   "to_column": "customer_id",
   "transform": "direct"
  },
  {
   "from_table": "staging.customers",
   "from_column": "first_name",
   "to_model": "marts.customers",
   "to_column": "first_name",
   "transform": "direct"
  },
  {
   "from_table": "staging.customers",
   "from_column": "last_name",
   "to_model": "marts.customers",
   "to_column": "last_name",
   "transform": "direct"
  },
  {
   "from_table": "marts.orders",
   "from_column": "order_id",
   "to_model": "marts.customers",
   "to_column": "number_of_orders",
   "transform": "aggregation"
  },
  {
   "from_table": "marts.orders",
   "from_column": "order_date",
   "to_model": "marts.customers",
   "to_column": "first_order_date",
   "transform": "aggregation"
  },
  {
   "from_table": "marts.orders",
   "from_column": "order_date",
   "to_model": "marts.customers",
   "to_column": "most_recent_order_date",
   "transform": "aggregation"
  },
  {
   "from_table": "marts.orders",
   "from_column": "amount",
   "to_model": "marts.customers",
   "to_column": "lifetime_value",
   "transform": "cast"
  },
  {
   "from_table": "staging.orders",
   "from_column": "order_id",
   "to_model": "marts.orders",
   "to_column": "order_id",
   "transform": "direct"
  },
  {
   "from_table": "staging.orders",
   "from_column": "customer_id",
   "to_model": "marts.orders",
   "to_column": "customer_id",
   "transform": "direct"
  },
  {
   "from_table": "staging.orders",
   "from_column": "order_date",
   "to_model": "marts.orders",
   "to_column": "order_date",
   "transform": "direct"
  },
  {
   "from_table": "staging.orders",
   "from_column": "status",
   "to_model": "marts.orders",
   "to_column": "status",
   "transform": "direct"
  },
  {
   "from_table": "staging.orders",
   "from_column": "status_group",
   "to_model": "marts.orders",
   "to_column": "status_group",
   "transform": "direct"
  },
  {
   "from_table": "staging.payments",
   "from_column": "amount",
   "to_model": "marts.orders",
   "to_column": "credit_card_amount",
   "transform": "cast"
  },
  {
   "from_table": "staging.payments",
   "from_column": "payment_method",
   "to_model": "marts.orders",
   "to_column": "credit_card_amount",
   "transform": "cast"
  },
  {
   "from_table": "staging.payments",
   "from_column": "amount",
   "to_model": "marts.orders",
   "to_column": "bank_transfer_amount",
   "transform": "cast"
  },
  {
   "from_table": "staging.payments",
   "from_column": "payment_method",
   "to_model": "marts.orders",
   "to_column": "bank_transfer_amount",
   "transform": "cast"
  },
  {
   "from_table": "staging.payments",
   "from_column": "amount",
   "to_model": "marts.orders",
   "to_column": "coupon_amount",
   "transform": "cast"
  },
  {
   "from_table": "staging.payments",
   "from_column": "payment_method",
   "to_model": "marts.orders",
   "to_column": "coupon_amount",
   "transform": "cast"
  },
  {
   "from_table": "staging.payments",
   "from_column": "amount",
   "to_model": "marts.orders",
   "to_column": "gift_card_amount",
   "transform": "cast"
  },
  {
   "from_table": "staging.payments",
   "from_column": "payment_method",
   "to_model": "marts.orders",
   "to_column": "gift_card_amount",
   "transform": "cast"
  },
  {
   "from_table": "staging.payments",
   "from_column": "amount",
   "to_model": "marts.orders",
   "to_column": "amount",
   "transform": "cast"
  },
  {
   "from_table": "raw.customers",
   "from_column": "id",
   "to_model": "staging.customers",
   "to_column": "customer_id",
   "transform": "direct"
  },
  {
   "from_table": "raw.customers",
   "from_column": "first_name",
   "to_model": "staging.customers",
   "to_column": "first_name",
   "transform": "direct"
  },
  {
   "from_table": "raw.customers",
   "from_column": "last_name",
   "to_model": "staging.customers",
   "to_column": "last_name",
   "transform": "direct"
  },
  {
   "from_table": "raw.customers",
   "from_column": "email",
   "to_model": "staging.customers",
   "to_column": "email",
   "transform": "direct"
  },
  {
   "from_table": "raw.orders",
   "from_column": "id",
   "to_model": "staging.orders",
   "to_column": "order_id",
   "transform": "direct"
  },
  {
   "from_table": "raw.orders",
   "from_column": "user_id",
   "to_model": "staging.orders",
   "to_column": "customer_id",
   "transform": "direct"
  },
  {
   "from_table": "raw.orders",
   "from_column": "order_date",
   "to_model": "staging.orders",
   "to_column": "order_date",
   "transform": "direct"
  },
  {
   "from_table": "raw.orders",
   "from_column": "status",
   "to_model": "staging.orders",
   "to_column": "status",
   "transform": "direct"
  },
  {
   "from_table": "raw.orders",
   "from_column": "status",
   "to_model": "staging.orders",
   "to_column": "status_group",
   "transform": "expression"
  },
  {
   "from_table": "raw.payments",
   "from_column": "id",
   "to_model": "staging.payments",
   "to_column": "payment_id",
   "transform": "direct"
  },
  {
   "from_table": "raw.payments",
   "from_column": "order_id",
   "to_model": "staging.payments",
   "to_column": "order_id",
   "transform": "direct"
  },
  {
   "from_table": "raw.payments",
   "from_column": "payment_method",
   "to_model": "staging.payments",
   "to_column": "payment_method",
   "transform": "direct"
  },
  {
   "from_table": "raw.payments",
   "from_column": "amount_cents",
   "to_model": "staging.payments",
   "to_column": "amount",
   "transform": "expression"
  }
 ],
 "column_lineage": {
  "column": "marts.customers.number_of_orders",
  "upstream": [
   {
    "table": "marts.orders",
    "column": "order_id",
    "distance": 1
   },
   {
    "table": "staging.orders",
    "column": "order_id",
    "distance": 2
   },
   {
    "table": "raw.orders",
    "column": "id",
    "distance": 3
   }
  ],
  "downstream": []
 },
 "diagram": null
}
