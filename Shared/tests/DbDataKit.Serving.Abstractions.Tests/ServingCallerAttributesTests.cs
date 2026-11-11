using DbDataKit.Serving;
using Xunit;

namespace DbDataKit.Serving.Tests;

/// <summary>Plan 22a D4, GP04: a caller's directory attributes are multi-valued and absent means none; the single-valued claims are untouched.</summary>
public class ServingCallerAttributesTests
{
    [Fact]
    public void A_caller_has_no_attributes_unless_the_identity_provider_gave_some()
    {
        var caller = new ServingCaller("u1", "alice");

        Assert.Empty(caller.Attributes);
        Assert.Empty(caller.Claims);
    }

    [Fact]
    public void A_caller_carries_several_values_of_one_attribute_and_a_copy_keeps_them()
    {
        var caller = new ServingCaller("u1", "alice", new HashSet<string>(), new Dictionary<string, string> { ["method"] = "token" })
        {
            Attributes = new Dictionary<string, IReadOnlyList<string>> { ["region"] = ["east", "west", "north"] },
        };

        var copy = caller with { Name = "Alice" };

        Assert.Equal(["east", "west", "north"], copy.Attributes["region"]);
        Assert.Equal("token", copy.Claims["method"]);
    }
}
