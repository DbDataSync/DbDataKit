-- One row per store: the region of its country, the channel group its channel code stands for, and a size band for the shops.
SELECT s.store_id,
       s.store_name,
       s.country_code,
       coalesce(r.region, 'Unknown') AS region,
       s.channel_code,
       coalesce(c.channel_group, 'other') AS channel_group,
       s.floor_area_m2,
       CASE WHEN s.floor_area_m2 IS NULL THEN 'none' WHEN s.floor_area_m2 < 1800 THEN 'small' WHEN s.floor_area_m2 < 3000 THEN 'medium' ELSE 'large' END AS size_band,
       s.opened_date
FROM staging.stores s
LEFT JOIN staging.country_regions r ON r.country_code = s.country_code
LEFT JOIN staging.channels c ON c.channel_code = s.channel_code
