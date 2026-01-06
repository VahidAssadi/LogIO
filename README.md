# LogIO

LogIO is a lightweight input/output logging helper for .NET services built on `DispatchProxy`. It provides an opt-in `[LogIO]` attribute plus a DI registration that wraps interface-based services and logs arguments, results, duration, and exceptions with minimal overhead.

## Features
- Attribute-based opt-in logging for methods or classes
- Configurable defaults per application
- DI-friendly proxy registration
- Async and sync method support
- Built-in caching and log-level guards to reduce overhead

## Installation
From NuGet:
```bash
dotnet add package LogIO
```

## Usage
Register the proxy once at startup:
```csharp
using LogIO;

builder.Services.AddInputOutputLoggingProxies(options =>
{
    options.NamespacePrefix = "YourApp."; // optional filter
    options.Defaults.Level = LogLevel.Information;
    options.Defaults.LogInput = true;
    options.Defaults.LogOutput = true;
    options.Defaults.LogDuration = true;
    options.Defaults.LogExceptions = true;
    options.LogWhenNoAttribute = false; // opt-in behavior
});
```

Annotate methods or classes:
```csharp
using LogIO;

public class MyService : IMyService
{
    [LogIO]
    public async Task<SampleResult> DoAsync(SampleInput input)
    {
        // ...
    }
}
```

Override per method:
```csharp
[LogIO(Level = LogLevel.Warning, LogOutput = false)]
    public async Task<SampleResult> DoAsync(SampleInput input)
    {
        // ...
    }
```

## Defaults and behavior
- If a method has `[LogIO]`, it will be logged.
- If `LogWhenNoAttribute = true`, all proxied methods are logged using defaults.
- Defaults are applied to attribute values that are left at their default.

## Performance notes
- Serialization happens only if the log level is enabled.
- Attribute resolution is cached per method.
- For hot paths, disable `LogOutput` or enable sampling in your app code.

## Limitations
- Works only with interface-based services resolved via DI.
- Open-generic registrations are not decorated.

## Target frameworks
- `netstandard2.0`
- `net8.0`

## License
Add your license here.
