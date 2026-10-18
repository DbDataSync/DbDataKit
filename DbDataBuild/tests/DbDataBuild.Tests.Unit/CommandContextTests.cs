using DbDataBuild.Cli;

namespace DbDataBuild.Tests.Unit;

/// <summary>The hooks a caller hands a running command (progress, stop) flow with the command and never leak to another one.</summary>
public class CommandContextTests
{
    [Fact]
    public void Hooks_are_in_force_inside_with_and_restored_after_it_even_across_a_thread_hop()
    {
        Assert.Null(CommandContext.Hooks);
        var seen = new List<string>();
        var hooks = new CommandHooks(seen.Add, () => true);
        var inside = CommandContext.With(hooks, () =>
        {
            Assert.Same(hooks, CommandContext.Hooks);
            return Task.Run(() => CommandContext.Hooks).GetAwaiter().GetResult();       // the apply runs on another thread
        });
        Assert.Same(hooks, inside);
        Assert.Null(CommandContext.Hooks);
    }

    [Fact]
    public void Hooks_are_restored_when_the_command_throws_and_nest()
    {
        var outer = new CommandHooks(null, () => false);
        var inner = new CommandHooks(null, () => true);
        CommandContext.With(outer, () =>
        {
            Assert.Throws<InvalidOperationException>(() => CommandContext.With<int>(inner, () => throw new InvalidOperationException()));
            Assert.Same(outer, CommandContext.Hooks);
            return 0;
        });
        Assert.Null(CommandContext.Hooks);
    }

    [Fact]
    public void Two_commands_running_at_once_do_not_see_each_others_hooks()
    {
        var a = new CommandHooks(null, () => true);
        var b = new CommandHooks(null, () => false);
        var gate = new Barrier(2);
        var ta = Task.Run(() => CommandContext.With(a, () => { gate.SignalAndWait(); return CommandContext.Hooks; }));
        var tb = Task.Run(() => CommandContext.With(b, () => { gate.SignalAndWait(); return CommandContext.Hooks; }));
        Assert.Same(a, ta.GetAwaiter().GetResult());
        Assert.Same(b, tb.GetAwaiter().GetResult());
    }
}
