using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;
using Vyral.Embeddings.Onnx;
using Vyral.Local;

namespace Vyral.Cli;

public class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("Vyral CLI - Viability & Resilience Abstraction Layer");

        var dbOption = new Option<string>(
            name: "--db",
            description: "Path to the SQLite database file.",
            getDefaultValue: () => "vyral.sqlite");
        var embeddingProviderOption = new Option<string>(
            name: "--embedding-provider",
            description: "Embedding provider id.",
            getDefaultValue: () => LocalTokenHashEmbeddingProviderFactory.Provider);
        var embeddingModelOption = new Option<string?>(
            name: "--embedding-model",
            description: "Embedding model id override.");
        var embeddingModelPathOption = new Option<string?>(
            name: "--embedding-model-path",
            description: "Path to an ONNX model file or model directory.");
        var embeddingVocabPathOption = new Option<string?>(
            name: "--embedding-vocab-path",
            description: "Path to a WordPiece vocab.txt file.");
        var embeddingExecutionProviderOption = new Option<string?>(
            name: "--embedding-execution-provider",
            description: "ONNX execution provider: cpu, cudaPreferred, or cudaRequired.");
        var embeddingIntraOpThreadsOption = new Option<int?>(
            name: "--embedding-intra-op-threads",
            description: "ONNX intra-op thread cap.");
        var embeddingInterOpThreadsOption = new Option<int?>(
            name: "--embedding-inter-op-threads",
            description: "ONNX inter-op thread cap.");
        var embeddingCudaMemoryLimitMbOption = new Option<long?>(
            name: "--embedding-cuda-memory-limit-mb",
            description: "ONNX CUDA GPU memory cap in MB.");

        var initCommand = new Command("init", "Initialize the Vyral database");
        initCommand.AddOption(dbOption);
        initCommand.SetHandler(async (dbPath) =>
        {
            Console.WriteLine($"Initializing database at {dbPath}...");
            var store = new SqliteRecordCollectionStore(dbPath);
            await store.InitializeAsync();
            var canonicalStore = new SqliteCanonicalStore(ResolveCanonicalDatabasePath(dbPath, null));
            await canonicalStore.ListMigrationsAsync();
            Console.WriteLine("Record and CanonicalStore databases initialized successfully.");
        }, dbOption);

        var collectionOption = new Option<string>("--collection", "Collection name") { IsRequired = true };

        var createCollectionCommand = new Command("create-collection", "Create a record collection with a vector policy");
        createCollectionCommand.AddOption(dbOption);
        var dimsOption = new Option<int>("--dims", "Vector dimensions") { IsRequired = true };
        var vectorNameOption = new Option<string>(
            name: "--vector-name",
            description: "Vector field name.",
            getDefaultValue: () => "contentEmbedding");
        var datatypeOption = new Option<string>(
            name: "--datatype",
            description: "Vector datatype.",
            getDefaultValue: () => "float32");
        var distanceOption = new Option<string>(
            name: "--distance",
            description: "Vector distance function.",
            getDefaultValue: () => "cosine");
        var indexTypeOption = new Option<string>(
            name: "--index-type",
            description: "Vector index type.",
            getDefaultValue: () => "flat");
        var indexedMetadataOption = new Option<string>(
            name: "--indexed-metadata",
            description: "Comma-separated JSON pointer paths to project into the local metadata index.",
            getDefaultValue: () => string.Empty);
        createCollectionCommand.AddOption(collectionOption);
        createCollectionCommand.AddOption(dimsOption);
        createCollectionCommand.AddOption(vectorNameOption);
        createCollectionCommand.AddOption(datatypeOption);
        createCollectionCommand.AddOption(distanceOption);
        createCollectionCommand.AddOption(indexTypeOption);
        createCollectionCommand.AddOption(indexedMetadataOption);

        createCollectionCommand.SetHandler(async (dbPath, collection, dims, vectorName, datatype, distance, indexType, indexedMetadata) =>
        {
            var store = new SqliteRecordCollectionStore(dbPath);
            await store.InitializeAsync();
            var policy = new RecordCollectionPolicy
            {
                Name = collection,
                IndexedMetadata = ParseCommaSeparated(indexedMetadata),
                VectorPolicies = new List<VectorFieldPolicy>
                {
                    new VectorFieldPolicy
                    {
                        Name = vectorName,
                        Dimensions = dims,
                        Path = $"/vectors/{vectorName}/values",
                        Datatype = datatype,
                        DistanceFunction = distance,
                        IndexType = indexType
                    }
                }
            };
            await store.CreateCollectionAsync(policy);
            Console.WriteLine($"Collection '{collection}' created with vector field '{vectorName}' and {dims} dimensions.");
        }, dbOption, collectionOption, dimsOption, vectorNameOption, datatypeOption, distanceOption, indexTypeOption, indexedMetadataOption);

        var deleteCollectionCommand = new Command("delete-collection", "Delete a record collection and all records in it");
        deleteCollectionCommand.AddOption(dbOption);
        deleteCollectionCommand.AddOption(collectionOption);
        deleteCollectionCommand.SetHandler(async (dbPath, collection) =>
        {
            var store = new SqliteRecordCollectionStore(dbPath);
            await store.InitializeAsync();
            await store.DeleteCollectionAsync(collection);
            Console.WriteLine($"Collection '{collection}' deleted.");
        }, dbOption, collectionOption);

        var upsertCommand = new Command("upsert", "Upsert a record into a collection");
        upsertCommand.AddOption(dbOption);
        var idOption = new Option<string>("--id", "Record ID") { IsRequired = true };
        var pkOption = new Option<string>("--pk", "Partition Key") { IsRequired = true };
        var contentOption = new Option<string>("--content", "JSON content") { IsRequired = true };

        upsertCommand.AddOption(collectionOption);
        upsertCommand.AddOption(idOption);
        upsertCommand.AddOption(pkOption);
        upsertCommand.AddOption(contentOption);
        upsertCommand.AddOption(embeddingProviderOption);
        upsertCommand.AddOption(embeddingModelOption);
        upsertCommand.AddOption(embeddingModelPathOption);
        upsertCommand.AddOption(embeddingVocabPathOption);
        upsertCommand.AddOption(embeddingExecutionProviderOption);
        upsertCommand.AddOption(embeddingIntraOpThreadsOption);
        upsertCommand.AddOption(embeddingInterOpThreadsOption);
        upsertCommand.AddOption(embeddingCudaMemoryLimitMbOption);

        upsertCommand.SetHandler(async (InvocationContext context) =>
        {
            var dbPath = context.ParseResult.GetValueForOption(dbOption)!;
            var collection = context.ParseResult.GetValueForOption(collectionOption)!;
            var id = context.ParseResult.GetValueForOption(idOption)!;
            var pk = context.ParseResult.GetValueForOption(pkOption)!;
            var content = context.ParseResult.GetValueForOption(contentOption)!;
            var embeddingProviderId = context.ParseResult.GetValueForOption(embeddingProviderOption)!;
            var embeddingModelId = context.ParseResult.GetValueForOption(embeddingModelOption);
            var embeddingModelPath = context.ParseResult.GetValueForOption(embeddingModelPathOption);
            var embeddingVocabPath = context.ParseResult.GetValueForOption(embeddingVocabPathOption);
            var embeddingExecutionProvider = context.ParseResult.GetValueForOption(embeddingExecutionProviderOption);
            var embeddingIntraOpThreads = context.ParseResult.GetValueForOption(embeddingIntraOpThreadsOption);
            var embeddingInterOpThreads = context.ParseResult.GetValueForOption(embeddingInterOpThreadsOption);
            var embeddingCudaMemoryLimitMb = context.ParseResult.GetValueForOption(embeddingCudaMemoryLimitMbOption);

            var store = new SqliteRecordCollectionStore(dbPath);
            await store.InitializeAsync();
            var policy = await store.GetCollectionPolicyAsync(collection);
            if (policy == null)
            {
                Console.WriteLine($"Error: Collection '{collection}' not found. Use 'create-collection' first.");
                return;
            }

            var contentDict = JsonSerializer.Deserialize<System.Text.Json.Nodes.JsonObject>(content) ?? new();
            var record = new VyralRecord
            {
                Id = id,
                PartitionKey = pk,
                Type = "cli-manual",
                Content = contentDict
            };

            // Automatically generate embedding for the first vector policy if possible
            if (policy.VectorPolicies.Any() && contentDict["text"] is System.Text.Json.Nodes.JsonValue textNode)
            {
                var text = textNode.TryGetValue<string>(out var s) ? s : textNode.ToJsonString();
                var provider = CreateEmbeddingProvider(embeddingProviderId, embeddingModelId, embeddingModelPath, embeddingVocabPath, embeddingExecutionProvider, embeddingIntraOpThreads, embeddingInterOpThreads, embeddingCudaMemoryLimitMb, policy.VectorPolicies[0].Dimensions);
                var vectorValues = await provider.GenerateEmbeddingAsync(text);

                record.Vectors = new Dictionary<string, VyralVector>
                {
                    [policy.VectorPolicies[0].Name] = new VyralVector
                    {
                        Values = vectorValues,
                        Dimensions = policy.VectorPolicies[0].Dimensions,
                        Model = provider.ModelId,
                        Datatype = policy.VectorPolicies[0].Datatype,
                        DistanceFunction = policy.VectorPolicies[0].DistanceFunction
                    }
                };
            }

            await store.UpsertRecordAsync(collection, record);
            Console.WriteLine($"Record {id} upserted into {collection}.");
        });

        var getCommand = new Command("get", "Get a record from a collection");
        getCommand.AddOption(dbOption);
        getCommand.AddOption(collectionOption);
        getCommand.AddOption(idOption);
        getCommand.AddOption(pkOption);

        getCommand.SetHandler(async (dbPath, collection, id, pk) =>
        {
            var store = new SqliteRecordCollectionStore(dbPath);
            await store.InitializeAsync();
            var record = await store.GetRecordAsync(collection, pk, id);
            if (record != null)
            {
                Console.WriteLine(JsonSerializer.Serialize(record, JsonOptions));
            }
            else
            {
                Console.WriteLine("Record not found.");
            }
        }, dbOption, collectionOption, idOption, pkOption);

        var searchCommand = new Command("search", "Search for records in a collection");
        searchCommand.AddOption(dbOption);
        searchCommand.AddOption(collectionOption);
        var queryOption = new Option<string>("--query", "Query text to embed and search") { IsRequired = true };
        var minScoreOption = new Option<float?>("--min-score", "Minimum retrieval score to return.");
        searchCommand.AddOption(queryOption);
        searchCommand.AddOption(minScoreOption);
        searchCommand.AddOption(embeddingProviderOption);
        searchCommand.AddOption(embeddingModelOption);
        searchCommand.AddOption(embeddingModelPathOption);
        searchCommand.AddOption(embeddingVocabPathOption);
        searchCommand.AddOption(embeddingExecutionProviderOption);
        searchCommand.AddOption(embeddingIntraOpThreadsOption);
        searchCommand.AddOption(embeddingInterOpThreadsOption);
        searchCommand.AddOption(embeddingCudaMemoryLimitMbOption);

        searchCommand.SetHandler(async (InvocationContext context) =>
        {
            var dbPath = context.ParseResult.GetValueForOption(dbOption)!;
            var collection = context.ParseResult.GetValueForOption(collectionOption)!;
            var queryText = context.ParseResult.GetValueForOption(queryOption)!;
            var minScore = context.ParseResult.GetValueForOption(minScoreOption);
            var embeddingProviderId = context.ParseResult.GetValueForOption(embeddingProviderOption)!;
            var embeddingModelId = context.ParseResult.GetValueForOption(embeddingModelOption);
            var embeddingModelPath = context.ParseResult.GetValueForOption(embeddingModelPathOption);
            var embeddingVocabPath = context.ParseResult.GetValueForOption(embeddingVocabPathOption);
            var embeddingExecutionProvider = context.ParseResult.GetValueForOption(embeddingExecutionProviderOption);
            var embeddingIntraOpThreads = context.ParseResult.GetValueForOption(embeddingIntraOpThreadsOption);
            var embeddingInterOpThreads = context.ParseResult.GetValueForOption(embeddingInterOpThreadsOption);
            var embeddingCudaMemoryLimitMb = context.ParseResult.GetValueForOption(embeddingCudaMemoryLimitMbOption);

            var store = new SqliteRecordCollectionStore(dbPath);
            await store.InitializeAsync();
            var policy = await store.GetCollectionPolicyAsync(collection);
            if (policy?.VectorPolicies.Any() != true)
            {
                Console.WriteLine($"Error: Collection '{collection}' does not have a vector policy.");
                return;
            }

            var embeddingProvider = CreateEmbeddingProvider(embeddingProviderId, embeddingModelId, embeddingModelPath, embeddingVocabPath, embeddingExecutionProvider, embeddingIntraOpThreads, embeddingInterOpThreads, embeddingCudaMemoryLimitMb, policy.VectorPolicies[0].Dimensions);
            var retrievalService = new LocalRetrievalService(store, embeddingProvider);

            var request = new RetrievalRequest
            {
                Collections = new List<string> { collection },
                Query = queryText,
                Embedding = new EmbeddingOptions { Field = policy.VectorPolicies[0].Name },
                Limit = 5,
                MinScore = minScore,
                IncludeTrace = true
            };

            var response = await retrievalService.SearchAsync(request);
            Console.WriteLine(JsonSerializer.Serialize(response, JsonOptions));
        });

        var envelopeOption = new Option<string>(
            name: "--envelope",
            description: "JSON QueryEnvelope payload.",
            getDefaultValue: () => "{}");
        var envelopeFileOption = new Option<string?>(
            name: "--envelope-file",
            description: "Path to a JSON QueryEnvelope file.");

        var queryRecordsCommand = new Command("query", "Query records with a JSON QueryEnvelope");
        queryRecordsCommand.AddOption(dbOption);
        queryRecordsCommand.AddOption(collectionOption);
        queryRecordsCommand.AddOption(envelopeOption);
        queryRecordsCommand.AddOption(envelopeFileOption);
        queryRecordsCommand.SetHandler(async (dbPath, collection, envelopeJson, envelopeFile) =>
        {
            var store = new SqliteRecordCollectionStore(dbPath);
            await store.InitializeAsync();
            var envelope = await ReadQueryEnvelopeAsync(envelopeJson, envelopeFile);
            var result = await store.QueryRecordsPageAsync(collection, envelope);
            Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
        }, dbOption, collectionOption, envelopeOption, envelopeFileOption);

        var searchRecordsCommand = new Command("search-records", "Vector-search records with a JSON QueryEnvelope");
        searchRecordsCommand.AddOption(dbOption);
        searchRecordsCommand.AddOption(collectionOption);
        searchRecordsCommand.AddOption(envelopeOption);
        searchRecordsCommand.AddOption(envelopeFileOption);
        searchRecordsCommand.SetHandler(async (dbPath, collection, envelopeJson, envelopeFile) =>
        {
            var store = new SqliteRecordCollectionStore(dbPath);
            await store.InitializeAsync();
            var envelope = await ReadQueryEnvelopeAsync(envelopeJson, envelopeFile);
            var result = await store.SearchRecordsPageAsync(collection, envelope);
            Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
        }, dbOption, collectionOption, envelopeOption, envelopeFileOption);

        var exportCommand = new Command("export", "Export a collection to a JSON file");
        exportCommand.AddOption(dbOption);
        exportCommand.AddOption(collectionOption);
        var fileOption = new Option<string>("--file", "Output file path") { IsRequired = true };
        var exportMaxRecordsOption = new Option<int?>("--max-records", "Maximum records to include in the export snapshot.");
        var allowPartialExportOption = new Option<bool>("--allow-partial", "Allow writing a truncated export when max-records is exceeded.");
        exportCommand.AddOption(fileOption);
        exportCommand.AddOption(exportMaxRecordsOption);
        exportCommand.AddOption(allowPartialExportOption);

        exportCommand.SetHandler(async (dbPath, collection, filePath, maxRecords, allowPartial) =>
        {
            var store = new SqliteRecordCollectionStore(dbPath);
            await store.InitializeAsync();
            var exportData = await store.ExportCollectionAsync(collection, new CollectionExportRequest
            {
                MaxRecords = maxRecords,
                FailOnLimitExceeded = !allowPartial
            });
            if (exportData == null)
            {
                Console.WriteLine($"Error: Collection '{collection}' not found.");
                return;
            }

            await File.WriteAllTextAsync(filePath, JsonSerializer.Serialize(exportData, JsonOptions));
            var truncated = exportData.Truncated ? " truncated" : string.Empty;
            Console.WriteLine($"Collection '{collection}' exported{truncated} to {filePath}. Records: {exportData.RecordCount}. Content hash: {exportData.ContentHash}.");
        }, dbOption, collectionOption, fileOption, exportMaxRecordsOption, allowPartialExportOption);

        var importCommand = new Command("import", "Import a collection from a JSON file");
        importCommand.AddOption(dbOption);
        importCommand.AddOption(fileOption);
        var importCollectionOption = new Option<string?>("--collection", "Optional target collection name. Defaults to the snapshot collection.");
        var replaceExistingOption = new Option<bool>("--replace-existing", "Delete and recreate the target collection before importing.");
        var continueOnErrorOption = new Option<bool>("--continue-on-error", "Continue importing records after per-record failures.");
        var allowCollectionRenameOption = new Option<bool>("--allow-collection-rename", "Allow importing a snapshot into a differently named target collection.");
        var allowPartialSnapshotOption = new Option<bool>("--allow-partial", "Allow importing a snapshot marked as truncated.");
        importCommand.AddOption(importCollectionOption);
        importCommand.AddOption(replaceExistingOption);
        importCommand.AddOption(continueOnErrorOption);
        importCommand.AddOption(allowCollectionRenameOption);
        importCommand.AddOption(allowPartialSnapshotOption);

        importCommand.SetHandler(async (dbPath, filePath, targetCollection, replaceExisting, continueOnError, allowCollectionRename, allowPartialSnapshot) =>
        {
            var store = new SqliteRecordCollectionStore(dbPath);
            await store.InitializeAsync();

            var json = await File.ReadAllTextAsync(filePath);
            var exportData = JsonSerializer.Deserialize<CollectionExportEnvelope>(json, JsonOptions)
                ?? throw new InvalidOperationException("Import file did not contain a collection export envelope.");
            var collection = string.IsNullOrWhiteSpace(targetCollection) ? exportData.Collection : targetCollection.Trim();
            var result = await store.ImportCollectionAsync(collection, new CollectionImportRequest
            {
                Snapshot = exportData,
                ReplaceExisting = replaceExisting,
                ContinueOnError = continueOnError,
                AllowCollectionRename = allowCollectionRename || !string.IsNullOrWhiteSpace(targetCollection),
                AllowPartialSnapshot = allowPartialSnapshot
            });

            Console.WriteLine($"Imported {result.Records.Succeeded} of {result.Records.Requested} records into collection '{result.Collection}'. Policy: {result.PolicyStatus}. Content hash: {result.ContentHash}.");
        }, dbOption, fileOption, importCollectionOption, replaceExistingOption, continueOnErrorOption, allowCollectionRenameOption, allowPartialSnapshotOption);

        var canonicalDatabaseOption = new Option<string?>("--canonical-db", "CanonicalStore SQLite database path. Defaults to a sibling <db>.canonical.sqlite file.");
        var canonicalTenantOption = new Option<string>("--tenant", "Canonical tenant id") { IsRequired = true };
        var canonicalFileOption = new Option<string>("--file", "Canonical snapshot or migration JSON file") { IsRequired = true };
        var canonicalArchiveDirectoryOption = new Option<string>("--directory", "Directory containing a canonical archive manifest.json and chunk files") { IsRequired = true };
        var canonicalArchiveChunkBytesOption = new Option<int>("--chunk-bytes", () => CanonicalTenantArchive.DefaultChunkBytes, "Archive chunk size in bytes (1 through 16777216).");

        var exportCanonicalCommand = new Command("canonical-export", "Export a complete CanonicalStore tenant snapshot to JSON");
        exportCanonicalCommand.AddOption(dbOption);
        exportCanonicalCommand.AddOption(canonicalDatabaseOption);
        exportCanonicalCommand.AddOption(canonicalTenantOption);
        exportCanonicalCommand.AddOption(canonicalFileOption);
        exportCanonicalCommand.SetHandler(async (dbPath, canonicalDbPath, tenantId, filePath) =>
        {
            var store = new SqliteCanonicalStore(ResolveCanonicalDatabasePath(dbPath, canonicalDbPath));
            var snapshot = await store.ExportTenantAsync(tenantId);
            await File.WriteAllTextAsync(filePath, JsonSerializer.Serialize(snapshot, JsonOptions));
            Console.WriteLine($"Canonical tenant '{snapshot.TenantId}' exported to {filePath}. Documents: {snapshot.Documents.Count}. Content hash: {snapshot.ContentHash}.");
        }, dbOption, canonicalDatabaseOption, canonicalTenantOption, canonicalFileOption);

        var restoreCanonicalCommand = new Command("canonical-restore", "Atomically restore a CanonicalStore tenant snapshot from JSON");
        restoreCanonicalCommand.AddOption(dbOption);
        restoreCanonicalCommand.AddOption(canonicalDatabaseOption);
        restoreCanonicalCommand.AddOption(canonicalTenantOption);
        restoreCanonicalCommand.AddOption(canonicalFileOption);
        var canonicalExpectedHashOption = new Option<string?>("--expected-content-hash", "Optional expected snapshot content hash. Defaults to the embedded snapshot hash.");
        restoreCanonicalCommand.AddOption(canonicalExpectedHashOption);
        restoreCanonicalCommand.SetHandler(async (dbPath, canonicalDbPath, tenantId, filePath, expectedContentHash) =>
        {
            var json = await File.ReadAllTextAsync(filePath);
            var snapshot = JsonSerializer.Deserialize<CanonicalTenantSnapshot>(json, JsonOptions)
                ?? throw new InvalidOperationException("Canonical restore file did not contain a tenant snapshot.");
            if (!string.Equals(tenantId, snapshot.TenantId, StringComparison.Ordinal))
                throw new InvalidOperationException("Canonical restore tenant must match the snapshot tenant.");
            var store = new SqliteCanonicalStore(ResolveCanonicalDatabasePath(dbPath, canonicalDbPath));
            await store.RestoreTenantAsync(new CanonicalRestoreRequest
            {
                Snapshot = snapshot,
                ExpectedContentHash = expectedContentHash
            });
            Console.WriteLine($"Canonical tenant '{tenantId}' restored from {filePath}. Content hash: {snapshot.ContentHash}.");
        }, dbOption, canonicalDatabaseOption, canonicalTenantOption, canonicalFileOption, canonicalExpectedHashOption);

        var exportCanonicalArchiveCommand = new Command("canonical-archive-export", "Export a large CanonicalStore tenant archive as manifest plus binary chunks");
        exportCanonicalArchiveCommand.AddOption(dbOption);
        exportCanonicalArchiveCommand.AddOption(canonicalDatabaseOption);
        exportCanonicalArchiveCommand.AddOption(canonicalTenantOption);
        exportCanonicalArchiveCommand.AddOption(canonicalArchiveDirectoryOption);
        exportCanonicalArchiveCommand.AddOption(canonicalArchiveChunkBytesOption);
        exportCanonicalArchiveCommand.SetHandler(async (dbPath, canonicalDbPath, tenantId, directory, chunkBytes) =>
        {
            var store = new SqliteCanonicalStore(ResolveCanonicalDatabasePath(dbPath, canonicalDbPath));
            var archive = await store.ExportTenantArchiveAsync(tenantId, chunkBytes);
            Directory.CreateDirectory(directory);
            foreach (var chunk in archive.Chunks)
            {
                await File.WriteAllBytesAsync(Path.Combine(directory, $"chunk-{chunk.Index:D6}.bin"), chunk.Content);
                chunk.Content = Array.Empty<byte>();
            }
            await File.WriteAllTextAsync(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(archive, JsonOptions));
            Console.WriteLine($"Canonical tenant '{archive.TenantId}' exported to {directory}. Chunks: {archive.Chunks.Count}. Content hash: {archive.ContentHash}.");
        }, dbOption, canonicalDatabaseOption, canonicalTenantOption, canonicalArchiveDirectoryOption, canonicalArchiveChunkBytesOption);

        var restoreCanonicalArchiveCommand = new Command("canonical-archive-restore", "Atomically restore a CanonicalStore tenant archive from manifest plus binary chunks");
        restoreCanonicalArchiveCommand.AddOption(dbOption);
        restoreCanonicalArchiveCommand.AddOption(canonicalDatabaseOption);
        restoreCanonicalArchiveCommand.AddOption(canonicalTenantOption);
        restoreCanonicalArchiveCommand.AddOption(canonicalArchiveDirectoryOption);
        restoreCanonicalArchiveCommand.AddOption(canonicalExpectedHashOption);
        restoreCanonicalArchiveCommand.SetHandler(async (dbPath, canonicalDbPath, tenantId, directory, expectedContentHash) =>
        {
            var archive = JsonSerializer.Deserialize<CanonicalTenantArchive>(await File.ReadAllTextAsync(Path.Combine(directory, "manifest.json")), JsonOptions)
                ?? throw new InvalidOperationException("Canonical archive manifest did not contain an archive.");
            if (!string.Equals(tenantId, archive.TenantId, StringComparison.Ordinal))
                throw new InvalidOperationException("Canonical archive restore tenant must match the archive tenant.");
            foreach (var chunk in archive.Chunks)
                chunk.Content = await File.ReadAllBytesAsync(Path.Combine(directory, $"chunk-{chunk.Index:D6}.bin"));
            var store = new SqliteCanonicalStore(ResolveCanonicalDatabasePath(dbPath, canonicalDbPath));
            await store.RestoreTenantArchiveAsync(new CanonicalArchiveRestoreRequest { Archive = archive, ExpectedContentHash = expectedContentHash ?? archive.ContentHash });
            Console.WriteLine($"Canonical tenant '{tenantId}' restored from {directory}. Content hash: {archive.ContentHash}.");
        }, dbOption, canonicalDatabaseOption, canonicalTenantOption, canonicalArchiveDirectoryOption, canonicalExpectedHashOption);

        var applyCanonicalMigrationsCommand = new Command("canonical-migrate", "Apply namespaced CanonicalStore migration id/checksum receipts from a JSON array");
        applyCanonicalMigrationsCommand.AddOption(dbOption);
        applyCanonicalMigrationsCommand.AddOption(canonicalDatabaseOption);
        applyCanonicalMigrationsCommand.AddOption(canonicalFileOption);
        applyCanonicalMigrationsCommand.SetHandler(async (dbPath, canonicalDbPath, filePath) =>
        {
            var json = await File.ReadAllTextAsync(filePath);
            var migrations = JsonSerializer.Deserialize<List<CanonicalMigration>>(json, JsonOptions)
                ?? throw new InvalidOperationException("Canonical migration file did not contain a JSON array.");
            var store = new SqliteCanonicalStore(ResolveCanonicalDatabasePath(dbPath, canonicalDbPath));
            await store.ApplyMigrationsAsync(migrations);
            Console.WriteLine($"Applied or verified {migrations.Count} CanonicalStore migration receipt(s).");
        }, dbOption, canonicalDatabaseOption, canonicalFileOption);

        var canonicalServerOption = new Option<string>("--server", "CanonicalStore server base URL") { IsRequired = true };
        var canonicalApiKeyOption = new Option<string?>("--api-key", "Optional Vyral API key; supplied only as the X-Vyral-Api-Key request header.");
        var canonicalIdentityTokenOption = new Option<string?>("--identity-token", "Optional workload identity token; supplied only as X-Serverless-Authorization.");
        var canonicalPreflightProbeOption = new Option<bool>("--probe", "Run the explicit reversible archive/restore and tenant-isolation data-plane probe.");
        var canonicalPreflightCommand = new Command("canonical-preflight", "Validate the deployed CanonicalStore boundary and print non-secret rollout evidence");
        canonicalPreflightCommand.AddOption(canonicalServerOption);
        canonicalPreflightCommand.AddOption(canonicalApiKeyOption);
        canonicalPreflightCommand.AddOption(canonicalIdentityTokenOption);
        canonicalPreflightCommand.AddOption(canonicalPreflightProbeOption);
        canonicalPreflightCommand.SetHandler(async (server, apiKey, identityToken, probe) =>
        {
            if (!Uri.TryCreate(server, UriKind.Absolute, out var baseUri)) throw new InvalidOperationException("--server must be an absolute URL.");
            var endpoint = new Uri(baseUri.ToString().TrimEnd('/') + (probe ? "/canonical/preflight/probe" : "/canonical/preflight"));
            using var client = new HttpClient();
            using var request = new HttpRequestMessage(probe ? HttpMethod.Post : HttpMethod.Get, endpoint);
            if (!string.IsNullOrWhiteSpace(apiKey)) request.Headers.TryAddWithoutValidation("X-Vyral-Api-Key", apiKey);
            if (!string.IsNullOrWhiteSpace(identityToken)) request.Headers.TryAddWithoutValidation("X-Serverless-Authorization", "Bearer " + identityToken);
            using var response = await client.SendAsync(request);
            var content = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Canonical preflight failed with HTTP {(int)response.StatusCode}: {content}");
            Console.WriteLine(content);
        }, canonicalServerOption, canonicalApiKeyOption, canonicalIdentityTokenOption, canonicalPreflightProbeOption);

        var pruneTracesCommand = new Command("prune-traces", "Prune local trace records with explicit constraints");
        pruneTracesCommand.AddOption(dbOption);
        var operationOption = new Option<string?>("--operation", "Optional trace operation to prune, such as retrieval.search or provider.run.");
        var olderThanOption = new Option<string?>("--older-than", "Optional cutoff timestamp; traces created before this value are candidates.");
        var keepLatestOption = new Option<int?>("--keep-latest", "Keep this many newest matching traces.");
        var pruneLimitOption = new Option<int?>("--limit", "Maximum number of traces to prune.");
        var dryRunOption = new Option<bool>("--dry-run", "Preview matching traces without deleting them.");
        pruneTracesCommand.AddOption(operationOption);
        pruneTracesCommand.AddOption(olderThanOption);
        pruneTracesCommand.AddOption(keepLatestOption);
        pruneTracesCommand.AddOption(pruneLimitOption);
        pruneTracesCommand.AddOption(dryRunOption);
        pruneTracesCommand.SetHandler(async (dbPath, operation, olderThan, keepLatest, limit, dryRun) =>
        {
            var traces = new SqliteTraceStore(dbPath);
            await traces.InitializeAsync();
            var result = await traces.PruneTracesAsync(new TracePruneRequest
            {
                Operation = operation,
                OlderThan = ParseOptionalDateTime(olderThan, "--older-than"),
                KeepLatest = keepLatest,
                Limit = limit,
                DryRun = dryRun
            });
            Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
        }, dbOption, operationOption, olderThanOption, keepLatestOption, pruneLimitOption, dryRunOption);

        var summarizeTracesCommand = new Command("summarize-traces", "Summarize local trace counts by operation");
        summarizeTracesCommand.AddOption(dbOption);
        summarizeTracesCommand.AddOption(operationOption);
        summarizeTracesCommand.SetHandler(async (dbPath, operation) =>
        {
            var traces = new SqliteTraceStore(dbPath);
            await traces.InitializeAsync();
            var summary = await traces.SummarizeTracesAsync(operation);
            Console.WriteLine(JsonSerializer.Serialize(summary, JsonOptions));
        }, dbOption, operationOption);

        var exportTracesCommand = new Command("export-traces", "Export local trace records as a reviewable JSON bundle");
        exportTracesCommand.AddOption(dbOption);
        exportTracesCommand.AddOption(operationOption);
        var traceExportLimitOption = new Option<int?>("--limit", "Maximum number of newest matching traces to include.");
        var failOnUnsafeContentOption = new Option<bool>("--fail-on-unsafe-content", "Fail when the trace bundle contains likely secrets.");
        var traceExportFileOption = new Option<string?>("--file", "Optional output file path. Omit to write JSON to stdout.");
        exportTracesCommand.AddOption(traceExportLimitOption);
        exportTracesCommand.AddOption(failOnUnsafeContentOption);
        exportTracesCommand.AddOption(traceExportFileOption);
        exportTracesCommand.SetHandler(async (dbPath, operation, limit, failOnUnsafeContent, filePath) =>
        {
            var traces = new SqliteTraceStore(dbPath);
            await traces.InitializeAsync();
            var bundle = await traces.ExportTracesAsync(new TraceExportRequest
            {
                Operation = operation,
                Limit = limit,
                FailOnUnsafeContent = failOnUnsafeContent
            });
            var json = JsonSerializer.Serialize(bundle, JsonOptions);
            if (string.IsNullOrWhiteSpace(filePath))
            {
                Console.WriteLine(json);
                return;
            }

            await File.WriteAllTextAsync(filePath, json);
            Console.WriteLine($"Exported {bundle.TraceCount} trace(s) to {filePath}. Content hash: {bundle.ContentHash}");
        }, dbOption, operationOption, traceExportLimitOption, failOnUnsafeContentOption, traceExportFileOption);

        rootCommand.AddCommand(initCommand);
        rootCommand.AddCommand(createCollectionCommand);
        rootCommand.AddCommand(deleteCollectionCommand);
        rootCommand.AddCommand(upsertCommand);
        rootCommand.AddCommand(getCommand);
        rootCommand.AddCommand(searchCommand);
        rootCommand.AddCommand(queryRecordsCommand);
        rootCommand.AddCommand(searchRecordsCommand);
        rootCommand.AddCommand(exportCommand);
        rootCommand.AddCommand(importCommand);
        rootCommand.AddCommand(exportCanonicalCommand);
        rootCommand.AddCommand(restoreCanonicalCommand);
        rootCommand.AddCommand(exportCanonicalArchiveCommand);
        rootCommand.AddCommand(restoreCanonicalArchiveCommand);
        rootCommand.AddCommand(applyCanonicalMigrationsCommand);
        rootCommand.AddCommand(canonicalPreflightCommand);
        rootCommand.AddCommand(pruneTracesCommand);
        rootCommand.AddCommand(summarizeTracesCommand);
        rootCommand.AddCommand(exportTracesCommand);

        return await rootCommand.InvokeAsync(args);
    }

    private static List<string> ParseCommaSeparated(string value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? new List<string>()
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    private static string ResolveCanonicalDatabasePath(string databasePath, string? canonicalDatabasePath) =>
        string.IsNullOrWhiteSpace(canonicalDatabasePath)
            ? Path.ChangeExtension(databasePath, ".canonical.sqlite")
            : canonicalDatabasePath.Trim();

    private static async Task<QueryEnvelope> ReadQueryEnvelopeAsync(string envelopeJson, string? envelopeFile)
    {
        var json = string.IsNullOrWhiteSpace(envelopeFile)
            ? envelopeJson
            : await File.ReadAllTextAsync(envelopeFile);

        return JsonSerializer.Deserialize<QueryEnvelope>(json, JsonOptions) ?? new QueryEnvelope();
    }

    private static DateTime? ParseOptionalDateTime(string? value, string optionName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
        {
            return parsed;
        }

        throw new InvalidOperationException($"{optionName} must be a valid date/time value.");
    }

    private static IEmbeddingProvider CreateEmbeddingProvider(
        string providerId,
        string? modelId,
        string? modelPath,
        string? vocabPath,
        string? executionProvider,
        int? intraOpThreads,
        int? interOpThreads,
        long? cudaMemoryLimitMb,
        int dimensions)
    {
        var registry = new EmbeddingProviderRegistry(LocalEmbeddingProviders.CreateFactories().Concat(OnnxEmbeddingProviders.CreateFactories()));
        return registry.Create(new EmbeddingProviderOptions
        {
            Provider = providerId,
            ModelId = modelId,
            Dimensions = dimensions,
            ModelPath = modelPath,
            VocabPath = vocabPath,
            ExecutionProvider = executionProvider,
            IntraOpNumThreads = intraOpThreads,
            InterOpNumThreads = interOpThreads,
            CudaMemoryLimitMb = cudaMemoryLimitMb
        });
    }
}
