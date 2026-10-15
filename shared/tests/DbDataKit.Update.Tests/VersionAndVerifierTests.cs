using System.Text;

namespace DbDataKit.Update.Tests;

public class VersionTests
{
    [Theory]
    [InlineData("1.0.0", "1.0.1", -1)]
    [InlineData("1.10.0", "1.9.0", 1)]
    [InlineData("1.0.0-beta.1", "1.0.0", -1)]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.10", -1)]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1", -1)]
    [InlineData("1.0.0-1", "1.0.0-a", -1)]
    [InlineData("v2.0.0+build5", "2.0.0", 0)]
    public void Compares(string a, string b, int expected)
        => Assert.Equal(expected, Math.Sign(ReleaseVersion.Parse(a).CompareTo(ReleaseVersion.Parse(b))));

    [Theory]
    [InlineData("1.2.3", Channel.Stable)]
    [InlineData("1.2.3-beta.4", Channel.Beta)]
    [InlineData("1.2.3-rc.1", Channel.Beta)]
    [InlineData("1.2.3-snapshot.20260101", Channel.Snapshot)]
    [InlineData("1.2.3-ci.7", Channel.Snapshot)]
    public void InfersChannel(string v, Channel c) => Assert.Equal(c, ReleaseVersion.Parse(v).Channel);

    [Theory]
    [InlineData("1.2")]
    [InlineData("01.2.3")]
    [InlineData("")]
    [InlineData("1.2.3-")]
    public void RejectsGarbage(string v) => Assert.False(ReleaseVersion.TryParse(v, out _));
}

public class VerifierTests
{
    private static byte[] Manifest(string version = "1.4.0", string product = "dbdataproxy")
        => ManifestBuilder.ToBytes(new ReleaseManifest { Product = product, Version = version, Channel = "stable" });

    [Fact]
    public void ValidSignatureAccepted()
    {
        var k = new KeyPair("k1");
        var bytes = Manifest();
        var v = ReleaseVerifier.VerifyManifest(bytes, ManifestSigner.Sign(bytes, k.Seed, k.Id), k.Trust(), "dbdataproxy");
        Assert.Equal("1.4.0", v.Manifest.Version);
        Assert.Equal(64, v.Sha256.Length);
    }

    [Fact]
    public void TamperedManifestRejected()
    {
        var k = new KeyPair("k1");
        var bytes = Manifest();
        var sig = ManifestSigner.Sign(bytes, k.Seed, k.Id);
        var evil = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("1.4.0", "9.9.9"));
        var ex = Assert.Throws<UpdateVerificationException>(() => ReleaseVerifier.VerifyManifest(evil, sig, k.Trust()));
        Assert.Equal(VerificationFailure.BadSignature, ex.Failure);
    }

    [Fact]
    public void WrongKeyRejected()
    {
        var real = new KeyPair("k1");
        var attacker = new KeyPair("k1"); // same id, different key
        var bytes = Manifest();
        var ex = Assert.Throws<UpdateVerificationException>(() =>
            ReleaseVerifier.VerifyManifest(bytes, ManifestSigner.Sign(bytes, attacker.Seed, "k1"), real.Trust()));
        Assert.Equal(VerificationFailure.BadSignature, ex.Failure);
    }

    [Fact]
    public void UnknownKeyIdRejected()
    {
        var real = new KeyPair("k1");
        var other = new KeyPair("k2");
        var bytes = Manifest();
        var ex = Assert.Throws<UpdateVerificationException>(() =>
            ReleaseVerifier.VerifyManifest(bytes, ManifestSigner.Sign(bytes, other.Seed, "k2"), real.Trust()));
        Assert.Equal(VerificationFailure.UnknownKey, ex.Failure);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("dbdata-sig/1 k1 !!!notbase64")]
    public void MalformedSignatureRejected(string sig)
    {
        var k = new KeyPair("k1");
        var ex = Assert.Throws<UpdateVerificationException>(() => ReleaseVerifier.VerifyManifest(Manifest(), sig, k.Trust()));
        Assert.Equal(VerificationFailure.MalformedSignature, ex.Failure);
    }

    [Fact]
    public void WrongProductRejected()
    {
        var k = new KeyPair("k1");
        var bytes = Manifest(product: "dbdatasync");
        var ex = Assert.Throws<UpdateVerificationException>(() =>
            ReleaseVerifier.VerifyManifest(bytes, ManifestSigner.Sign(bytes, k.Seed, k.Id), k.Trust(), "dbdataproxy"));
        Assert.Equal(VerificationFailure.WrongProduct, ex.Failure);
    }

    [Fact]
    public void SignedButInvalidManifestRejected()
    {
        var k = new KeyPair("k1");
        var bytes = Encoding.UTF8.GetBytes("{\"schema\":\"release/2\",\"product\":\"x\",\"version\":\"1.0.0\"}");
        var ex = Assert.Throws<UpdateVerificationException>(() =>
            ReleaseVerifier.VerifyManifest(bytes, ManifestSigner.Sign(bytes, k.Seed, k.Id), k.Trust()));
        Assert.Equal(VerificationFailure.MalformedManifest, ex.Failure);
    }

    [Fact]
    public void KeyRotationSignedByPreviousKeyWorksAndRetiresOldKey()
    {
        var old = new KeyPair("2026-a");
        var next = new KeyPair("2027-a");
        var store = old.Trust();
        var km = TrustStore.CreateKeyManifest(1, [new TrustedKey(next.Id, next.Public)], ["2026-a"], old.Seed, old.Id);
        var rotated = store.Rotate(km);
        Assert.Equal(1, rotated.Sequence);
        var bytes = Manifest();
        ReleaseVerifier.VerifyManifest(bytes, ManifestSigner.Sign(bytes, next.Seed, next.Id), rotated);
        var ex = Assert.Throws<UpdateVerificationException>(() =>
            ReleaseVerifier.VerifyManifest(bytes, ManifestSigner.Sign(bytes, old.Seed, old.Id), rotated));
        Assert.Equal(VerificationFailure.UnknownKey, ex.Failure);
    }

    [Fact]
    public void KeyRotationSignedByStrangerRejected()
    {
        var old = new KeyPair("2026-a");
        var stranger = new KeyPair("2026-a");
        var evil = new KeyPair("evil");
        var km = TrustStore.CreateKeyManifest(1, [new TrustedKey(evil.Id, evil.Public)], [], stranger.Seed, "2026-a");
        var ex = Assert.Throws<UpdateVerificationException>(() => old.Trust().Rotate(km));
        Assert.Equal(VerificationFailure.KeyRotationRejected, ex.Failure);
    }

    [Fact]
    public void KeyRotationReplayRejected()
    {
        var old = new KeyPair("a");
        var next = new KeyPair("b");
        var km = TrustStore.CreateKeyManifest(1, [new TrustedKey(next.Id, next.Public)], [], old.Seed, "a");
        var rotated = old.Trust().Rotate(km);
        Assert.Throws<UpdateVerificationException>(() => rotated.Rotate(km));
    }

    [Fact]
    public void KeyRotationCannotEmptyTheStore()
    {
        var old = new KeyPair("a");
        var km = TrustStore.CreateKeyManifest(1, [], ["a"], old.Seed, "a");
        Assert.Throws<UpdateVerificationException>(() => old.Trust().Rotate(km));
    }
}
