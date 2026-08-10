using Vyral.Execution;

namespace Vyral.Server;

internal static class VyralAdmissionOperations
{
    public const string IngestRecordArtifact = "ingestRecordArtifact";
    public const string CreateCollection = "createCollection";
    public const string DeleteCollection = "deleteCollection";
    public const string ImportCollection = "importCollection";
    public const string ImportGraphEnvelope = "importGraphEnvelope";
    public const string UpsertRecords = "upsertRecords";
    public const string IngestRagText = "ingestRagText";
    public const string IngestRagTextBatch = "ingestRagTextBatch";
    public const string StartExecutionRun = "startExecutionRun";
    public const string StartEmbeddingJob = "startEmbeddingJob";
    public const string StartProviderRunJob = "startProviderRunJob";
    public const string RunProviderCapability = "runProviderCapability";
    public const string StartCollectionImportJob = "startCollectionImportJob";
    public const string StartRecordBatchUpsertJob = "startRecordBatchUpsertJob";
    public const string StartRagIngestionTextJob = "startRagIngestionTextJob";
    public const string StartRagIngestionBatchJob = "startRagIngestionBatchJob";
    public const string StartRetrievalEvaluationJob = "startRetrievalEvaluationJob";
    public const string StartRetrievalEvaluationComparisonJob = "startRetrievalEvaluationComparisonJob";
    public const string StartGraphImportJob = "startGraphImportJob";
    public const string StartGraphInspectionJob = "startGraphInspectionJob";
    public const string StartGraphDoctorJob = "startGraphDoctorJob";

    private static readonly HashSet<string> KnownOperationIds = new(StringComparer.Ordinal)
    {
        IngestRecordArtifact,
        CreateCollection,
        DeleteCollection,
        ImportCollection,
        ImportGraphEnvelope,
        UpsertRecords,
        IngestRagText,
        IngestRagTextBatch,
        StartExecutionRun,
        StartEmbeddingJob,
        StartProviderRunJob,
        RunProviderCapability,
        StartCollectionImportJob,
        StartRecordBatchUpsertJob,
        StartRagIngestionTextJob,
        StartRagIngestionBatchJob,
        StartRetrievalEvaluationJob,
        StartRetrievalEvaluationComparisonJob,
        StartGraphImportJob,
        StartGraphInspectionJob,
        StartGraphDoctorJob
    };

    public static string ResolveOperationId(ExecutionRun run, string fallback)
    {
        ArgumentNullException.ThrowIfNull(run);
        var tagged = run.Tags.GetValueOrDefault("vyral.admission.operation-id");
        return tagged is not null && KnownOperationIds.Contains(tagged) ? tagged : fallback;
    }

    public static (string OperationId, string StatusUri) Resolve(ExecutionRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var operationId = run.Tags.GetValueOrDefault("vyral.admission.operation-id");
        if (operationId is not null && KnownOperationIds.Contains(operationId))
        {
            var statusUri = operationId is IngestRecordArtifact or CreateCollection or DeleteCollection
                ? $"/execution/runs/{run.Id}"
                : run.HandlerId switch
                {
                    ExecutionRuntimeProviderRunJobAdapter.HandlerId => $"/provider-jobs/{run.Id}",
                    ExecutionRuntimeRecordImportJobAdapter.CollectionImportHandlerId or
                    ExecutionRuntimeRecordImportJobAdapter.BatchUpsertHandlerId => $"/record-import/jobs/{run.Id}",
                    ExecutionRuntimeRagIngestionJobAdapter.TextHandlerId or
                    ExecutionRuntimeRagIngestionJobAdapter.BatchHandlerId => $"/rag/ingestion/jobs/{run.Id}",
                    ExecutionRuntimeGraphJobAdapter.ImportHandlerId or
                    ExecutionRuntimeGraphJobAdapter.InspectionHandlerId or
                    ExecutionRuntimeGraphJobAdapter.DoctorHandlerId => $"/graph/jobs/{run.Id}",
                    _ => $"/execution/runs/{run.Id}"
                };
            return (operationId, statusUri);
        }
        if (string.Equals(operationId, IngestRecordArtifact, StringComparison.Ordinal))
            return (IngestRecordArtifact, $"/execution/runs/{run.Id}");
        if (string.Equals(run.HandlerId, ExecutionRuntimeCollectionManagementAdapter.CreateHandlerId, StringComparison.Ordinal))
            return (CreateCollection, $"/execution/runs/{run.Id}");
        if (string.Equals(run.HandlerId, ExecutionRuntimeCollectionManagementAdapter.DeleteHandlerId, StringComparison.Ordinal))
            return (DeleteCollection, $"/execution/runs/{run.Id}");
        if (string.Equals(run.HandlerId, ExecutionRuntimeEmbeddingJobAdapter.HandlerId, StringComparison.Ordinal) &&
            string.Equals(run.Tags.GetValueOrDefault("vyral.job"), "embedding", StringComparison.Ordinal))
            return (StartEmbeddingJob, $"/embeddings/jobs/{run.Id}");
        if (string.Equals(run.HandlerId, ExecutionRuntimeProviderRunJobAdapter.HandlerId, StringComparison.Ordinal) &&
            string.Equals(run.Tags.GetValueOrDefault("vyral.job"), "provider-run", StringComparison.Ordinal))
            return (StartProviderRunJob, $"/provider-jobs/{run.Id}");
        if (string.Equals(run.HandlerId, ExecutionRuntimeRecordImportJobAdapter.CollectionImportHandlerId, StringComparison.Ordinal))
            return (StartCollectionImportJob, $"/record-import/jobs/{run.Id}");
        if (string.Equals(run.HandlerId, ExecutionRuntimeRecordImportJobAdapter.BatchUpsertHandlerId, StringComparison.Ordinal))
            return (StartRecordBatchUpsertJob, $"/record-import/jobs/{run.Id}");
        if (string.Equals(run.HandlerId, ExecutionRuntimeRagIngestionJobAdapter.TextHandlerId, StringComparison.Ordinal))
            return (StartRagIngestionTextJob, $"/rag/ingestion/jobs/{run.Id}");
        if (string.Equals(run.HandlerId, ExecutionRuntimeRagIngestionJobAdapter.BatchHandlerId, StringComparison.Ordinal))
            return (StartRagIngestionBatchJob, $"/rag/ingestion/jobs/{run.Id}");
        if (string.Equals(run.HandlerId, ExecutionRuntimeRetrievalEvaluationJobAdapter.EvaluationHandlerId, StringComparison.Ordinal))
            return (StartRetrievalEvaluationJob, $"/retrieval/evaluate/jobs/{run.Id}");
        if (string.Equals(run.HandlerId, ExecutionRuntimeRetrievalEvaluationJobAdapter.ComparisonHandlerId, StringComparison.Ordinal))
            return (StartRetrievalEvaluationComparisonJob, $"/retrieval/evaluate/jobs/{run.Id}");
        if (string.Equals(run.HandlerId, ExecutionRuntimeGraphJobAdapter.ImportHandlerId, StringComparison.Ordinal))
            return (StartGraphImportJob, $"/graph/jobs/{run.Id}");
        if (string.Equals(run.HandlerId, ExecutionRuntimeGraphJobAdapter.InspectionHandlerId, StringComparison.Ordinal))
            return (StartGraphInspectionJob, $"/graph/jobs/{run.Id}");
        if (string.Equals(run.HandlerId, ExecutionRuntimeGraphJobAdapter.DoctorHandlerId, StringComparison.Ordinal))
            return (StartGraphDoctorJob, $"/graph/jobs/{run.Id}");
        return (StartExecutionRun, $"/execution/runs/{run.Id}");
    }
}
