namespace DbDataKit.Access.Tests;

/// <summary>Plan 22a GP13: the column rules of a section resolved for one caller. The strongest treatment wins (hide, then null, then mask), a person is named by id or name, a group by any of the caller's groups, all ignoring case.</summary>
public sealed class ColumnAccessTests
{
    private static AccessSection Section(string columns) => AccessSectionTests.Read("columns:\n" + columns, out _)!;

    private const string Rules = """
          - { subject: support, dataset: customers, column: email, treatment: mask, mask: hash }
          - { subject: interns, dataset: customers, column: email, treatment: mask }
          - { subject: interns, dataset: customers, column: phone, treatment: "null" }
          - { subject: contractors, dataset: customers, column: phone, treatment: hide }
          - { subject: ann@example.com, subject_kind: person, dataset: customers, column: salary, treatment: hide }
        """;

    [Fact]
    public void A_caller_no_rule_names_sees_everything_and_has_the_empty_fingerprint()
    {
        var none = ColumnTreatments.For(Section(Rules), "u9", "zed", ["staff"]);
        Assert.True(none.IsEmpty);
        Assert.Equal("-", none.Fingerprint);
        Assert.Null(none.Of("customers", "email"));
        Assert.True(ColumnTreatments.For(null, "u", "u", []).IsEmpty);
    }

    [Fact]
    public void The_strongest_treatment_wins_when_several_rules_match()
    {
        var section = Section(Rules);
        var both = ColumnTreatments.For(section, "u1", "bo", ["interns", "contractors"]);
        Assert.Equal(ColumnTreatment.Hide, both.Of("customers", "phone")!.Treatment);       // hide beats null
        var nullAndMask = ColumnTreatments.For(section, "u1", "bo", ["interns"]);
        Assert.Equal(ColumnTreatment.Null, nullAndMask.Of("customers", "phone")!.Treatment);
        var masks = ColumnTreatments.For(section, "u1", "bo", ["support", "interns"]);
        Assert.Equal(new ColumnTreatmentOf(ColumnTreatment.Mask, MaskKind.Fixed), masks.Of("customers", "email"));   // the fixed mask reveals less than the hash
        Assert.Equal(new ColumnTreatmentOf(ColumnTreatment.Mask, MaskKind.Hash), ColumnTreatments.For(section, "u1", "bo", ["support"]).Of("customers", "email"));
    }

    [Fact]
    public void A_person_rule_names_the_id_or_the_name_and_a_group_rule_only_a_group_ignoring_case()
    {
        var section = Section(Rules);
        Assert.NotNull(ColumnTreatments.For(section, "u1", "Ann@Example.com", []).Of("customers", "salary"));
        Assert.NotNull(ColumnTreatments.For(section, "ANN@example.com", "ann", []).Of("customers", "salary"));
        Assert.Null(ColumnTreatments.For(section, "u1", "ann", ["ann@example.com"]).Of("customers", "salary"));     // a group of that name is not the person
        Assert.NotNull(ColumnTreatments.For(section, "u1", "bo", ["INTERNS"]).Of("customers", "phone"));
        Assert.Null(ColumnTreatments.For(section, "interns", "interns", []).Of("customers", "phone"));              // a person named like a group is not in the group
    }

    [Fact]
    public void Callers_with_equal_treatments_share_a_fingerprint_and_others_do_not()
    {
        var section = Section(Rules);
        var a = ColumnTreatments.For(section, "u1", "a", ["interns"]).Fingerprint;
        Assert.Equal(a, ColumnTreatments.For(section, "u2", "b", ["Interns", "staff"]).Fingerprint);
        Assert.NotEqual(a, ColumnTreatments.For(section, "u3", "c", ["interns", "contractors"]).Fingerprint);
        Assert.NotEqual(a, ColumnTreatments.For(section, "u3", "c", ["support"]).Fingerprint);
    }

    [Fact]
    public void A_hash_mask_is_deterministic_keyed_per_column_and_not_the_value()
    {
        byte[] key = [1, 2, 3];
        var mask = ColumnMasks.Hash(key, "customers", "email", "ann@example.com");
        Assert.Equal(mask, ColumnMasks.Hash(key, "customers", "email", "ann@example.com"));
        Assert.NotEqual(mask, ColumnMasks.Hash([1, 2, 4], "customers", "email", "ann@example.com"));
        Assert.NotEqual(mask, ColumnMasks.Hash(key, "customers", "phone", "ann@example.com"));
        Assert.NotEqual(mask, ColumnMasks.Hash(key, "customers", "email", "bob@example.com"));
        Assert.Equal(17, mask.Length);
        Assert.Equal(mask.Length, ColumnMasks.Hash(key, "customers", "email", new string('x', 500)).Length);    // the length of the value does not show
    }
}
