import contextlib
import io
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
import check_caught_exceptions as check  # noqa: E402


def tree(files):
    root = tempfile.mkdtemp()
    for rel, text in files.items():
        path = os.path.join(root, rel)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8") as f:
            f.write(text)
    return root


def method(body):
    return "class A\n{\n    void M()\n    {\n" + body + "\n    }\n}\n"


class CheckCaughtExceptions(unittest.TestCase):
    def problems(self, body, path="src/A.cs"):
        return check.check_text(path, method(body))

    def test_a_new_exception_without_the_caught_one_is_found_and_passing_it_passes(self):
        lost = self.problems('try { Run(); }\ncatch (Exception ex) { throw new InvalidOperationException("x"); }')
        self.assertEqual(1, len(lost), lost)
        self.assertTrue(lost[0].startswith("src/A.cs:6: throw new InvalidOperationException"), lost[0])
        self.assertIn("'ex'", lost[0])
        self.assertEqual([], self.problems('try { Run(); }\ncatch (Exception ex) { throw new InvalidOperationException("x", ex); }'))

    def test_the_whole_check_fails_on_a_file_and_passes_once_it_is_fixed(self):
        bad = tree({"src/A.cs": method('try { Run(); } catch (Exception ex) { throw new InvalidOperationException("x"); }')})
        problems, _ = check.check(bad)
        self.assertEqual(1, len(problems), problems)
        good = tree({"src/A.cs": method('try { Run(); } catch (Exception ex) { throw new InvalidOperationException("x", ex); }')})
        self.assertEqual(([], []), check.check(good))
        with contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(0, check.main(["x", good]))
            self.assertEqual(1, check.main(["x", bad]))

    def test_the_message_alone_does_not_pass_the_exception(self):
        for thrown in ('new X(ex.Message)', 'new X($"failed: {ex}")', 'new X($"failed: {ex.Message}", 3)', 'new X(ex.GetType().Name)'):
            with self.subTest(thrown):
                self.assertEqual(1, len(self.problems(f"try {{ Run(); }} catch (IOException ex) {{ throw {thrown}; }}")))

    def test_any_argument_that_is_the_exception_passes(self):
        for thrown in ('new X("m", ex)', 'new X("m", innerException: ex)', 'new X(ex)', 'new X(code: 4, "m", ex)', 'new X("m", ex.InnerException ?? ex)',
                       'new X("m", ex.InnerException)', 'new RpcException(new Status(StatusCode.Internal, "m", ex))', 'new X(\n    "m",\n    ex)'):
            with self.subTest(thrown):
                self.assertEqual([], self.problems(f"try {{ Run(); }} catch (IOException ex) {{ throw {thrown}; }}"))

    def test_a_catch_without_a_variable_is_found(self):
        for clause in ("catch", "catch (IOException)", "catch (System.IO.IOException)", "catch (OperationCanceledException) when (!ct.IsCancellationRequested)"):
            with self.subTest(clause):
                lost = self.problems(f'try {{ Run(); }} {clause} {{ throw new X("m"); }}')
                self.assertEqual(1, len(lost), lost)
                self.assertIn("names no variable", lost[0])

    def test_a_filter_and_a_qualified_or_generic_type_keep_the_variable(self):
        for clause in ("catch (Exception e) when (e is IOException or JsonException)", "catch (System.Text.Json.JsonException e)",
                       "catch (Problem<int> e)", "catch (Exception e) when (Test(e, (1, 2)))"):
            with self.subTest(clause):
                self.assertEqual([], self.problems(f'try {{ Run(); }} {clause} {{ throw new X("m", e); }}'))
                self.assertEqual(1, len(self.problems(f'try {{ Run(); }} {clause} {{ throw new X("m"); }}')))

    def test_a_rethrow_and_a_throw_outside_a_catch_are_not_checked(self):
        self.assertEqual([], self.problems('throw new X("m");'))
        self.assertEqual([], self.problems('try { Run(); } catch (Exception ex) { Log(ex); throw; }'))
        self.assertEqual([], self.problems('try { Run(); } catch (Exception ex) { throw Wrap(ex); }'))
        self.assertEqual([], self.problems('try { throw new X("m"); } catch (Exception ex) { Log(ex); } finally { Done(); }'))

    def test_a_throw_belongs_to_the_innermost_catch(self):
        body = ('try { Run(); }\ncatch (IOException outer)\n{\n    try { Again(); }\n    catch (Exception inner) { throw new X("m", inner); }\n'
                '    throw new X("m", outer);\n}')
        self.assertEqual([], self.problems(body))
        lost = self.problems('try { Run(); }\ncatch (IOException outer)\n{\n    try { Again(); }\n    catch (Exception inner) { throw new X("m", outer); }\n}')
        self.assertEqual(1, len(lost), lost)
        self.assertIn("'inner'", lost[0])

    def test_a_throw_expression_inside_a_catch_is_found(self):
        self.assertEqual(1, len(self.problems('try { Run(); } catch (Exception ex) { return Read() ?? throw new X("m"); }')))

    def test_strings_and_comments_are_not_code(self):
        body = ('var a = "catch (Exception ex) { throw new X(\\"m\\"); }";\n'
                'var b = @"catch { throw new X(""m""); }";\n'
                'var c = $"{(x ? "catch {" : "}")} throw new X(1)";\n'
                'var d = """\n    catch { throw new X("m"); }\n    """;\n'
                "var e = '{';\n"
                "// catch { throw new X(); }\n"
                "/* catch { throw new X(); } */\n"
                'try { Run(); } catch (Exception ex) { throw new X($"a }} {{ {ex.Message}", ex); }')
        self.assertEqual([], self.problems(body))

    def test_a_brace_in_a_string_does_not_end_the_catch(self):
        lost = self.problems('try { Run(); }\ncatch (Exception ex)\n{\n    Log("}");\n    throw new X("m");\n}')
        self.assertEqual(1, len(lost), lost)
        self.assertTrue(lost[0].startswith("src/A.cs:9:"), lost[0])

    def test_the_exception_passed_only_to_a_call_or_an_is_test_is_found(self):
        for thrown in ('new ConnectionFailureException(FileFailures.Classify(ex))', 'new ConnectionFailureException(Classify(ex, ct))',
                       'new X(Describe(ex), [])', 'new X(ex is TimeoutException ? Timeout() : Classify(ex))', 'new X(Wrap(ex).Message)'):
            with self.subTest(thrown):
                self.assertEqual(1, len(self.problems(f"try {{ Run(); }} catch (IOException ex) {{ throw {thrown}; }}")))
        for thrown in ('new ConnectionFailureException(FileFailures.Classify(ex), ex)', 'new X(Describe(ex), [], ex)', 'new X("m", (ex))',
                       'new X("m", new Y(Describe(ex), ex))', 'new X(ex is TimeoutException ? "t" : "o", ex)'):
            with self.subTest(thrown):
                self.assertEqual([], self.problems(f"try {{ Run(); }} catch (IOException ex) {{ throw {thrown}; }}"))

    def test_the_baseline_tolerates_its_count_and_only_shrinks(self):
        two = method('try { Run(); } catch (Exception ex) { throw new X("a"); }\ntry { Run(); } catch (Exception ex) { throw new X("b"); }')
        root = tree({"src/A.cs": two, check.BASELINE: "# comment\nsrc/A.cs: 2\n"})
        problems, warnings = check.check(root)
        self.assertEqual([], problems)
        self.assertEqual(1, len(warnings))

        root = tree({"src/A.cs": two, check.BASELINE: "src/A.cs: 1\n"})
        problems, _ = check.check(root)
        self.assertIn("the baseline allows 1", problems[0])
        self.assertEqual(3, len(problems), problems)

        root = tree({"src/A.cs": two, check.BASELINE: "src/A.cs: 3\n"})
        problems, _ = check.check(root)
        self.assertEqual(1, len(problems))
        self.assertIn("lower it", problems[0])

        root = tree({"src/A.cs": method("Run();"), check.BASELINE: "src/A.cs: 1\n"})
        problems, _ = check.check(root)
        self.assertEqual(1, len(problems))
        self.assertIn("delete the line", problems[0])

    def test_a_new_finding_in_a_file_outside_the_baseline_fails(self):
        lost = method('try { Run(); } catch (Exception ex) { throw new X("a"); }')
        root = tree({"src/A.cs": lost, "src/B.cs": lost, check.BASELINE: "src/A.cs: 1\n"})
        problems, warnings = check.check(root)
        self.assertEqual(1, len(problems), problems)
        self.assertTrue(problems[0].startswith("src/B.cs:5: "), problems[0])
        self.assertEqual(["src/A.cs: 1 (baseline)"], warnings)

    def test_the_baseline_of_the_repository_is_well_formed_and_names_cs_files(self):
        root = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
        for path, count in check.load_baseline(root).items():
            self.assertTrue(path.endswith(".cs") and count > 0, (path, count))

    def test_a_malformed_baseline_line_is_refused(self):
        root = tree({"src/A.cs": method("Run();"), check.BASELINE: "src/A.cs\n"})
        with self.assertRaises(ValueError):
            check.check(root)

if __name__ == "__main__":
    unittest.main()
