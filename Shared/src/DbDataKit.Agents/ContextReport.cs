namespace DbDataKit.Agents;

/// <summary>The context an agent would be given for what is in play, with each part's tokens and share of the budget (plan 27a KN28, KN17's <c>knowledge context</c>).</summary>
public sealed record ContextReport(string Agent, IReadOnlyList<ContextPartRecord> Parts, int TotalTokens, int Window, int BudgetTokens, bool OverBudget, string Basis, IReadOnlyList<string> InPlay, IReadOnlyList<ContextPartShare> Shares);

public sealed record ContextPartShare(string Layer, string Id, string Version, int Tokens, double ShareOfBudget);
