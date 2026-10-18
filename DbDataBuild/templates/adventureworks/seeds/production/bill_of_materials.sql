-- Three or four levels: a bike (1 to 30) is made of 3 to 6 components (31 to 90), the first twenty of which are assemblies themselves made of 2 or 3 parts (91 to 150).
WITH top_level AS (
  SELECT DISTINCT a AS product_assembly_id, 31 + pick(a * 10 + j, 72, 60) AS component_id, 1 + pick(a * 10 + j, 73, 4) AS qty
  FROM generate_series(1, 30) AS g(a) CROSS JOIN generate_series(1, 6) AS h(j)
  WHERE j <= 3 + pick(a, 74, 4)
),
sub_level AS (
  SELECT DISTINCT a AS product_assembly_id, 91 + pick(a * 10 + j, 75, 60) AS component_id, CASE WHEN pick(a * 10 + j, 76, 4) = 0 THEN 0.5 ELSE 1 + pick(a * 10 + j, 77, 3) END AS qty
  FROM generate_series(31, 50) AS g(a) CROSS JOIN generate_series(1, 3) AS h(j)
  WHERE j <= 2 + pick(a, 78, 2)
),
both_levels AS (SELECT * FROM top_level UNION ALL SELECT * FROM sub_level)
SELECT row_number() OVER (ORDER BY product_assembly_id, component_id) AS bill_of_materials_id,
       product_assembly_id,
       component_id,
       CAST(qty AS DECIMAL(8, 2)) AS per_assembly_qty,
       CASE WHEN component_id >= 91 AND pick(component_id, 79, 5) = 0 THEN 'IN' ELSE 'EA' END AS unit_measure_code
FROM both_levels
