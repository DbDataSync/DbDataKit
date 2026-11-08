import sys
import pyarrow as pa
import adbc_driver_flightsql.dbapi as fsql
from adbc_driver_flightsql import DatabaseOptions
from adbc_driver_manager import DatabaseOptions as MgrOptions
PORT, USER, PW, DS = sys.argv[1:5]
kw = {MgrOptions.USERNAME.value: USER, MgrOptions.PASSWORD.value: PW, DatabaseOptions.TLS_SKIP_VERIFY.value: "true",
      DatabaseOptions.RPC_CALL_HEADER_PREFIX.value + "x-datasource": DS}
with fsql.connect(f"grpc+tls://localhost:{PORT}", db_kwargs=kw) as conn:
    with conn.cursor() as cur:
        cur.execute("SELECT TOP 3 name, database_id, create_date, CAST(1.25 AS decimal(10,2)) AS d FROM sys.databases ORDER BY database_id")
        t = cur.fetch_arrow_table()
        print(DS, "query:", t.schema.types, t.num_rows, "rows", t.column("name").to_pylist())
    print(DS, "table types:", conn.adbc_get_table_types())
    tree = conn.adbc_get_objects(depth="tables", db_schema_filter="dbo", table_name_filter="spt_%").read_all().to_pylist()
    names = [(c["catalog_name"], s["db_schema_name"], t["table_name"], t["table_type"]) for c in tree for s in (c["catalog_db_schemas"] or []) for t in (s["db_schema_tables"] or [])]
    print(DS, "objects:", names[:3])
    print(DS, "table schema:", conn.adbc_get_table_schema("spt_values", db_schema_filter="dbo").names[:4])
