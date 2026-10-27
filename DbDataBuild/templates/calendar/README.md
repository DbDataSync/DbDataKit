# calendar: a fiscal calendar and the semantic model over it

Two models and a semantic model. `marts.dim_date` is a calendar built from a date series: one row for every day of three years, with the **fiscal year, quarter and month** each day is in
(as labels that carry the year: `FY2025`, `FY2025-Q1`, `FY2025-P01`) and the same three labels **one year earlier** (`prior_year_*`). `marts.fct_sales` is a small fact on top of
the seeded `shop.sales`. `semantic/calendar.yml` is the semantic model: the date dimension with its levels, a **calendar** (so a period in a question, such as "last complete fiscal
month" or "the same quarter last year", is resolved from the table), a **hierarchy** (year > quarter > month), the sales measures, and the relationship from a sale to its day.

```
dbdatabuild project compile                    # checks everything, the semantic model against the models included
dbdatabuild project show semantic              # what every attribute and measure is bound to
dbdatabuild project tests list --kind semantic # the data checks generated from the hierarchy and the relationship
dbdatabuild project sample marts.dim_date      # the calendar's first rows
```

## Change the fiscal year

`parameters: {fiscal_start_month: 7}` in `dbdatabuild.yml` is the month the fiscal year starts in (1 makes it the calendar year). The series starts on 2024-01-01 and is 1,096 days long:
both are written in `models/marts/dim_date.sql`. 4-4-5 and other retail calendars are not here: their levels are not the months, so each needs a different `fiscal` expression.

## The checks the semantic model brings

The hierarchy and the relationship are claims about the data, and a wrong claim makes a question answer something else without any error. Four checks are **generated** from them (nothing is written):

| Check | Fails when |
|---|---|
| `...hierarchy.date.by_fiscal_year.date.fiscal_quarter` | a quarter label is in more than one year |
| `...hierarchy.date.by_fiscal_year.date.fiscal_month` | a month label is in more than one quarter |
| `...relationship.sales_to_date.orphans` | a sale has a day the calendar does not have (the calendar is too short) |
| `...relationship.sales_to_date.grain` | a day is in the calendar twice |

`dbdatabuild project tests list --kind semantic` lists them; `dbdatabuild connection check` runs them against a database (it only reads; counts only unless `--show-values`):

```
dbdatabuild connection seed --apply                  # shop.sales, from seeds/shop/sales.sql
dbdatabuild connection init --apply
dbdatabuild connection deploy --write-plan --accept-inferred
dbdatabuild connection deploy --apply-plan <the plan file>
dbdatabuild connection check
```

Try breaking one: make the series shorter in `dim_date.sql` and deploy again; `connection check` then names the sales that fall outside the calendar.

## PostgreSQL

Replace the three lines of `dbdatabuild.yml` as its comment says. The semantic model says `default` for its connection, so it needs no change.
