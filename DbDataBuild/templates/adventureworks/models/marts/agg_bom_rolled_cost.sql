-- What a finished product costs to build from its parts (the leaves of the explosion, each at its standard cost) against the standard cost it carries.
SELECT e.finished_product_id AS product_id,
       count(*) AS leaf_parts,
       CAST(sum(e.quantity_per_finished * p.standard_cost) AS DECIMAL(19, 4)) AS rolled_up_cost,
       f.standard_cost AS standard_cost,
       CAST(sum(e.quantity_per_finished * p.standard_cost) - f.standard_cost AS DECIMAL(19, 4)) AS variance
FROM marts.fct_bom_explosion e
JOIN staging.products p ON p.product_id = e.component_id
JOIN staging.products f ON f.product_id = e.finished_product_id
WHERE e.is_leaf
GROUP BY e.finished_product_id, f.standard_cost
