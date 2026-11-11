namespace DbDataKit.Agents;

/// <summary>Counts tokens with the bound model's own tokenizer when there is one (plan 27a AG11).</summary>
public interface ITokenCounter
{
    int Count(string text);

    /// <summary>How the count was made, for the message of a refusal: <c>an estimate (characters divided by 3.5, plus 15%)</c> or the provider's name.</summary>
    string Basis { get; }
}

/// <summary>
/// The count for a model with no local tokenizer (decision SD15): a conservative estimate, characters divided by 3.5 plus 15%, so a refusal at publish is never made on a number that is too low. Where the provider has a token-count call, a counter that uses it
/// replaces this one.
/// </summary>
public sealed class EstimateCounter : ITokenCounter
{
    public static readonly EstimateCounter Instance = new();

    public int Count(string text) => (int)Math.Ceiling(text.Length / 3.5 * 1.15);

    public string Basis => "an estimate (characters divided by 3.5, plus 15%)";
}
