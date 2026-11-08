# Documents that need review and cleanup

Four documents were written before the products became one repository and have not been brought up to date. Review and cleanup is **low priority**.

| Document | What it is |
| --- | --- |
| `docs/security-review.md` | the consolidated security findings |
| `DbDataSync/architecture/review.md` | Sync's review (code and security) |
| `DbDataProxy/docs/review.md` | Proxy's review |
| `DbDataBuild/docs/review.md` | Build's review |

They are excluded from the audit and from the automated checks: `scripts/check_docs_commands.py` skips every file named `review.md` and `security-review.md` (a test says so, `test_the_four_pre_monorepo_reviews_are_not_read`), and `scripts/check_plans.py` reads only `docs/plans`. Their file and command names may be stale; do not copy from them without checking the code. When one is reviewed, bring it up to date or delete it, and remove its name from this file and from the skip list.
