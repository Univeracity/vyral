using System.Text.Json.Nodes;

namespace Vyral.Tests.Pgvector;

public class PgvectorQueryBuilderTests
{
    [Fact]
    public void BuildQuery_RejectsNonScalarFilterValues()
    {
        var builder = new PgvectorQueryBuilder();

        var invalidFilters = new List<FilterNode>
        {
            new() { Path = "/metadata/status", Op = "eq", Value = new JsonObject { ["nested"] = "active" } },
            new() { Path = "/metadata/status", Op = "eq", Value = new JsonArray("active") },
            new() { Path = "/metadata/status", Op = "in", Value = new JsonArray(new JsonObject { ["nested"] = "active" }) },
            new() { Path = "/metadata/status", Op = "exists", Value = new JsonObject { ["present"] = true } },
            new() { Path = "/content/text", Op = "contains", Value = 5 },
            new() { Path = "/metadata/status", Op = "starts_with", Value = "active" }
        };

        foreach (var filter in invalidFilters)
        {
            Assert.Throws<NotSupportedException>(() =>
                builder.BuildQuery("records", new QueryEnvelope { Filter = filter }));
        }
    }
}
