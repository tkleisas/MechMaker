namespace MechMaker.Core.Validation;

public enum Severity
{
    Error,
    Warning,
    Info
}

public sealed record Diagnostic(string Code, Severity Severity, string Message, string? Subject = null)
{
    public override string ToString() => $"{Severity} {Code}: {Message}" + (Subject is null ? "" : $" [{Subject}]");
}

public sealed class ValidationReport
{
    public List<Diagnostic> Diagnostics { get; } = [];

    public bool HasErrors => Diagnostics.Any(d => d.Severity == Severity.Error);

    public void Add(string code, Severity severity, string message, string? subject = null)
        => Diagnostics.Add(new Diagnostic(code, severity, message, subject));

    public void AddError(string code, string message, string? subject = null)
        => Add(code, Severity.Error, message, subject);

    public void AddWarning(string code, string message, string? subject = null)
        => Add(code, Severity.Warning, message, subject);

    public override string ToString()
        => Diagnostics.Count == 0 ? "OK" : string.Join(Environment.NewLine, Diagnostics);
}
