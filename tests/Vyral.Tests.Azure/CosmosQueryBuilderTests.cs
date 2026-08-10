using System.Text.Json.Nodes;
using Vyral.Abstractions.Models;
using Vyral.Azure;

namespace Vyral.Tests.Azure;

public class CosmosQueryBuilderTests
{
    [Fact]
    public void BuildRecordQuery_TranslatesPortableFiltersAndOrdering()
    {
        var builder = new CosmosQueryBuilder();

        var plan = builder.BuildRecordQuery(new QueryEnvelope
        {
            PartitionKeys = new List<string> { "tenant-a", "tenant-b" },
            Filter = new FilterNode
            {
                Combine = "all",
                Children = new List<FilterNode>
                {
                    new() { Path = "/metadata/status", Op = "in", Value = new[] { "active", "preview" } },
                    new() { Path = "/metadata/score", Op = "gte", Value = 5 },
                    new() { Path = "/content/text", Op = "exists", Value = true }
                }
            },
            OrderBy = new List<OrderExpression> { new() { Path = "/id", Direction = "asc" } }
        });

        Assert.Equal(
            "SELECT * FROM c WHERE ARRAY_CONTAINS(@partitionKeys, c[\"partitionKey\"]) AND (ARRAY_CONTAINS(@p1, c[\"metadata\"][\"status\"]) AND c[\"metadata\"][\"score\"] >= @p2 AND IS_DEFINED(c[\"content\"][\"text\"])) ORDER BY c[\"id\"] ASC",
            plan.Sql);
        Assert.Equal(new[] { "tenant-a", "tenant-b" }, Assert.IsType<List<string>>(plan.Parameters["@partitionKeys"]));
        Assert.Equal(new[] { "active", "preview" }, Assert.IsAssignableFrom<IEnumerable<object?>>(plan.Parameters["@p1"]).Cast<string>());
        Assert.Equal(5, plan.Parameters["@p2"]);
    }

    [Fact]
    public void BuildVectorSearchQuery_UsesParameterizedTopAndVectorDistance()
    {
        var builder = new CosmosQueryBuilder();

        var plan = builder.BuildVectorSearchQuery(new QueryEnvelope
        {
            PartitionKeys = new List<string> { "tenant-a" },
            Filter = new FilterNode { Path = "/metadata/status", Op = "eq", Value = "active" },
            Vector = new VectorSearchOptions
            {
                Field = "contentEmbedding",
                Value = new float[] { 1, 0 },
                Top = 8
            }
        });

        Assert.Equal(
            "SELECT TOP @top c, VectorDistance(c[\"vectors\"][\"contentEmbedding\"][\"values\"], @vector) AS SimilarityScore FROM c WHERE c[\"partitionKey\"] = @partitionKey0 AND c[\"metadata\"][\"status\"] = @p3 ORDER BY VectorDistance(c[\"vectors\"][\"contentEmbedding\"][\"values\"], @vector)",
            plan.Sql);
        Assert.Equal(8, plan.Parameters["@top"]);
        Assert.Equal("tenant-a", plan.Parameters["@partitionKey0"]);
        Assert.Equal("active", plan.Parameters["@p3"]);
    }

    [Fact]
    public void BuildRecordQuery_DistinguishesNullAndMissing()
    {
        var builder = new CosmosQueryBuilder();

        var plan = builder.BuildRecordQuery(new QueryEnvelope
        {
            Filter = new FilterNode
            {
                Combine = "all",
                Children = new List<FilterNode>
                {
                    new() { Path = "/metadata/nullable", Op = "eq", Value = null },
                    new() { Path = "/metadata/status", Op = "neq", Value = null },
                    new() { Path = "/metadata/missing", Op = "exists", Value = false }
                }
            }
        });

        Assert.Equal(
            "SELECT * FROM c WHERE (IS_NULL(c[\"metadata\"][\"nullable\"]) AND (IS_DEFINED(c[\"metadata\"][\"status\"]) AND NOT IS_NULL(c[\"metadata\"][\"status\"])) AND NOT IS_DEFINED(c[\"metadata\"][\"missing\"]))",
            plan.Sql);
        Assert.Empty(plan.Parameters);
    }

    [Fact]
    public void BuildRecordQuery_RejectsNonScalarFilterValues()
    {
        var builder = new CosmosQueryBuilder();

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
                builder.BuildRecordQuery(new QueryEnvelope { Filter = filter }));
        }
    }

    [Fact]
    public void BuildRecordQuery_TranslatesStringPredicates()
    {
        var builder = new CosmosQueryBuilder();

        var plan = builder.BuildRecordQuery(new QueryEnvelope
        {
            Filter = new FilterNode
            {
                Combine = "all",
                Children = new List<FilterNode>
                {
                    new() { Path = "/content/text", Op = "contains", Value = "retrieval" },
                    new() { Path = "/metadata/category", Op = "startsWith", Value = "guide" }
                }
            }
        });

        Assert.Equal(
            "SELECT * FROM c WHERE (CONTAINS(c[\"content\"][\"text\"], @p0) AND STARTSWITH(c[\"metadata\"][\"category\"], @p1))",
            plan.Sql);
        Assert.Equal("retrieval", plan.Parameters["@p0"]);
        Assert.Equal("guide", plan.Parameters["@p1"]);
    }

    [Fact]
    public void BuildRecordQuery_RejectsUnsupportedSearchOptionsInsteadOfIgnoringThem()
    {
        var builder = new CosmosQueryBuilder();

        Assert.Throws<NotSupportedException>(() => builder.BuildRecordQuery(new QueryEnvelope
        {
            Vector = new VectorSearchOptions
            {
                Field = "contentEmbedding",
                Value = new float[] { 1, 0 },
                Top = 5
            }
        }));

        Assert.Throws<NotSupportedException>(() => builder.BuildRecordQuery(new QueryEnvelope
        {
            Lexical = new LexicalSearchOptions { Query = "retention" }
        }));
    }

    [Fact]
    public void BuildVectorSearchQuery_RejectsUnsupportedOptionsInsteadOfIgnoringThem()
    {
        var builder = new CosmosQueryBuilder();

        Assert.Throws<NotSupportedException>(() => builder.BuildVectorSearchQuery(new QueryEnvelope
        {
            Lexical = new LexicalSearchOptions { Query = "retention" },
            Vector = new VectorSearchOptions { Field = "contentEmbedding", Value = new float[] { 1, 0 }, Top = 5 }
        }));

        Assert.Throws<NotSupportedException>(() => builder.BuildVectorSearchQuery(new QueryEnvelope
        {
            OrderBy = new List<OrderExpression> { new() { Path = "/id", Direction = "asc" } },
            Vector = new VectorSearchOptions { Field = "contentEmbedding", Value = new float[] { 1, 0 }, Top = 5 }
        }));

        var supported = builder.BuildVectorSearchQuery(new QueryEnvelope
        {
            Vector = new VectorSearchOptions
            {
                Field = "contentEmbedding",
                Value = new float[] { 1, 0 },
                Top = 5,
                MinScore = 0.75f
            }
        });
        Assert.Contains("VectorDistance", supported.Sql, StringComparison.Ordinal);
    }
}
