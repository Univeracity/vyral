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

download_model "Xenova/paraphrase-MiniLM-L3-v2" "paraphrase-MiniLM-L3-v2-quantized" "model_quantized.onnx"
download_model "Xenova/paraphrase-MiniLM-L3-v2" "paraphrase-MiniLM-L3-v2-fp32" "model.onnx"
download_model "Xenova/all-MiniLM-L6-v2" "all-MiniLM-L6-v2-quantized" "model_quantized.onnx"
download_model "Xenova/all-MiniLM-L6-v2" "all-MiniLM-L6-v2-fp32" "model.onnx"
download_model "Xenova/multi-qa-MiniLM-L6-cos-v1" "multi-qa-MiniLM-L6-cos-v1-quantized" "model_quantized.onnx"
download_model "Xenova/multi-qa-MiniLM-L6-cos-v1" "multi-qa-MiniLM-L6-cos-v1-fp32" "model.onnx"
download_model "Xenova/all-MiniLM-L12-v2" "all-MiniLM-L12-v2-quantized" "model_quantized.onnx"
download_model "Xenova/all-MiniLM-L12-v2" "all-MiniLM-L12-v2-fp32" "model.onnx"
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

echo "Downloaded benchmark ONNX models under $root."
