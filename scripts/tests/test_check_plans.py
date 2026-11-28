import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
import check_plans as check  # noqa: E402


def tree(files):
    root = tempfile.mkdtemp()
    for rel, text in files.items():
        path = os.path.join(root, rel)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8") as f:
            f.write(text)
    return root


def index(*rows):
    return ("# Planning\n\n| # | Subject | Goal | Status | Assigned |\n| --- | --- | --- | --- | --- |\n"
            + "".join(f"| {i + 1} | [{slug}]({slug}.md) | goal | queued | unassigned |\n" for i, (slug, n) in enumerate(rows)))


def subject(slug, tasks=(), proposals=(), needs=""):
    rows = "".join(f"| {slug}-{n} | what | {dep} | S | a test |\n" for n, dep in tasks)
    props = "".join(f"### {slug}-P{n}: a question\n\nScenario.\n\n" for n in proposals)
    return (f"# {slug}\n\n## Goal\n\nx\n\n## Tasks\n\n| Task | What | Depends on | Size | Done when |\n| --- | --- | --- | --- | --- |\n{rows}\n"
            f"## Needs from other subjects\n\n{needs}\n\n## Proposed decisions\n\n{props}")


GOOD = {
    "planning/README.md": index(("ALPHABETA", 2), ("GAMMADELTA", 1)),
    "planning/ROLES.md": "# Roles\n",
    "planning/ALPHABETA.md": subject("ALPHABETA", [(1, "none"), (2, "ALPHABETA-1")], [1]),
    "planning/GAMMADELTA.md": subject("GAMMADELTA", [(1, "ALPHABETA-2")], needs="- ALPHABETA-1: the thing; see ALPHABETA-P1."),
    "docs/a.md": "See [the plan](../planning/ALPHABETA.md).\n",
}


class CheckPlans(unittest.TestCase):
    def problems(self, changes=None, drop=()):
        files = {k: v for k, v in GOOD.items() if k not in drop}
        files.update(changes or {})
        return check.problems(tree(files))

    def test_a_consistent_planning_folder_passes(self):
        self.assertEqual([], self.problems())

    def test_tasks_that_depend_on_each_other_in_a_cycle_are_refused(self):
        found = self.problems({"planning/ALPHABETA.md": subject("ALPHABETA", [(1, "GAMMADELTA-1"), (2, "ALPHABETA-1")], [1])})
        self.assertEqual(["planning/ALPHABETA.md: the tasks ALPHABETA-1 -> GAMMADELTA-1 -> ALPHABETA-2 -> ALPHABETA-1 depend on each other in a cycle"], found)

    def test_a_follows_in_the_text_must_be_under_depends_on(self):
        row = "| ALPHABETA-2 | Follows ALPHABETA-1 (it exists). What | none | S | a test |\n"
        text = subject("ALPHABETA", [(1, "none")], [1]).replace("| ALPHABETA-1 | what | none | S | a test |\n", "| ALPHABETA-1 | what | none | S | a test |\n" + row)
        self.assertEqual(["planning/ALPHABETA.md: ALPHABETA-2 follows ALPHABETA-1 in its text but does not list it under Depends on"],
                         self.problems({"planning/ALPHABETA.md": text}))
        self.assertEqual([], self.problems({"planning/ALPHABETA.md": text.replace("(it exists). What | none |", "(it exists). What | ALPHABETA-1 |")}))

    def test_a_dependency_on_a_lower_ranked_subject_is_reported_not_refused(self):
        root = tree({**GOOD, "planning/ALPHABETA.md": subject("ALPHABETA", [(1, "GAMMADELTA-1"), (2, "ALPHABETA-1")], [1]),
                     "planning/GAMMADELTA.md": subject("GAMMADELTA", [(1, "none")], needs="- ALPHABETA-1: the thing; see ALPHABETA-P1.")})
        self.assertEqual([], check.problems(root))
        self.assertEqual(["ALPHABETA-1 (rank 1) depends on GAMMADELTA-1 (rank 2)"], check.priority_report(root))
        self.assertEqual([], check.priority_report(tree(GOOD)))

    def test_a_subject_missing_from_the_index_is_named(self):
        found = self.problems({"planning/README.md": index(("ALPHABETA", 2))})
        self.assertTrue(any("does not list the subject planning/GAMMADELTA.md" in p for p in found), found)

    def test_an_index_line_without_its_file_is_named(self):
        found = self.problems({"planning/README.md": index(("ALPHABETA", 2), ("GAMMADELTA", 1), ("EPSILONZETA", 0))})
        self.assertTrue(any("links EPSILONZETA.md, which does not exist" in p for p in found), found)

    def with_subject(self, slug, tasks=(), proposals=(), needs=""):
        files = {"planning/README.md": index(("ALPHABETA", 2), ("GAMMADELTA", 1), (slug, 0)),
                 f"planning/{slug}.md": subject(slug, tasks, proposals, needs)}
        return self.problems(files)

    def test_a_lower_case_or_digit_first_slug_is_refused(self):
        for slug in ("Short", "9LIVES"):
            found = self.with_subject(slug)
            self.assertTrue(any(f"the slug '{slug}' must be capitals and digits, starting with a letter" in p for p in found), found)

    def test_a_short_slug_passes_with_its_tasks_and_proposals(self):
        found = self.with_subject("SEO4", [(1, "none"), (2, "SEO4-1")], [1], needs="- ALPHABETA-1: the thing; see SEO4-P1.")
        self.assertEqual([], found)

    def test_a_twelve_character_slug_passes(self):
        self.assertEqual([], self.with_subject("ABCDEFGHIJ12", [(1, "none")]))

    def test_a_new_slug_longer_than_twelve_characters_is_refused(self):
        found = self.with_subject("ABCDEFGHIJ123")
        self.assertIn("planning/ABCDEFGHIJ123.md: the slug 'ABCDEFGHIJ123' has 13 characters; a slug has at most 12", found)

    def test_a_long_slug_named_before_the_limit_passes(self):
        self.assertEqual([], self.with_subject("WORKSPACEISOLATION", [(1, "none")]))

    def test_a_task_of_a_short_slug_that_does_not_exist_is_named(self):
        found = self.with_subject("SEO4", [(1, "SEO4-7")])
        self.assertIn("planning/SEO4.md: names SEO4-7, which is not a task of any subject", found)

    def test_short_words_with_a_number_that_are_not_slugs_are_not_ids(self):
        found = self.with_subject("SEO4", [(1, "none")], needs="- hashes are SHA-256 in UTF-8, tested on N-1.")
        self.assertEqual([], found)

    def test_a_task_with_another_subjects_prefix_is_refused(self):
        found = self.problems({"planning/GAMMADELTA.md": subject("GAMMADELTA", [(1, "none")]) + "\n| ALPHABETA-9 | x | none | S | t |\n"})
        self.assertTrue(any("task ALPHABETA-9 does not start with the slug GAMMADELTA" in p for p in found), found)

    def test_a_task_id_used_twice_is_refused(self):
        found = self.problems({"planning/ALPHABETA.md": subject("ALPHABETA", [(1, "none"), (1, "none")], [1])})
        self.assertTrue(any("task ALPHABETA-1 is also in" in p for p in found), found)

    def test_a_need_naming_a_task_that_does_not_exist_is_named(self):
        found = self.problems({"planning/GAMMADELTA.md": subject("GAMMADELTA", [(1, "none")], needs="- ALPHABETA-7: missing")})
        self.assertTrue(any("names ALPHABETA-7, which is not a task" in p for p in found), found)

    def test_a_proposed_decision_with_another_prefix_or_twice_is_refused(self):
        text = subject("GAMMADELTA", [(1, "none")]) + "\n### ALPHABETA-P4: x\n\n### GAMMADELTA-P1: y\n\n### GAMMADELTA-P1: z\n"
        found = self.problems({"planning/GAMMADELTA.md": text})
        self.assertTrue(any("proposed decision ALPHABETA-P4 does not start with the slug GAMMADELTA" in p for p in found), found)
        self.assertTrue(any("proposed decision GAMMADELTA-P1 is also in" in p for p in found), found)

    def test_a_reference_to_a_proposal_that_does_not_exist_is_named(self):
        found = self.problems({"planning/GAMMADELTA.md": subject("GAMMADELTA", [(1, "ALPHABETA-P8")])})
        self.assertTrue(any("names ALPHABETA-P8, which is not a proposed decision" in p for p in found), found)

    def test_a_document_that_names_docs_plans_is_refused(self):
        found = self.problems({"DbDataKit/README.md": "See `docs/plans/35-isolation.md`.\n", "docs/b.md": "[old](plans/26-app.md)\n"})
        self.assertTrue(any(p.startswith("DbDataKit/README.md: names docs/plans/") for p in found), found)
        self.assertTrue(any(p.startswith("docs/b.md: names docs/plans/") for p in found), found)

    def test_a_link_to_a_deleted_subject_is_refused(self):
        found = self.problems({"docs/c.md": "Owned by [OLDSUBJECT](../planning/OLDSUBJECT.md).\n"})
        self.assertTrue(any("docs/c.md: links ../planning/OLDSUBJECT.md, which does not exist" in p for p in found), found)

    def test_wip_notes_are_not_read_here(self):
        self.assertEqual([], self.problems({"planning/wip/ALPHABETA/ALPHABETA-1.md": "# notes\n- names NOSUCHTASK-3\n"}))

    def test_the_parked_file_needs_no_index_row_and_may_be_linked(self):
        parked = "# Parked\n\n## Offline installer\n\nThe whole design; it was ALPHABETA-2 before it was parked.\n"
        found = self.problems({
            "planning/PARKED.md": parked,
            "planning/README.md": index(("ALPHABETA", 2), ("GAMMADELTA", 1)) + "\nParked work: [PARKED](PARKED.md).\n",
            "planning/ALPHABETA.md": subject("ALPHABETA", [(1, "none"), (2, "ALPHABETA-1")], [1]) + "See [parked](PARKED.md#offline-installer).\n",
        })
        self.assertEqual([], found)

    def test_the_parked_file_holds_no_task_rows_or_proposals(self):
        found = self.problems({"planning/PARKED.md": "# Parked\n\n| PARKED-1 | what | none | S | a test |\n\n### PARKED-P1: a question\n"})
        self.assertTrue(any(p.startswith("planning/PARKED.md: has the task row PARKED-1") for p in found), found)
        self.assertTrue(any(p.startswith("planning/PARKED.md: has the proposed decision PARKED-P1") for p in found), found)

    def test_the_parked_file_naming_a_task_that_does_not_exist_is_named(self):
        found = self.problems({"planning/PARKED.md": "# Parked\n\nIt was ALPHABETA-9.\n"})
        self.assertTrue(any("planning/PARKED.md: names ALPHABETA-9, which is not a task" in p for p in found), found)

    def test_the_repository_is_consistent(self):
        root = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
        self.assertEqual([], check.problems(root))


if __name__ == "__main__":
    unittest.main()
