/** The slots a script can fill that a generated-input test runs from a skeleton (Sync tests the hook, source-query and verification slots too, but a skeleton for them is a bigger decision than a starting point). */
export const SCRIPT_KINDS = ['sqlColumnExpression', 'valueColumnExpression', 'rowTransform'] as const
export type ScriptKind = (typeof SCRIPT_KINDS)[number]

export const scriptManifestPath = (name: string) => `config/scripts/${name}.yaml`
export const scriptCodePath = (name: string) => `config/scripts/${name}.csx`

/** The names of the scripts a draft has (a manifest in `config/scripts/`), from its file list. */
export const scriptsOf = (paths: readonly string[]): string[] =>
  paths.map((p) => /^config\/scripts\/([^/]+)\.ya?ml$/i.exec(p)?.[1]).filter((n): n is string => !!n).sort()

const USINGS = 'using System;\nusing System.Collections.Generic;\nusing System.Threading;\nusing System.Threading.Tasks;\nusing DbDataSync.Scripting.Abstractions;\n\n'

/** The starting code of each slot: a script ends with an expression (no semicolon), the implementation the slot asks for. */
export const SKELETONS: Record<ScriptKind, string> = {
  sqlColumnExpression: `${USINGS}public sealed class MyScript : ISqlColumnExpression
{
    // Return null to leave the column exactly as it would have been.
    public string? RenderSql(SqlColumnExpressionContext context) => context.SourceColumn == "Name" ? $"UPPER({context.ColumnReference})" : null;
}

new MyScript()
`,
  valueColumnExpression: `${USINGS}public sealed class MyScript : IValueColumnExpression
{
    // The source columns this transforms; every other column passes through untouched.
    public IReadOnlyList<string> DeclareColumns(ValueColumnDeclarationContext context) => ["Name"];

    public object? Evaluate(object? value, ValueColumnExpressionContext context) => value is string text ? text.ToUpperInvariant() : value;
}

new MyScript()
`,
  rowTransform: `${USINGS}public sealed class MyScript : IRowTransform
{
    // Return the input unchanged when the shape of the row is unchanged.
    public ChangeSchema DeclareSchema(ChangeSchema input, RowTransformContext context) => input;

    // Return null to drop the row; deletes reach here too (row.Operation).
    public ValueTask<ChangeRow?> TransformAsync(ChangeRow row, RowTransformContext context, CancellationToken cancellationToken) => ValueTask.FromResult<ChangeRow?>(row);
}

new MyScript()
`,
}

/** A new script as its two files: the manifest (the current format, no parameters) and the code of the slot's skeleton. */
export const newScriptFiles = (name: string, kind: ScriptKind): { path: string; text: string }[] => [
  { path: scriptManifestPath(name), text: ['schemaVersion: 1', `name: ${name}`, `kind: ${kind}`, 'language: CSharp', 'parameters: []', 'enabled: true', ''].join('\n') },
  { path: scriptCodePath(name), text: SKELETONS[kind] },
]
