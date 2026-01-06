using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace LogIO;

internal interface ILoggingProxy
{
    void Initialize(object decorated, ILoggerFactory loggerFactory, LogIODefaults defaults, bool logWhenNoAttribute, bool applyDefaultsToAttributes);
}

public class LoggingProxy<TService> : DispatchProxy, ILoggingProxy where TService : class
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        ReferenceHandler = ReferenceHandler.IgnoreCycles
    };

    private static readonly LogIOAttribute AttributeDefaults = new();

    private readonly ConcurrentDictionary<MethodInfo, LogIOAttribute?> _attributeCache = new();
    private TService? _decorated;
    private ILogger? _logger;
    private string? _serviceName;
    private LogIODefaults? _defaults;
    private bool _logWhenNoAttribute;
    private bool _applyDefaultsToAttributes;

    public void Initialize(object decorated, ILoggerFactory loggerFactory, LogIODefaults defaults, bool logWhenNoAttribute, bool applyDefaultsToAttributes)
    {
        _decorated = (TService)decorated ?? throw new ArgumentNullException(nameof(decorated));
        _logger = loggerFactory.CreateLogger(_decorated.GetType());
        _serviceName = _decorated.GetType().Name;
        _defaults = defaults ?? throw new ArgumentNullException(nameof(defaults));
        _logWhenNoAttribute = logWhenNoAttribute;
        _applyDefaultsToAttributes = applyDefaultsToAttributes;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod == null) throw new ArgumentNullException(nameof(targetMethod));
        if (_decorated == null || _logger == null || _serviceName == null)
            throw new InvalidOperationException("Logging proxy is not initialized.");

        var attribute = GetEffectiveAttribute(targetMethod);
        if (attribute == null)
        {
            return targetMethod.Invoke(_decorated, args);
        }

        var logLevelEnabled = _logger.IsEnabled(attribute.Level);
        var errorEnabled = attribute.LogExceptions && _logger.IsEnabled(LogLevel.Error);
        var outputEnabled = attribute.LogOutput && logLevelEnabled;
        var inputEnabled = attribute.LogInput && logLevelEnabled;
        Stopwatch? sw = attribute.LogDuration && (outputEnabled || errorEnabled) ? Stopwatch.StartNew() : null;

        if (inputEnabled)
        {
            _logger.Log(attribute.Level,
                "Call {Service}.{Method} started. Args: {Args}",
                _serviceName,
                targetMethod.Name,
                SerializeArguments(targetMethod, args));
        }

        try
        {
            var result = targetMethod.Invoke(_decorated, args);
            if (result is Task task)
            {
                return InterceptAsync(task, targetMethod, attribute, sw);
            }

            if (attribute.LogOutput)
            {
                LogCompleted(targetMethod, attribute, sw, result);
            }

            return result;
        }
        catch (TargetInvocationException ex)
        {
            var actual = ex.InnerException ?? ex;
            LogException(targetMethod, attribute, sw, actual);
            throw actual;
        }
        catch (Exception ex)
        {
            LogException(targetMethod, attribute, sw, ex);
            throw;
        }
    }

    private object InterceptAsync(Task task, MethodInfo targetMethod, LogIOAttribute attribute, Stopwatch? sw)
    {
        if (targetMethod.ReturnType == typeof(Task))
        {
            return InterceptAsyncInternal(task, targetMethod, attribute, sw);
        }

        if (targetMethod.ReturnType.IsGenericType &&
            targetMethod.ReturnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var resultType = targetMethod.ReturnType.GetGenericArguments()[0];
            var method = typeof(LoggingProxy<TService>)
                .GetMethod(nameof(InterceptAsyncInternalWithResult), BindingFlags.Instance | BindingFlags.NonPublic);
            var generic = method!.MakeGenericMethod(resultType);
            return generic.Invoke(this, new object?[] { task, targetMethod, attribute, sw })!;
        }

        return task;
    }

    private async Task InterceptAsyncInternal(Task task, MethodInfo targetMethod, LogIOAttribute attribute, Stopwatch? sw)
    {
        try
        {
            await task.ConfigureAwait(false);
            if (attribute.LogOutput)
            {
                LogCompleted(targetMethod, attribute, sw, result: null);
            }
        }
        catch (Exception ex)
        {
            LogException(targetMethod, attribute, sw, ex);
            throw;
        }
    }

    private async Task<TResult> InterceptAsyncInternalWithResult<TResult>(Task task, MethodInfo targetMethod, LogIOAttribute attribute, Stopwatch? sw)
    {
        try
        {
            var resultTask = (Task<TResult>)task;
            var result = await resultTask.ConfigureAwait(false);
            if (attribute.LogOutput)
            {
                LogCompleted(targetMethod, attribute, sw, result);
            }

            return result;
        }
        catch (Exception ex)
        {
            LogException(targetMethod, attribute, sw, ex);
            throw;
        }
    }

    private void LogCompleted(MethodInfo targetMethod, LogIOAttribute attribute, Stopwatch? sw, object? result)
    {
        if (!_logger!.IsEnabled(attribute.Level))
        {
            return;
        }

        if (attribute.LogDuration && sw != null)
        {
            _logger!.Log(attribute.Level,
                "Call {Service}.{Method} completed in {ElapsedMs}ms. Result: {Result}",
                _serviceName,
                targetMethod.Name,
                sw.ElapsedMilliseconds,
                SerializeValue(result));
            return;
        }

        _logger!.Log(attribute.Level,
            "Call {Service}.{Method} completed. Result: {Result}",
            _serviceName,
            targetMethod.Name,
            SerializeValue(result));
    }

    private void LogException(MethodInfo targetMethod, LogIOAttribute attribute, Stopwatch? sw, Exception exception)
    {
        if (!attribute.LogExceptions || !_logger!.IsEnabled(LogLevel.Error))
        {
            return;
        }

        if (attribute.LogDuration && sw != null)
        {
            _logger!.LogError(exception,
                "Call {Service}.{Method} failed after {ElapsedMs}ms.",
                _serviceName,
                targetMethod.Name,
                sw.ElapsedMilliseconds);
            return;
        }

        _logger!.LogError(exception,
            "Call {Service}.{Method} failed.",
            _serviceName,
            targetMethod.Name);
    }

    private static string SerializeArguments(MethodInfo method, object?[]? args)
    {
        if (args == null || args.Length == 0)
        {
            return "{}";
        }

        var parameters = method.GetParameters();
        var payload = new Dictionary<string, object?>(args.Length);
        for (var i = 0; i < args.Length; i++)
        {
            var value = args[i];
            if (value is CancellationToken || value is ILogger || value is ILoggerFactory)
            {
                continue;
            }

            var name = parameters.Length > i ? parameters[i].Name : $"arg{i}";
            payload[name ?? $"arg{i}"] = value;
        }

        return SerializeValue(payload);
    }

    private static string SerializeValue(object? value)
    {
        if (value == null)
        {
            return "null";
        }

        try
        {
            return JsonSerializer.Serialize(value, SerializerOptions);
        }
        catch (Exception ex)
        {
            return $"\"<unserializable:{ex.GetType().Name}>\"";
        }
    }

    private LogIOAttribute? ResolveAttribute(MethodInfo interfaceMethod)
    {
        var attr = interfaceMethod.GetCustomAttribute<LogIOAttribute>(inherit: true)
                   ?? interfaceMethod.DeclaringType?.GetCustomAttribute<LogIOAttribute>(inherit: true);

        if (attr != null)
        {
            return attr;
        }

        if (interfaceMethod.DeclaringType == null)
        {
            return _decorated?.GetType().GetCustomAttribute<LogIOAttribute>(inherit: true);
        }

        var interfaceType = interfaceMethod.DeclaringType;
        if (!interfaceType.IsInterface)
        {
            return _decorated?.GetType().GetCustomAttribute<LogIOAttribute>(inherit: true);
        }

        var map = _decorated!.GetType().GetInterfaceMap(interfaceType);
        var index = Array.IndexOf(map.InterfaceMethods, interfaceMethod);
        if (index < 0)
        {
            return _decorated.GetType().GetCustomAttribute<LogIOAttribute>(inherit: true);
        }

        var implementationMethod = map.TargetMethods[index];
        return implementationMethod.GetCustomAttribute<LogIOAttribute>(inherit: true)
            ?? _decorated.GetType().GetCustomAttribute<LogIOAttribute>(inherit: true);
    }

    private LogIOAttribute? GetEffectiveAttribute(MethodInfo targetMethod)
    {
        return _attributeCache.GetOrAdd(targetMethod, method =>
        {
            var attribute = ResolveAttribute(method);
            if (attribute == null)
            {
                if (!_logWhenNoAttribute)
                {
                    return null;
                }

                return CreateDefaultAttribute();
            }

            return _applyDefaultsToAttributes ? ApplyDefaults(attribute) : attribute;
        });
    }

    private LogIOAttribute CreateDefaultAttribute()
    {
        var defaults = _defaults ?? new LogIODefaults();
        return new LogIOAttribute
        {
            Level = defaults.Level,
            LogInput = defaults.LogInput,
            LogOutput = defaults.LogOutput,
            LogDuration = defaults.LogDuration,
            LogExceptions = defaults.LogExceptions
        };
    }

    private LogIOAttribute ApplyDefaults(LogIOAttribute attribute)
    {
        var defaults = _defaults ?? new LogIODefaults();
        return new LogIOAttribute
        {
            Level = attribute.Level == AttributeDefaults.Level ? defaults.Level : attribute.Level,
            LogInput = attribute.LogInput == AttributeDefaults.LogInput ? defaults.LogInput : attribute.LogInput,
            LogOutput = attribute.LogOutput == AttributeDefaults.LogOutput ? defaults.LogOutput : attribute.LogOutput,
            LogDuration = attribute.LogDuration == AttributeDefaults.LogDuration ? defaults.LogDuration : attribute.LogDuration,
            LogExceptions = attribute.LogExceptions == AttributeDefaults.LogExceptions ? defaults.LogExceptions : attribute.LogExceptions
        };
    }
}
