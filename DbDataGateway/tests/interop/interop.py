import sys, traceback
import pyarrow as pa
import pyarrow.flight as flight
import adbc_driver_flightsql.dbapi as fsql
from adbc_driver_flightsql import DatabaseOptions, ConnectionOptions
from adbc_driver_manager import DatabaseOptions as MgrOptions

PORT, USER, PW = sys.argv[1], sys.argv[2], sys.argv[3]
URI = f"grpc+tls://localhost:{PORT}"
results = []

def check(name, fn):
    try:
        detail = fn()
        results.append((name, True, detail))
    except Exception as ex:
        results.append((name, False, f"{type(ex).__name__}: {ex}"))
        traceback.print_exc()

def adbc_connect(datasource="duck", compression=None, user=USER, password=PW):
    kwargs = {
        MgrOptions.USERNAME.value: user,
        MgrOptions.PASSWORD.value: password,
        DatabaseOptions.TLS_SKIP_VERIFY.value: "true",
        DatabaseOptions.RPC_CALL_HEADER_PREFIX.value + "x-datasource": datasource,
    }
    if compression:
        kwargs[DatabaseOptions.RPC_CALL_HEADER_PREFIX.value + "x-compression"] = compression
    return fsql.connect(URI, db_kwargs=kwargs)

# --- ADBC (the standard Flight SQL client) ---
for codec in ["none", "gzip", "zstd", "lz4"]:
    def q(codec=codec):
        with adbc_connect(compression=codec) as conn, conn.cursor() as cur:
            cur.execute("SELECT id, name, score, day FROM numbers ORDER BY id")
            t = cur.fetch_arrow_table()
            assert t.num_rows == 100000, t.num_rows
            assert t.column("id")[99999].as_py() == 99999
            assert t.column("name")[5].as_py() == "name-5"
            assert t.schema.field("day").type == pa.date32(), t.schema
            return f"{t.num_rows} rows, schema={t.schema.names}"
    check(f"ADBC query, compression={codec}", q)

def default_compression():
    with adbc_connect() as conn, conn.cursor() as cur:  # server default is Arrow data compression (zstd)
        cur.execute("SELECT count(*) AS n FROM numbers")
        return str(cur.fetch_arrow_table().column("n")[0].as_py())
check("ADBC query, server default compression", default_compression)

def update():
    with adbc_connect() as conn, conn.cursor() as cur:
        cur.execute("CREATE TABLE IF NOT EXISTS py_scratch(i INTEGER)")
        cur.execute("INSERT INTO py_scratch SELECT * FROM range(0, 7)")
        n = cur.rowcount
        cur.execute("SELECT count(*) AS n FROM py_scratch")
        total = cur.fetch_arrow_table().column("n")[0].as_py()
        conn.commit() if False else None
        return f"rowcount={n} total={total}"
check("ADBC update + readback", update)

def parameters():
    with adbc_connect() as conn, conn.cursor() as cur:
        cur.execute("SELECT id, name FROM numbers WHERE id >= ? AND id < ? ORDER BY id", (10, 13))
        t = cur.fetch_arrow_table()
        assert t.column("id").to_pylist() == [10, 11, 12], t
        cur.execute("SELECT count(*) AS n FROM numbers WHERE name = ?", ("x'; DROP TABLE numbers; --",))
        assert cur.fetch_arrow_table().column("n")[0].as_py() == 0
        cur.execute("CREATE TABLE IF NOT EXISTS py_params(i INTEGER, s VARCHAR)")
        cur.executemany("INSERT INTO py_params VALUES (?, ?)", [(1, "a"), (2, "b"), (3, None)])
        cur.execute("SELECT count(*) AS n FROM py_params")
        n = cur.fetch_arrow_table().column("n")[0].as_py()
        assert n >= 3, n
        return f"select ok, injection harmless, executemany total={n}"
check("ADBC parameters (query, executemany)", parameters)

def objects():
    with adbc_connect() as conn:
        types = conn.adbc_get_table_types()
        info = conn.adbc_get_info()
        tree = conn.adbc_get_objects(depth="tables", table_name_filter="numbers").read_all().to_pylist()
        names = [t["table_name"] for c in tree for s in (c["catalog_db_schemas"] or []) for t in (s["db_schema_tables"] or [])]
        assert "numbers" in names, tree
        return f"types={types} info_keys={len(info)} tables={names}"
check("ADBC metadata: table types, info, objects", objects)

def table_schema():
    with adbc_connect() as conn:
        schema = conn.adbc_get_table_schema("numbers")
        assert schema.names == ["id", "name", "score", "day"], schema
        return str(schema.names)
check("ADBC get_table_schema", table_schema)

def bad_password():
    try:
        with adbc_connect(password="wrong") as conn, conn.cursor() as cur:
            cur.execute("SELECT 1")
            cur.fetch_arrow_table()
    except Exception as ex:
        return f"rejected as expected ({type(ex).__name__})"
    raise AssertionError("wrong password was accepted")
check("ADBC wrong password rejected", bad_password)

# --- Raw pyarrow Flight: handshake token + hand-encoded Flight SQL command ---
def varint(n):
    out = bytearray()
    while True:
        b = n & 0x7F
        n >>= 7
        out.append(b | (0x80 if n else 0))
        if not n:
            return bytes(out)

def pack_statement_query(sql: str) -> bytes:
    inner = b"\x0a" + varint(len(sql.encode())) + sql.encode()           # CommandStatementQuery.query = 1
    url = b"type.googleapis.com/arrow.flight.protocol.sql.CommandStatementQuery"
    return b"\x0a" + varint(len(url)) + url + b"\x12" + varint(len(inner)) + inner  # Any{type_url, value}

for codec in ["none", "zstd", "lz4"]:
    def raw(codec=codec):
        client = flight.FlightClient(URI, disable_server_verification=True)
        token = client.authenticate_basic_token(USER, PW)
        options = flight.FlightCallOptions(headers=[token, (b"x-datasource", b"duck"), (b"x-compression", codec.encode())])
        info = client.get_flight_info(flight.FlightDescriptor.for_command(pack_statement_query("SELECT id, name FROM numbers")), options)
        table = client.do_get(info.endpoints[0].ticket, options).read_all()
        assert table.num_rows == 100000
        return f"{table.num_rows} rows"
    check(f"pyarrow Flight raw, compression={codec}", raw)

print()
width = max(len(n) for n, _, _ in results)
for name, ok, detail in results:
    print(("PASS " if ok else "FAIL ") + name.ljust(width) + "  " + str(detail)[:150])
sys.exit(0 if all(ok for _, ok, _ in results) else 1)
