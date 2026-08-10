#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
data_dir="$(realpath -m "${VYRAL_DATA_DIR:-"$repo_root/.vyral"}")"
database_path="$(realpath -m "${DatabasePath:-${VYRAL_DATABASE_PATH:-"$data_dir/vyral.sqlite"}}")"
objects_path="$(realpath -m "${ObjectsPath:-${VYRAL_OBJECTS_PATH:-"$data_dir/objects"}}")"
artifact_dir="$(realpath -m "${Providers__ArtifactDirectory:-${VYRAL_PROVIDER_ARTIFACT_DIR:-"$data_dir/provider-runs"}}")"
urls="${ASPNETCORE_URLS:-${VYRAL_URLS:-http://127.0.0.1:5220}}"

# This launcher is specifically for isolated local use. Preserve an explicitly selected
# environment, but make Development the stable default so development-only local access
# behavior does not unexpectedly become Production.
if [[ -z "${ASPNETCORE_ENVIRONMENT:-}" && -z "${DOTNET_ENVIRONMENT:-}" ]]; then
  export ASPNETCORE_ENVIRONMENT=Development
  export DOTNET_ENVIRONMENT=Development
fi

set_from_alias() {
  local source_name="$1"
  local target_name="$2"
  local source_value="${!source_name:-}"
  local target_value="${!target_name:-}"
  if [[ -n "$source_value" && -z "$target_value" ]]; then
    export "$target_name=$source_value"
  fi
}

mkdir -p "$(dirname "$database_path")" "$objects_path" "$artifact_dir"

export DatabasePath="$database_path"
export ObjectsPath="$objects_path"
export Providers__ArtifactDirectory="$artifact_dir"
export ASPNETCORE_URLS="$urls"

set_from_alias VYRAL_API_KEY Server__ApiKey
set_from_alias VYRAL_ENABLE_LIVE_TARGETS Providers__EnableLiveTargets
set_from_alias VYRAL_EMBEDDING_PROVIDER Embedding__Provider
set_from_alias VYRAL_EMBEDDING_MODEL_ID Embedding__ModelId
set_from_alias VYRAL_EMBEDDING_DIMENSIONS Embedding__Dimensions
set_from_alias VYRAL_EMBEDDING_MODEL_PATH Embedding__ModelPath
set_from_alias VYRAL_EMBEDDING_VOCAB_PATH Embedding__VocabPath
set_from_alias VYRAL_EMBEDDING_EXECUTION_PROVIDER Embedding__ExecutionProvider
set_from_alias VYRAL_EMBEDDING_MAX_TOKENS Embedding__MaxTokens
set_from_alias VYRAL_EMBEDDING_LOWERCASE Embedding__Lowercase
set_from_alias VYRAL_EMBEDDING_NORMALIZE Embedding__Normalize
set_from_alias VYRAL_EMBEDDING_POOLING Embedding__Pooling
set_from_alias VYRAL_EMBEDDING_OUTPUT_NAME Embedding__OutputName
set_from_alias VYRAL_EMBEDDING_INTRA_OP_THREADS Embedding__IntraOpNumThreads
set_from_alias VYRAL_EMBEDDING_INTER_OP_THREADS Embedding__InterOpNumThreads
set_from_alias VYRAL_EMBEDDING_EXECUTION_MODE Embedding__ExecutionMode
set_from_alias VYRAL_EMBEDDING_CUDA_DEVICE_ID Embedding__CudaDeviceId
set_from_alias VYRAL_EMBEDDING_CUDA_MEMORY_LIMIT_MB Embedding__CudaMemoryLimitMb
set_from_alias VYRAL_EMBEDDING_QUERY_PREFIX Embedding__QueryPrefix
set_from_alias VYRAL_EMBEDDING_PASSAGE_PREFIX Embedding__PassagePrefix
set_from_alias VYRAL_EMBEDDING_SYMMETRIC_PREFIX Embedding__SymmetricPrefix
set_from_alias VYRAL_RERANK_PROVIDER Retrieval__Rerank__Provider
set_from_alias VYRAL_ONNX_RERANK_CPU_MODEL_ID Providers__OnnxReranker__Cpu__ModelId
set_from_alias VYRAL_ONNX_RERANK_CPU_MODEL_PATH Providers__OnnxReranker__Cpu__ModelPath
set_from_alias VYRAL_ONNX_RERANK_CPU_VOCAB_PATH Providers__OnnxReranker__Cpu__VocabPath
set_from_alias VYRAL_ONNX_RERANK_CPU_MAX_TOKENS Providers__OnnxReranker__Cpu__MaxTokens
set_from_alias VYRAL_ONNX_RERANK_CPU_BATCH_SIZE Providers__OnnxReranker__Cpu__BatchSize
set_from_alias VYRAL_ONNX_RERANK_CPU_SCORE_MODE Providers__OnnxReranker__Cpu__ScoreMode
set_from_alias VYRAL_ONNX_RERANK_GPU_MODEL_ID Providers__OnnxReranker__Gpu__ModelId
set_from_alias VYRAL_ONNX_RERANK_GPU_MODEL_PATH Providers__OnnxReranker__Gpu__ModelPath
set_from_alias VYRAL_ONNX_RERANK_GPU_VOCAB_PATH Providers__OnnxReranker__Gpu__VocabPath
set_from_alias VYRAL_ONNX_RERANK_GPU_EXECUTION_PROVIDER Providers__OnnxReranker__Gpu__ExecutionProvider
set_from_alias VYRAL_ONNX_RERANK_GPU_MAX_TOKENS Providers__OnnxReranker__Gpu__MaxTokens
set_from_alias VYRAL_ONNX_RERANK_GPU_BATCH_SIZE Providers__OnnxReranker__Gpu__BatchSize
set_from_alias VYRAL_ONNX_RERANK_GPU_SCORE_MODE Providers__OnnxReranker__Gpu__ScoreMode
set_from_alias VYRAL_ONNX_RERANK_GPU_CUDA_MEMORY_LIMIT_MB Providers__OnnxReranker__Gpu__CudaMemoryLimitMb

printf 'Starting Vyral server\n'
printf '  Environment: %s\n' "${ASPNETCORE_ENVIRONMENT:-${DOTNET_ENVIRONMENT:-Production}}"
printf '  URLs: %s\n' "$ASPNETCORE_URLS"
printf '  DatabasePath: %s\n' "$DatabasePath"
printf '  ObjectsPath: %s\n' "$ObjectsPath"
printf '  Providers:ArtifactDirectory: %s\n' "$Providers__ArtifactDirectory"

exec dotnet run --no-launch-profile --project "$repo_root/src/Vyral.Server/Vyral.Server.csproj" -- "$@"
