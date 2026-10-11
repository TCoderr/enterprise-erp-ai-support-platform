using System.Reflection;
using Kantar.Application.Common;
using Kantar.Domain.Common;
using Kantar.Infrastructure.Persistence;
using Xunit;

namespace Kantar.UnitTests;

public sealed class ArchitectureTests
{
    private static readonly Assembly Domain = typeof(DomainException).Assembly;
    private static readonly Assembly Application = typeof(CommandExecutor).Assembly;
    private static readonly Assembly Infrastructure = typeof(KantarDbContext).Assembly;

    private static string[] References(Assembly assembly) =>
        assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();

    [Fact]
    public void Domain_depends_on_nothing_but_the_base_class_library()
    {
        var references = References(Domain);

        Assert.DoesNotContain(references, r => r.StartsWith("Kantar.", StringComparison.Ordinal));
        Assert.DoesNotContain(references, r => r.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
        Assert.DoesNotContain(references, r => r.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
    }

    [Fact]
    public void Application_depends_on_domain_and_provider_neutral_persistence_only()
    {
        var references = References(Application);

        Assert.Contains("Kantar.Domain", references);
        Assert.DoesNotContain("Kantar.Infrastructure", references);
        Assert.DoesNotContain("Kantar.Api", references);
        Assert.DoesNotContain(references, r => r.Contains("SqlServer", StringComparison.Ordinal) || r.Contains("SqlClient", StringComparison.Ordinal));
        Assert.DoesNotContain(references, r => r.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
    }

    [Fact]
    public void Infrastructure_does_not_depend_on_the_api_host()
    {
        Assert.DoesNotContain("Kantar.Api", References(Infrastructure));
    }
}
