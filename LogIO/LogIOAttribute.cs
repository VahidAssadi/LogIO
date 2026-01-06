using Microsoft.Extensions.Logging;

namespace LogIO;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true)]
public sealed class LogIOAttribute : Attribute
{
    public LogLevel Level { get; set; } = LogLevel.Information;
    public bool LogInput { get; set; } = true;
    public bool LogOutput { get; set; } = true;
    public bool LogDuration { get; set; } = true;
    public bool LogExceptions { get; set; } = true;
}
