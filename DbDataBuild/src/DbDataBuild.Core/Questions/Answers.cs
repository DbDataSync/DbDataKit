namespace DbDataBuild.Core.Questions;

/// <summary>One answer as written in an answers file: an explicit <c>choice</c> (with a <c>value</c> where the option takes one), or <c>accept: inferred</c>.</summary>
public sealed record Answer(string QuestionId, string? Choice, string? Value, string? Note, bool AcceptInferred, SourceLocation Location);

public sealed record AnswerFile(IReadOnlyList<Answer> Answers);
