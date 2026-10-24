using DbDataKit.Cli;

namespace DbDataKit.Cli.Tests;

public sealed class CommandSpecTests
{
    private static readonly CommandSpec Deploy = new("connection deploy", EffectClass.TargetWrites, "Change a structure", true, "deploy", "P⇒S");

    [Fact]
    public void ThePathIsTheWordsOfTheName() => Assert.Equal(["connection", "deploy"], Deploy.Path);

    [Fact]
    public void TheMarksNameTheDispositionAndTheLane() =>
        Assert.Equal("reads/writes: P⇒S  |  lane: deploy", Deploy.Marks);

    [Fact]
    public void TheMarksOmitAnEmptyLane() =>
        Assert.Equal("reads/writes: P→", (Deploy with { Lane = "", Disposition = "P→" }).Marks);

    [Fact]
    public void TheIdIsOneTokenForFileAndSchemaNames() =>
        Assert.Equal("project_agent_kit", new CommandSpec("project agent-kit", EffectClass.RepoFilesOnly, "", true).Id);

    [Fact]
    public void EveryEffectClassHasADescription()
    {
        foreach (var effect in Enum.GetValues<EffectClass>())
            Assert.False(string.IsNullOrWhiteSpace(effect.Describe()), effect.ToString());
    }

    [Fact]
    public void AnEffectClassOutsideTheEnumIsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ((EffectClass)99).Describe());
}
