using Microsoft.Extensions.DependencyInjection;

namespace LogIO;

public sealed class LoggingProxyOptions
{
    public string NamespacePrefix { get; set; } = string.Empty;
    public Func<ServiceDescriptor, bool>? Filter { get; set; }
    public LogIODefaults Defaults { get; } = new();
    public bool LogWhenNoAttribute { get; set; }
    public bool ApplyDefaultsToAttributeValues { get; set; } = true;
}

