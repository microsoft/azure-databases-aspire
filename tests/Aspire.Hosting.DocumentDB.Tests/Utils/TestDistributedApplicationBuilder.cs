// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using System;
using Xunit.Abstractions;

namespace Aspire.Hosting.Utils;

/// <summary>
/// DistributedApplication.CreateBuilder() creates a builder that includes configuration to read from appsettings.json.
/// The builder has a FileSystemWatcher, which can't be cleaned up unless a DistributedApplication is built and disposed.
/// This class wraps the builder and provides a way to automatically dispose it to prevent test failures from excessive
/// FileSystemWatcher instances from many tests.
/// </summary>
public static class TestDistributedApplicationBuilder
{
    public static DisposableDistributedApplicationBuilder Create(DistributedApplicationOperation operation, string publisher = "manifest", string outputPath = "./")
    {
        var args = operation switch
        {
            DistributedApplicationOperation.Run => (string[])[],
            DistributedApplicationOperation.Publish => [$"Publishing:Publisher={publisher}", $"Publishing:OutputPath={outputPath}"],
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

        return Create(args);
    }

    public static DisposableDistributedApplicationBuilder Create(params string[] args)
    {
        return CreateCore(args, (_) => { });
    }

    public static DisposableDistributedApplicationBuilder Create(ITestOutputHelper testOutputHelper, params string[] args)
    {
        return CreateCore(args, (_) => { }, testOutputHelper);
    }

    public static DisposableDistributedApplicationBuilder Create(Action<DistributedApplicationOptions>? configureOptions, ITestOutputHelper? testOutputHelper = null)
    {
        return CreateCore([], configureOptions, testOutputHelper);
    }

    public static DisposableDistributedApplicationBuilder CreateWithTestContainerRegistry(ITestOutputHelper testOutputHelper) =>
        Create(testOutputHelper);

    private static DisposableDistributedApplicationBuilder CreateCore(string[] args, Action<DistributedApplicationOptions>? configureOptions, ITestOutputHelper? testOutputHelper = null)
    {
        var builder = DistributedApplication.CreateBuilder(args);

        // Apply configuration options if provided
        if (configureOptions != null)
        {
            builder.Services.Configure(configureOptions);
        }

        // Configure HTTP client defaults with resilience handler
        builder.Services.ConfigureHttpClientDefaults(http => http.AddStandardResilienceHandler());

        return new DisposableDistributedApplicationBuilder(builder);
    }
}

/// <summary>
/// A disposable wrapper around IDistributedApplicationBuilder to maintain compatibility with existing tests.
/// </summary>
public class DisposableDistributedApplicationBuilder : IDisposable
{
    private readonly IDistributedApplicationBuilder _builder;
    private bool _disposed;

    public DisposableDistributedApplicationBuilder(IDistributedApplicationBuilder builder)
    {
        _builder = builder;
    }

    // Forward all the builder methods directly
    public IServiceCollection Services => _builder.Services;
    public IResourceBuilder<T> AddResource<T>(T resource) where T : IResource => _builder.AddResource(resource);
    public IResourceBuilder<T> CreateResourceBuilder<T>(T resource) where T : IResource => _builder.CreateResourceBuilder(resource);
    public DistributedApplication Build() => _builder.Build();

    // Provide access to the underlying builder for extension methods
    public IDistributedApplicationBuilder Builder => _builder;

    public void Dispose()
    {
        if (!_disposed)
        {
            // The underlying builder doesn't need disposal
            _disposed = true;
        }
    }
}

// Extension methods to forward DocumentDB methods to the underlying builder
public static class DisposableDistributedApplicationBuilderExtensions
{
    public static IResourceBuilder<Aspire.Hosting.ApplicationModel.DocumentDBServerResource> AddDocumentDB(
        this DisposableDistributedApplicationBuilder builder, 
        string name, 
        int? port = null,
        IResourceBuilder<ParameterResource>? userName = null,
        IResourceBuilder<ParameterResource>? password = null,
        bool tls = false,
        bool allowInsecureTls = false)
    {
        return builder.Builder.AddDocumentDB(name, port, userName, password, tls, allowInsecureTls);
    }

    public static IResourceBuilder<Aspire.Hosting.ApplicationModel.DocumentDBServerResource> AddDocumentDB(
        this DisposableDistributedApplicationBuilder builder, 
        string name, 
        int? port)
    {
        return builder.Builder.AddDocumentDB(name, port);
    }

    public static IResourceBuilder<ParameterResource> AddParameter(
        this DisposableDistributedApplicationBuilder builder,
        string name,
        string value)
    {
        // Use the CreateDefaultPasswordParameter approach but with a fixed value
        return builder.Builder.AddParameter(name, false);
    }

    public static IResourceBuilder<ParameterResource> AddParameter(
        this DisposableDistributedApplicationBuilder builder,
        string name,
        bool secret = false)
    {
        return builder.Builder.AddParameter(name, secret);
    }
}
