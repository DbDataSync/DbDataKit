using Grpc.Core;

namespace DbDataKit.Serving.FlightSql.Tests;

public class CompressionTests
{
    private static CompressionOptions Options(CompressionMode mode, string codec = "zstd", bool overrideAllowed = true, int? level = null) =>
        new() { Mode = mode, Codec = codec, AllowClientOverride = overrideAllowed, Level = level };

    [Fact]
    public void The_default_is_arrow_compression_with_zstd_and_a_client_may_choose_another()
    {
        var plan = CompressionPlan.Resolve(new CompressionOptions(), null, null);
        Assert.Equal(("Arrow", "zstd"), (plan.Mode.ToString(), plan.Codec));
        Assert.Equal("Arrow:zstd", plan.ToString());
        Assert.Equal("none", CompressionPlan.None.ToString());
    }

    [Theory]
    [InlineData("none", CompressionMode.None, "none")]
    [InlineData("off", CompressionMode.None, "none")]
    [InlineData("identity", CompressionMode.None, "none")]
    [InlineData("gzip", CompressionMode.Grpc, "gzip")]
    [InlineData("ZSTD", CompressionMode.Arrow, "zstd")]
    [InlineData("lz4", CompressionMode.Arrow, "lz4")]
    public void A_client_header_chooses_the_plan_when_the_settings_allow_it(string header, CompressionMode mode, string codec)
    {
        var plan = CompressionPlan.Resolve(Options(CompressionMode.None), null, header);
        Assert.Equal((mode, codec), (plan.Mode, plan.Codec));
    }

    [Fact]
    public void A_header_is_ignored_when_the_settings_forbid_an_override_and_the_sources_settings_beat_the_global_ones()
    {
        Assert.Equal("Arrow:lz4", CompressionPlan.Resolve(Options(CompressionMode.Arrow, "lz4", overrideAllowed: false), null, "gzip").ToString());
        Assert.Equal("Grpc:gzip", CompressionPlan.Resolve(Options(CompressionMode.Arrow), Options(CompressionMode.Grpc, "gzip"), null).ToString());
        Assert.Equal("none", CompressionPlan.Resolve(Options(CompressionMode.Arrow), Options(CompressionMode.None), null).ToString());
    }

    [Fact]
    public void A_header_that_names_no_codec_is_refused_with_the_values_that_work()
    {
        var error = Assert.Throws<RpcException>(() => CompressionPlan.Resolve(new CompressionOptions(), null, "brotli"));

        Assert.Equal(StatusCode.InvalidArgument, error.StatusCode);
        Assert.Contains("none, gzip, zstd or lz4", error.Status.Detail);
    }

    [Theory]
    [InlineData(CompressionMode.Grpc, "zstd")]
    [InlineData(CompressionMode.Arrow, "gzip")]
    public void A_codec_that_does_not_belong_to_the_mode_is_a_configuration_error(CompressionMode mode, string codec) =>
        Assert.Throws<InvalidOperationException>(() => CompressionPlan.FromOptions(Options(mode, codec)));

    [Fact]
    public void Arrow_data_compression_depends_on_private_members_of_the_flight_package_and_this_pins_that_they_are_still_there()
    {
        // if a package update moves them the server refuses to enable the compression instead of sending uncompressed data under a compressed name
        Assert.True(FlightIpcCompression.IsSupported);
        FlightIpcCompression.EnsureSupported();
    }
}
