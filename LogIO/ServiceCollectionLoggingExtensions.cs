using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LogIO;

public sealed class LogIODefaults
{
    public LogLevel Level { get; set; } = LogLevel.Information;
    public bool LogInput { get; set; } = true;
    public bool LogOutput { get; set; } = true;
    public bool LogDuration { get; set; } = true;
    public bool LogExceptions { get; set; } = true;
}

public static class ServiceCollectionLoggingExtensions
{
    public static IServiceCollection AddInputOutputLoggingProxies(this IServiceCollection services, Action<LoggingProxyOptions>? configure = null)
    {
        var options = new LoggingProxyOptions();
        configure?.Invoke(options);

        for (var i = 0; i < services.Count; i++)
        {
            var descriptor = services[i];
            if (!ShouldDecorate(descriptor, options))
            {
                continue;
            }

            services[i] = CreateDecoratedDescriptor(descriptor, options);
        }

        return services;
    }

    private static bool ShouldDecorate(ServiceDescriptor descriptor, LoggingProxyOptions options)
    {
        if (!descriptor.ServiceType.IsInterface)
        {
            return false;
        }

        if (descriptor.ServiceType.IsGenericTypeDefinition)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(options.NamespacePrefix))
        {
            var ns = descriptor.ServiceType.Namespace;
            if (ns == null || !ns.StartsWith(options.NamespacePrefix, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return options.Filter?.Invoke(descriptor) ?? true;
    }

    private static ServiceDescriptor CreateDecoratedDescriptor(ServiceDescriptor descriptor, LoggingProxyOptions options)
    {
        return ServiceDescriptor.Describe(
            descriptor.ServiceType,
            sp =>
            {
                var decorated = CreateInstance(sp, descriptor);
                return LoggingProxyFactory.Create(descriptor.ServiceType, decorated, sp, options);
            },
            descriptor.Lifetime);
    }

    private static object CreateInstance(IServiceProvider sp, ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationInstance != null)
        {
            return descriptor.ImplementationInstance;
        }

        if (descriptor.ImplementationFactory != null)
        {
            return descriptor.ImplementationFactory(sp);
        }

        if (descriptor.ImplementationType == null)
        {
            throw new InvalidOperationException($"Service descriptor for {descriptor.ServiceType} has no implementation.");
        }

        return ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType);
    }
}

internal static class LoggingProxyFactory
{
    private static readonly MethodInfo DispatchProxyCreateMethod = typeof(DispatchProxy)
        .GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Single(m => m.Name == nameof(DispatchProxy.Create) && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);

    public static object Create(Type serviceType, object decorated, IServiceProvider services, LoggingProxyOptions options)
    {
        var proxyType = typeof(LoggingProxy<>).MakeGenericType(serviceType);
        var createMethod = DispatchProxyCreateMethod.MakeGenericMethod(serviceType, proxyType);
        var proxy = createMethod.Invoke(null, Array.Empty<object?>());
        if (proxy is not ILoggingProxy loggingProxy)
        {
            throw new InvalidOperationException($"Proxy for {serviceType} does not implement {nameof(ILoggingProxy)}.");
        }

        var loggerFactory = services.GetRequiredService<ILoggerFactory>();
        loggingProxy.Initialize(decorated, loggerFactory, options.Defaults, options.LogWhenNoAttribute, options.ApplyDefaultsToAttributeValues);
        return proxy!;
    }
}
