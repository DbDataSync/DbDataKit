namespace DbDataKit.State;

/// <summary>
/// The <c>bindings</c> module (plan 27a IX17 and IX23, <c>docs/channel-bindings.md</c>): the one table <c>posture_choice</c>, the same shape as the runner's and read and written by the same code (<see cref="PostureChoices"/> and <see cref="PostureGuard"/>
/// with this module's name), for a server that keeps bindings and has no <c>runner</c> module: the console, whose <c>bindings.yml</c> binds delivery channels. A server keeps the posture choices of <b>its own</b> bindings in its own state:
/// the Runner in <c>runner.posture_choice</c> (its slots), the console here (its delivery channels). The scope is <c>bindings/&lt;section&gt;/&lt;slot&gt;/&lt;release channel&gt;</c>, the setting <c>production_binding_on_non_production</c>.
/// Scope: one copy per install. Migrations are appended, never edited.
/// </summary>
public static class BindingPostureModule
{
    public const string Name = "bindings";

    public static StateModule Module { get; } = new(
        Name,
        StateScopeKind.Install,
        [
            """
            -- A loosened default of a binding, recorded: scope names the binding (bindings/<section>/<slot>/<release channel>), setting what was loosened; who chose it, when, and why.
            CREATE TABLE {table:posture_choice} (
                scope         {{key}} NOT NULL,
                setting       {{key}} NOT NULL,
                chosen_by     {{key}} NOT NULL,
                chosen_at     {{time}} NOT NULL,
                note          {{text}} NULL,
                PRIMARY KEY (scope, setting)
            );
            """,
        ]);
}
