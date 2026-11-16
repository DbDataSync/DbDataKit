namespace DbDataBuild.Cli;

/// <summary>
/// Which schema describes which file of a project (the same table as <c>.vscode/settings.json</c>, which a test compares), what the tests check the templates against (plan 13, W05; the console's forms that read it went with the Build server, plan 33 SC23, and the Workspace's Build forms use the same schemas).
/// The first pattern that matches a path wins, so the folder file is listed before the model glob that would also match it.
/// </summary>
internal static class FileSchemas
{
    /// <param name="Pattern">A project-relative path pattern: <c>**</c> crosses folders, <c>*</c> does not.</param>
    /// <param name="Schema">The schema's file name under <c>schemas/</c>.</param>
    /// <param name="Kind">What the file is called on screen.</param>
    public sealed record Association(string Pattern, string Schema, string Kind);

    public static readonly IReadOnlyList<Association> All =
    [
        new("models/**/_dbdatabuild.yml", "folder.schema.json", "folder settings"),
        new("models/**/*.yml", "model.schema.json", "model"),
        new("tests/models/**/*.yml", "model-test.schema.json", "model test"),
        new("semantic/**/*.yml", "semantic.schema.json", "semantic model"),
        new("dashboards/**/*.yaml", "dashboard.schema.json", "dashboard"),
        new("dashboards/**/*.yml", "dashboard.schema.json", "dashboard"),
        new("reports/**/*.yaml", "report.schema.json", "report"),
        new("reports/**/*.yml", "report.schema.json", "report"),
        new("dbdatabuild.yml", "config.schema.json", "project"),
        new("connections/*.yaml", "connection.schema.json", "connection"),
        new("answers.yml", "answers.schema.json", "answers"),
    ];
}
