-- description: A mart is a dimension (dim_), a fact (fct_), an aggregate (agg_) or a report (rpt_)
-- tags: naming
SELECT model FROM metadata_models WHERE model LIKE 'marts.%' AND NOT regexp_matches(model, '^marts\.(dim|fct|agg|rpt)_')
