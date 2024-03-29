﻿using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Brand.Template.Domain.Tempos.Abstractions;
using Brand.Template.Infra.Tempos;
using Brand.Template.Infra.Tempos.Repositories;
using Mapster;
using MapsterMapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Contrib.WaitAndRetry;
using Prometheus;
using Refit;

namespace Brand.Template.Infra;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        ConfigurationManager config
    ) =>
        services
            .AddSettings(
                config,
                out Settings.Api api,
                out Settings.Database db
            )
            .AddHealthChecking(db, api)
            .AddApiClients(api)
            .AddRepositories()
            .AddMappers();

    private static IServiceCollection AddSettings(
        this IServiceCollection services,
        ConfigurationManager config,
        out Settings.Api api,
        out Settings.Database db
    )
    {
        var keyVault = config
            .GetRequiredSection(nameof(Settings.KeyVault))
            .Get<Settings.KeyVault>()!;

        if (string.IsNullOrEmpty(keyVault.Url))
        {
            bool rodandoLocal = keyVault is not { ClientId: null, TenantId: null, ClientSecret: null };

            if (rodandoLocal)
                config.AddAzureKeyVault(
                    new SecretClient(
                        new Uri(keyVault.Url),
                        new ClientSecretCredential(
                            keyVault.TenantId,
                            keyVault.ClientId,
                            keyVault.ClientSecret
                        )
                    ),
                    new AzureKeyVaultConfigurationOptions()
                );
            else // se estiver rodando em cloud, usa managed identity
                config.AddAzureKeyVault(
                    new Uri(keyVault.Url),
                    new DefaultAzureCredential()
                );
        }

        services
            .Configure<Settings.Api>(config.GetSection(nameof(Settings.Api)))
            .Configure<Settings.Database>(config.GetSection(nameof(Settings.Database)));

        ServiceProvider provider = services.BuildServiceProvider();

        api = provider.GetRequiredService<IOptions<Settings.Api>>().Value;
        db = provider.GetRequiredService<IOptions<Settings.Database>>().Value;

        return services;
    }

    private static IServiceCollection AddHealthChecking(
        this IServiceCollection services,
        Settings.Database dbSettings,
        Settings.Api apiSettings
    )
    {
        services.AddHealthChecks()
            .AddSqlServer(
                name: "DB-Log",
                connectionString: dbSettings.Log.ConnectionString,
                failureStatus: HealthStatus.Degraded,
                tags: ["db", "mssql", "log"]
            )
            .AddElasticsearch(
                elasticsearchUri: apiSettings.Elasticsearch.Url,
                name: "Elasticsearch",
                failureStatus: HealthStatus.Degraded,
                tags: ["db", "elastic", "log"]
            )
            .ForwardToPrometheus();

        return services;
    }

    private static IServiceCollection AddApiClients(
        this IServiceCollection services,
        Settings.Api apis
    )
    {
        Options.Api openWeather = apis.OpenWeather;

        services
            .AddRefitClient<IOpenWeatherApiClient>()
            .ConfigureHttpClient(c =>
            {
                c.BaseAddress = new Uri(openWeather.Url);
                c.Timeout = TimeSpan.FromSeconds(openWeather.Timeout);
            })
            .AddTransientHttpErrorPolicy(policyBuilder =>
            {
                IEnumerable<TimeSpan> retryStrategy = Backoff.DecorrelatedJitterBackoffV2(
                    TimeSpan.FromSeconds(openWeather.Resilience.MedianFirstRetryDelay),
                    openWeather.Resilience.RetryCount
                );

                return policyBuilder.WaitAndRetryAsync(retryStrategy);
            });

        return services;
    }

    private static IServiceCollection AddRepositories(this IServiceCollection services) =>
        services
            .AddSingleton<ICidadeRepository, CidadeRepository>()
            .AddSingleton<ITempoRepository, TempoRepository>();

    private static IServiceCollection AddMappers(this IServiceCollection services) =>
        services
            .AddSingleton(TypeAdapterConfig.GlobalSettings)
            .AddSingleton<IMapper, ServiceMapper>();
}