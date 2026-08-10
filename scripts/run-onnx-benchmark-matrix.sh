#!/usr/bin/env bash
set -euo pipefail

iterations="${VYRAL_BENCHMARK_ITERATIONS:-16}"
warmup="${VYRAL_BENCHMARK_WARMUP:-4}"
skip_gpu="${VYRAL_BENCHMARK_SKIP_GPU:-auto}"
results_dir="$(realpath -m "${VYRAL_BENCHMARK_RESULTS_DIR:-.vyral/benchmarks}")"
models_root="$(realpath -m "${VYRAL_MODEL_ROOT:-.vyral/models}")"
timestamp="$(date -u +%Y%m%dT%H%M%SZ)"
output="$results_dir/onnx-$timestamp.jsonl"

mkdir -p "$results_dir"

has_onnx_model() {
  local path="$1"
  compgen -G "$path/onnx/*.onnx" >/dev/null && [[ -f "$path/vocab.txt" ]]
}

run_benchmark() {
  local build_args="$1"
  local wrapper="$2"
  local model_id="$3"
  local model_path="$4"
  local execution_provider="$5"
  shift 5

  echo "benchmark $model_id $execution_provider $*" >&2
  if [[ -n "$wrapper" ]]; then
    "$wrapper" dotnet run --no-build --project tools/Vyral.Benchmarks $build_args -- \
      --model-id "$model_id" \
      --model-path "$model_path" \
      --execution-provider "$execution_provider" \
      --iterations "$iterations" \
      --warmup "$warmup" \
      "$@" >> "$output"
  else
    dotnet run --no-build --project tools/Vyral.Benchmarks $build_args -- \
      --model-id "$model_id" \
      --model-path "$model_path" \
      --execution-provider "$execution_provider" \
      --iterations "$iterations" \
      --warmup "$warmup" \
      "$@" >> "$output"
  fi
}

should_run_gpu() {
  case "${skip_gpu,,}" in
    1|true|yes)
      return 1
      ;;
    0|false|no)
      return 0
      ;;
  esac

  command -v nvidia-smi >/dev/null 2>&1 && nvidia-smi >/dev/null 2>&1
}

cpu_models=(
  "paraphrase-MiniLM-L3-v2-quantized"
  "all-MiniLM-L6-v2-quantized"
  "multi-qa-MiniLM-L6-cos-v1-quantized"
  "all-MiniLM-L12-v2-quantized"
  "bge-small-en-v1.5-quantized"
  "bge-base-en-v1.5-quantized"
  "e5-small-v2-quantized"
  "e5-base-v2-quantized"
)

gpu_models=(
  "paraphrase-MiniLM-L3-v2-fp32"
  "all-MiniLM-L6-v2-fp32"
  "multi-qa-MiniLM-L6-cos-v1-fp32"
  "all-MiniLM-L12-v2-fp32"
  "bge-small-en-v1.5-fp32"
  "bge-base-en-v1.5-fp32"
  "e5-small-v2-fp32"
  "e5-base-v2-fp32"
)

if [[ -n "${VYRAL_BENCHMARK_CPU_MODELS:-}" ]]; then
  IFS=',' read -r -a cpu_models <<< "$VYRAL_BENCHMARK_CPU_MODELS"
fi

if [[ -n "${VYRAL_BENCHMARK_GPU_MODELS:-}" ]]; then
  IFS=',' read -r -a gpu_models <<< "$VYRAL_BENCHMARK_GPU_MODELS"
fi

dotnet restore Vyral.sln
dotnet build Vyral.sln --no-restore
for model in "${cpu_models[@]}"; do
  path="$models_root/$model"
  if ! has_onnx_model "$path"; then
    echo "skipping missing CPU ONNX model: $model ($path)" >&2
    continue
  fi
  pooling="mean"
  max_tokens=256
  dimensions=384
  extra_args=()
  if [[ "$model" == bge-* ]]; then
    pooling="cls"
    max_tokens=512
    extra_args+=(--query-prefix "Represent this sentence for searching relevant passages: ")
  fi
  if [[ "$model" == e5-* ]]; then
    max_tokens=512
    extra_args+=(--query-prefix "query: " --passage-prefix "passage: ")
  fi
  if [[ "$model" == *base* ]]; then
    dimensions=768
  fi
  run_benchmark "" "" "$model/cpu-1t" "$path" "cpu" --dimensions "$dimensions" --intra-op-threads 1 --inter-op-threads 1 --pooling "$pooling" --max-tokens "$max_tokens" "${extra_args[@]}"
  run_benchmark "" "" "$model/cpu-2t" "$path" "cpu" --dimensions "$dimensions" --intra-op-threads 2 --inter-op-threads 1 --pooling "$pooling" --max-tokens "$max_tokens" "${extra_args[@]}"
  run_benchmark "" "" "$model/cpu-4t" "$path" "cpu" --dimensions "$dimensions" --intra-op-threads 4 --inter-op-threads 1 --pooling "$pooling" --max-tokens "$max_tokens" "${extra_args[@]}"
done

if should_run_gpu; then
  scripts/with-local-cuda-libs.sh dotnet restore Vyral.sln -p:VyralOnnxRuntime=Gpu
  scripts/with-local-cuda-libs.sh dotnet build Vyral.sln --no-restore -p:VyralOnnxRuntime=Gpu
  for model in "${gpu_models[@]}"; do
    path="$models_root/$model"
    if ! has_onnx_model "$path"; then
      echo "skipping missing GPU ONNX model: $model ($path)" >&2
      continue
    fi
    pooling="mean"
    max_tokens=256
    dimensions=384
    extra_args=()
    if [[ "$model" == bge-* ]]; then
      pooling="cls"
      max_tokens=512
      extra_args+=(--query-prefix "Represent this sentence for searching relevant passages: ")
    fi
    if [[ "$model" == e5-* ]]; then
      max_tokens=512
      extra_args+=(--query-prefix "query: " --passage-prefix "passage: ")
    fi
    if [[ "$model" == *base* ]]; then
      dimensions=768
    fi
    run_benchmark "-p:VyralOnnxRuntime=Gpu" "scripts/with-local-cuda-libs.sh" "$model/gpu-512m" "$path" "cudaRequired" --dimensions "$dimensions" --cuda-memory-limit-mb 512 --intra-op-threads 1 --inter-op-threads 1 --pooling "$pooling" --max-tokens "$max_tokens" "${extra_args[@]}"
    run_benchmark "-p:VyralOnnxRuntime=Gpu" "scripts/with-local-cuda-libs.sh" "$model/gpu-1024m" "$path" "cudaRequired" --dimensions "$dimensions" --cuda-memory-limit-mb 1024 --intra-op-threads 1 --inter-op-threads 1 --pooling "$pooling" --max-tokens "$max_tokens" "${extra_args[@]}"
  done
else
  echo "skipping GPU ONNX benchmarks; set VYRAL_BENCHMARK_SKIP_GPU=0 to force or configure nvidia-smi for auto mode" >&2
fi

dotnet restore Vyral.sln
dotnet build Vyral.sln --no-restore

if [[ "${VYRAL_BENCHMARK_PRINT_SUMMARY:-1}" != "0" && -x scripts/summarize-onnx-benchmark-results.sh && -n "$(command -v jq || true)" ]]; then
  scripts/summarize-onnx-benchmark-results.sh "$output" >&2
fi

echo "$output"
