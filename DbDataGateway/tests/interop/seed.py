import sys, duckdb
c = duckdb.connect(sys.argv[1])
c.execute("""CREATE TABLE numbers AS SELECT i::INTEGER AS id, 'name-' || i::VARCHAR AS name, (i*1.5)::DOUBLE AS score,
             DATE '2024-01-01' + CAST(i % 365 AS INTEGER) AS day FROM range(0,100000) t(i)""")
c.close()
