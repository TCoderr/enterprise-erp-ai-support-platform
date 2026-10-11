using Kantar.Application.Abstractions;
using Kantar.Application.Assistance;
using Kantar.Application.Common;
using Kantar.Application.Identity;
using Kantar.Application.Inventory;
using Kantar.Application.MasterData;
using Kantar.Application.Operations;
using Kantar.Application.Purchasing;
using Kantar.Application.Sales;
using Kantar.Application.Support;
using Kantar.Infrastructure.Assistance;
using Kantar.Infrastructure.Operations;
using Kantar.Infrastructure.Persistence;
using Kantar.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Kantar.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Registers persistence and application services for the web and worker processes (restricted login).</summary>
    public static IServiceCollection AddKantar(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("App")
            ?? throw new InvalidOperationException("ConnectionStrings:App is not configured.");

        services.AddDbContext<KantarDbContext>(options => options.UseSqlServer(connectionString, sql => sql.CommandTimeout(30)));
        services.AddScoped<IKantarDb>(sp => sp.GetRequiredService<KantarDbContext>());
        services.AddScoped<IUnitOfWork, SqlUnitOfWork>();
        services.AddScoped<IStockLedger, SqlStockLedger>();
        services.AddScoped<IDocumentLocks, SqlDocumentLocks>();
        services.AddScoped<IDocumentNumbers, SqlDocumentNumbers>();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IFailpoints, NoFailpoints>();

        var hashing = configuration.GetSection("Security:PasswordHashing").Get<PasswordHashingOptions>() ?? new PasswordHashingOptions();
        services.AddSingleton(hashing);
        services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
        services.AddSingleton<LoginDecoy>();
        services.AddSingleton(configuration.GetSection("Security:Sessions").Get<SecurityOptions>() ?? new SecurityOptions());

        services.AddScoped<CommandExecutor>();
        services.AddScoped<AuditWriter>();
        services.AddScoped<OutboxWriter>();
        services.AddScoped<AuthService>();
        services.AddScoped<AdminService>();
        services.AddScoped<MasterDataService>();
        services.AddScoped<StockPoster>();
        services.AddScoped<InventoryService>();
        services.AddScoped<PurchasingService>();
        services.AddScoped<SalesService>();
        services.AddScoped<KnowledgeService>();
        services.AddScoped<TicketService>();
        services.AddScoped<AssistService>();
        services.AddScoped<OperationsService>();
        services.AddScoped<OutboxDispatcher>();
        services.AddSingleton<OpsMetrics>();
        services.AddSingleton<AssistMetrics>();
        services.AddSingleton(configuration.GetSection("Worker").Get<WorkerOptions>() ?? new WorkerOptions());
        AddAnswerGenerator(services, configuration);
        return services;
    }

    /// <summary>
    /// The provider is chosen by server configuration only. An invalid local endpoint (not loopback and
    /// not explicitly allowed) stops the process at start-up instead of failing on the first question.
    /// </summary>
    private static void AddAnswerGenerator(IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection("Ai").Get<AiOptions>() ?? new AiOptions();
        services.AddSingleton(options);
        switch (options.Provider)
        {
            case "Demo":
                services.TryAddSingleton<IAnswerGenerator, DemoAnswerGenerator>();
                break;
            case "LocalModel":
                LocalEndpointPolicy.Validate(options.LocalModel.BaseUrl, options.LocalModel.AllowedPrivateHosts);
                services.TryAddSingleton<IAnswerGenerator>(_ => new LocalModelAnswerGenerator(options));
                break;
            case "Disabled":
                services.TryAddSingleton<IAnswerGenerator, DisabledAnswerGenerator>();
                break;
            default:
                throw new InvalidOperationException("Ai:Provider must be Demo, LocalModel or Disabled.");
        }
    }

    /// <summary>Background loops of the worker process.</summary>
    public static IServiceCollection AddKantarWorkers(this IServiceCollection services)
    {
        services.AddHostedService<OutboxWorker>();
        services.AddHostedService<MaintenanceWorker>();
        return services;
    }
}

/// <summary>Used only by `dotnet ef` to build the model; it never opens this connection.</summary>
internal sealed class DesignTimeFactory : IDesignTimeDbContextFactory<KantarDbContext>
{
    public KantarDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<KantarDbContext>()
            .UseSqlServer("Server=design-time-only;Database=kantar;Integrated Security=true;TrustServerCertificate=true")
            .Options);
}
