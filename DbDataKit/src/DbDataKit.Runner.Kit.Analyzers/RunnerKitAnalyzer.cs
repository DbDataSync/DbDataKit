using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace DbDataKit.Runner.Kit.Analyzers
{
    /// <summary>
    /// The two checks of the runner kit the type system cannot make (plan 28b section 3, decision K13): two steps, units or counters with one name in one class (the console groups by name, and the runner refuses the
    /// second at its first use), and a step called inside the body of another step (refused at run time naming both; this finds it before a run).
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class RunnerKitAnalyzer : DiagnosticAnalyzer
    {
        public const string DuplicateNameId = "DBKR001";
        public const string NestedStepId = "DBKR002";

        private const string KitNamespace = "DbDataKit.Runner.Kit";

        private static readonly DiagnosticDescriptor DuplicateName = new DiagnosticDescriptor(
            DuplicateNameId,
            "Two definitions share a name",
            "{0}",
            "DbDataKit.Runner.Kit",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            customTags: WellKnownDiagnosticTags.CompilationEnd,
            description: "Steps, units and counters are found by their names (the console groups by them and the run refuses a second definition under a name): a name is used once per job class, or once per static class that holds definitions.");

        private static readonly DiagnosticDescriptor NestedStep = new DiagnosticDescriptor(
            NestedStepId,
            "A step is called inside another step",
            "{0}",
            "DbDataKit.Runner.Kit",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: "A step is the smallest piece that is remembered: it may not contain another step. Compose steps in the job, one after the other.");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(DuplicateName, NestedStep);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationStartAction(start =>
            {
                var definitions = new ConcurrentBag<Definition>();
                start.RegisterSyntaxNodeAction(c => CollectDefinition(c, definitions), SyntaxKind.InvocationExpression, SyntaxKind.ObjectCreationExpression, SyntaxKind.ImplicitObjectCreationExpression);
                start.RegisterSyntaxNodeAction(CheckNestedStep, SyntaxKind.InvocationExpression);
                start.RegisterCompilationEndAction(end => ReportDuplicates(end, definitions));
            });
        }

        // ---- duplicate names

        private sealed class Definition
        {
            public Definition(INamedTypeSymbol holder, string kind, string name, Location location)
            {
                Holder = holder;
                Kind = kind;
                Name = name;
                Location = location;
            }

            public INamedTypeSymbol Holder { get; }

            public string Kind { get; }

            public string Name { get; }

            public Location Location { get; }
        }

        private static void CollectDefinition(SyntaxNodeAnalysisContext context, ConcurrentBag<Definition> definitions)
        {
            var node = context.Node;
            string? kind = null;
            ArgumentListSyntax? arguments = null;
            switch (node)
            {
                case InvocationExpressionSyntax invocation:
                    if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is IMethodSymbol method && IsKit(method.ContainingType))
                    {
                        if (method.ContainingType.Name == "Step" && (method.Name == "Value" || method.Name == "Stream")) kind = "step";
                        else if (method.ContainingType.Name == "Unit" && method.Name == "Of") kind = "unit";
                        arguments = invocation.ArgumentList;
                    }

                    break;
                case BaseObjectCreationExpressionSyntax creation:
                    if (context.SemanticModel.GetTypeInfo(creation, context.CancellationToken).Type is INamedTypeSymbol created && created.Name == "Counter" && IsKit(created))
                    {
                        kind = "counter";
                        arguments = creation.ArgumentList;
                    }

                    break;
            }

            if (kind == null || arguments == null || arguments.Arguments.Count == 0) return;
            var constant = context.SemanticModel.GetConstantValue(arguments.Arguments[0].Expression, context.CancellationToken);
            if (!constant.HasValue || !(constant.Value is string name)) return;
            var holder = context.ContainingSymbol?.ContainingType;
            if (holder == null) return;
            definitions.Add(new Definition(holder, kind, name, node.GetLocation()));
        }

        private static void ReportDuplicates(CompilationAnalysisContext context, ConcurrentBag<Definition> definitions)
        {
            foreach (var group in definitions.GroupBy(d => (Holder: d.Holder, d.Kind, d.Name), d => d, new KeyComparer()))
            {
                var ordered = group.OrderBy(d => d.Location.SourceTree?.FilePath ?? string.Empty, System.StringComparer.Ordinal).ThenBy(d => d.Location.SourceSpan.Start).ToList();
                for (var i = 1; i < ordered.Count; i++)
                {
                    var message = $"The {group.Key.Kind} name '{group.Key.Name}' is already used by another {group.Key.Kind} in {group.Key.Holder.Name}; names are unique per class.";
                    context.ReportDiagnostic(Diagnostic.Create(DuplicateName, ordered[i].Location, message));
                }
            }
        }

        private sealed class KeyComparer : IEqualityComparer<(INamedTypeSymbol Holder, string Kind, string Name)>
        {
            public bool Equals((INamedTypeSymbol Holder, string Kind, string Name) x, (INamedTypeSymbol Holder, string Kind, string Name) y) =>
                SymbolEqualityComparer.Default.Equals(x.Holder, y.Holder) && x.Kind == y.Kind && x.Name == y.Name;

            public int GetHashCode((INamedTypeSymbol Holder, string Kind, string Name) obj) =>
                (SymbolEqualityComparer.Default.GetHashCode(obj.Holder) * 31 + obj.Kind.GetHashCode()) * 31 + obj.Name.GetHashCode();
        }

        // ---- a step inside a step

        private static void CheckNestedStep(SyntaxNodeAnalysisContext context)
        {
            var invocation = (InvocationExpressionSyntax)context.Node;
            if (!IsStepCall(context, invocation)) return;

            for (SyntaxNode? node = invocation.Parent; node != null; node = node.Parent)
            {
                if (node is MemberDeclarationSyntax) return;
                if (!(node is AnonymousFunctionExpressionSyntax lambda)) continue;
                if (!(lambda.Parent is ArgumentSyntax argument) || !(argument.Parent is ArgumentListSyntax list) || !(list.Parent is InvocationExpressionSyntax outer)) continue;
                if (!IsStepBody(context, outer)) continue;
                var inner = StepNameOf(context, invocation);
                var outerName = StepNameOf(context, outer);
                var message = $"The step call {(inner == null ? "" : "'" + inner + "' ")}is inside the body of {(outerName == null ? "another step" : "the step '" + outerName + "'")}; a step may not contain another step, so call them one after the other in the job.";
                context.ReportDiagnostic(Diagnostic.Create(NestedStep, invocation.GetLocation(), message));
                return;
            }
        }

        /// <summary><c>Step(definition, input)</c>, the member of the job class.</summary>
        private static bool IsStepCall(SyntaxNodeAnalysisContext context, InvocationExpressionSyntax invocation)
        {
            if (!(context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is IMethodSymbol method)) return false;
            return method.Name == "Step" && IsKit(method.ContainingType) && method.ContainingType.OriginalDefinition.Name == "RunnerJob";
        }

        /// <summary><c>….RunAsync(body, ct)</c> of a value step's runner or <c>….StreamAsync(body, ct)</c> of a stream step's.</summary>
        private static bool IsStepBody(SyntaxNodeAnalysisContext context, InvocationExpressionSyntax invocation)
        {
            if (!(context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is IMethodSymbol method)) return false;
            var type = method.ContainingType.OriginalDefinition.Name;
            return IsKit(method.ContainingType) && ((type == "ValueRunner" && method.Name == "RunAsync") || (type == "StreamRunner" && method.Name == "StreamAsync"));
        }

        /// <summary>The name of the step a <c>Step(…)</c> call or a call chain ending in the runner's method stands for, when its definition is a constant-named field.</summary>
        private static string? StepNameOf(SyntaxNodeAnalysisContext context, InvocationExpressionSyntax invocation)
        {
            // walk down the chain .RunAsync(..) <- .Check(..) <- Step(def, input) to the Step call, and name its first argument's field
            SyntaxNode? current = invocation;
            while (current is InvocationExpressionSyntax call)
            {
                if (IsStepCall(context, call))
                {
                    var definition = call.ArgumentList.Arguments.Count > 0 ? call.ArgumentList.Arguments[0].Expression : null;
                    return definition == null ? null : FieldDefinitionName(context, definition);
                }

                current = (call.Expression as MemberAccessExpressionSyntax)?.Expression;
            }

            return null;
        }

        private static string? FieldDefinitionName(SyntaxNodeAnalysisContext context, ExpressionSyntax definition)
        {
            if (!(context.SemanticModel.GetSymbolInfo(definition, context.CancellationToken).Symbol is IFieldSymbol field)) return null;
            foreach (var reference in field.DeclaringSyntaxReferences)
            {
                if (!(reference.GetSyntax(context.CancellationToken) is VariableDeclaratorSyntax declarator) || declarator.Initializer == null) continue;
                if (declarator.Initializer.Value is InvocationExpressionSyntax creation && creation.ArgumentList.Arguments.Count > 0
                    && creation.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
                    return literal.Token.ValueText;
            }

            return null;
        }

        private static bool IsKit(INamedTypeSymbol? type) =>
            type != null && type.ContainingNamespace != null && type.ContainingNamespace.ToDisplayString() == KitNamespace;
    }
}
