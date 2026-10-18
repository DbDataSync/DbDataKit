-- The parts of every finished product at every level: start from the assemblies nothing else uses, then add the components of each component, multiplying the quantity per
-- assembly down the levels, and keep the path that led to each part.
WITH RECURSIVE parts AS (
  SELECT b.product_assembly_id AS finished_product_id,
         b.component_id,
         b.per_assembly_qty AS quantity_per_finished,
         1 AS bom_level,
         CAST(b.product_assembly_id AS VARCHAR) || '>' || CAST(b.component_id AS VARCHAR) AS path
  FROM staging.bom b
  WHERE NOT EXISTS (SELECT 1 FROM staging.bom parent WHERE parent.component_id = b.product_assembly_id)
  UNION ALL
  SELECT p.finished_product_id,
         b.component_id,
         p.quantity_per_finished * b.per_assembly_qty,
         p.bom_level + 1,
         p.path || '>' || CAST(b.component_id AS VARCHAR)
  FROM parts p
  JOIN staging.bom b ON b.product_assembly_id = p.component_id
)
SELECT finished_product_id, component_id, bom_level, path, quantity_per_finished,
       NOT EXISTS (SELECT 1 FROM staging.bom child WHERE child.product_assembly_id = parts.component_id) AS is_leaf
FROM parts
