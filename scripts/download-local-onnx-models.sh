#!/usr/bin/env bash
set -euo pipefail

root="$(realpath -m "${1:-.vyral/models}")"

download() {
  local repo="$1"
  local file="$2"
  local target="$3"
  mkdir -p "$(dirname "$target")"
  if [[ -f "$target" ]]; then
    echo "exists $target"
    return
  fi

  echo "download $target"
  curl --fail --location --retry 3 --output "$target" "https://huggingface.co/$repo/resolve/main/$file"
}

download_model() {
  local repo="$1"
  local directory="$2"
  local model_file="$3"
  local target="$root/$directory"

  download "$repo" "onnx/$model_file" "$target/onnx/$model_file"
  download "$repo" "vocab.txt" "$target/vocab.txt"
  download "$repo" "config.json" "$target/config.json"
}

minilm_cpu_dir="$root/all-MiniLM-L6-v2-quantized"
minilm_gpu_dir="$root/all-MiniLM-L6-v2-fp32"
multiqa_cpu_dir="$root/multi-qa-MiniLM-L6-cos-v1-quantized"
multiqa_gpu_dir="$root/multi-qa-MiniLM-L6-cos-v1-fp32"
bge_cpu_dir="$root/bge-small-en-v1.5-quantized"
bge_gpu_dir="$root/bge-small-en-v1.5-fp32"
bge_base_cpu_dir="$root/bge-base-en-v1.5-quantized"
bge_base_gpu_dir="$root/bge-base-en-v1.5-fp32"
e5_small_cpu_dir="$root/e5-small-v2-quantized"
e5_small_gpu_dir="$root/e5-small-v2-fp32"
e5_base_cpu_dir="$root/e5-base-v2-quantized"
e5_base_gpu_dir="$root/e5-base-v2-fp32"
reranker_cpu_dir="$root/ms-marco-MiniLM-L-6-v2-quantized"
reranker_gpu_dir="$root/ms-marco-MiniLM-L-6-v2-fp32"

download_model "Xenova/all-MiniLM-L6-v2" "all-MiniLM-L6-v2-quantized" "model_quantized.onnx"
download_model "Xenova/all-MiniLM-L6-v2" "all-MiniLM-L6-v2-fp32" "model.onnx"
download_model "Xenova/multi-qa-MiniLM-L6-cos-v1" "multi-qa-MiniLM-L6-cos-v1-quantized" "model_quantized.onnx"
download_model "Xenova/multi-qa-MiniLM-L6-cos-v1" "multi-qa-MiniLM-L6-cos-v1-fp32" "model.onnx"
download_model "Xenova/bge-small-en-v1.5" "bge-small-en-v1.5-quantized" "model_quantized.onnx"
download_model "Xenova/bge-small-en-v1.5" "bge-small-en-v1.5-fp32" "model.onnx"
download_model "Xenova/bge-base-en-v1.5" "bge-base-en-v1.5-quantized" "model_quantized.onnx"
download_model "Xenova/bge-base-en-v1.5" "bge-base-en-v1.5-fp32" "model.onnx"
download_model "Xenova/e5-small-v2" "e5-small-v2-quantized" "model_quantized.onnx"
download_model "Xenova/e5-small-v2" "e5-small-v2-fp32" "model.onnx"
download_model "Xenova/e5-base-v2" "e5-base-v2-quantized" "model_quantized.onnx"
download_model "Xenova/e5-base-v2" "e5-base-v2-fp32" "model.onnx"
download_model "Xenova/ms-marco-MiniLM-L-6-v2" "ms-marco-MiniLM-L-6-v2-quantized" "model_quantized.onnx"
download_model "Xenova/ms-marco-MiniLM-L-6-v2" "ms-marco-MiniLM-L-6-v2-fp32" "model.onnx"

cat <<EOF

Downloaded local ONNX embedding models under $root.

Recommended semantic CPU preset:
  provider: onnx-multi-qa-minilm-cpu
  model:    $multiqa_cpu_dir

Recommended semantic GPU-preferred preset:
  provider: onnx-multi-qa-minilm-gpu
  model:    $multiqa_gpu_dir

Balanced general CPU preset:
  provider: onnx-minilm-cpu
  model:    $minilm_cpu_dir

Balanced general GPU-preferred preset:
  provider: onnx-minilm-gpu
  model:    $minilm_gpu_dir

BGE CPU preset:
  provider: onnx-bge-small-cpu
  model:    $bge_cpu_dir

BGE GPU-preferred preset:
  provider: onnx-bge-small-gpu
  model:    $bge_gpu_dir

Higher-quality BGE CPU preset:
  provider: onnx-bge-base-cpu
  model:    $bge_base_cpu_dir

Higher-quality BGE GPU-preferred preset:
  provider: onnx-bge-base-gpu
  model:    $bge_base_gpu_dir

E5 small CPU preset:
  provider: onnx-e5-small-cpu
  model:    $e5_small_cpu_dir

E5 small GPU-preferred preset:
  provider: onnx-e5-small-gpu
  model:    $e5_small_gpu_dir

Higher-quality E5 CPU preset:
  provider: onnx-e5-base-cpu
  model:    $e5_base_cpu_dir

Higher-quality E5 GPU-preferred preset:
  provider: onnx-e5-base-gpu
  model:    $e5_base_gpu_dir

Cross-encoder reranker CPU preset:
  provider: onnx-cross-encoder-reranker-cpu
  model:    $reranker_cpu_dir

Cross-encoder reranker GPU-preferred preset:
  provider: onnx-cross-encoder-reranker-gpu
  model:    $reranker_gpu_dir

Live test example:
  VYRAL_ONNX_MODEL_DIR="$multiqa_cpu_dir" VYRAL_ONNX_POOLING=mean dotnet test tests/Vyral.Tests.Local --filter OnnxProvider_GeneratesNormalizedSemanticVectorFromUntrackedModel

Reranker live test example:
  VYRAL_ONNX_RERANK_MODEL_DIR="$reranker_cpu_dir" dotnet test tests/Vyral.Tests.Providers --filter OnnxRerankerProvider_ReranksCandidatesWithUntrackedModel
EOF
