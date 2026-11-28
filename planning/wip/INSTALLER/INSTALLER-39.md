# INSTALLER-39 (work in progress)

Built: `delete_remote()` in `scripts/backup_branches.py` asks `git ls-remote --heads <remote> refs/heads/<name>` after a failed delete; the head-branch
step of `delete-merged-branch.yml` reads `git/ref/heads/<branch>` with `gh api --include` and takes a 404 as gone. Tests in
`scripts/tests/test_backup_branches.py`. Docs: `docs/branch-cleanup.md`, a row in `docs/ci-lessons.md`.

Not done yet: the task row leaves `planning/INSTALLER.md`; this file is deleted.

Open questions: none. Defaults: none.
