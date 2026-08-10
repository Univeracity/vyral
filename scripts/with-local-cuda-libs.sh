#!/usr/bin/env bash
set -euo pipefail

root="$(realpath -m "${VYRAL_CUDA_LIB_ROOT:-.vyral/cuda-libs}")"
if [[ ! -d "$root" ]]; then
  echo "Local CUDA library root does not exist: $root" >&2
  echo "Run scripts/setup-local-cuda-libs.sh first." >&2
  exit 1
fi

cuda_lib_path="$(find "$root/nvidia" -type d -path '*/lib' | sort | paste -sd: -)"
if [[ -z "$cuda_lib_path" ]]; then
  echo "No NVIDIA library directories were found under $root." >&2
  echo "Run scripts/setup-local-cuda-libs.sh first." >&2
  exit 1
fi

export LD_LIBRARY_PATH="$cuda_lib_path${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"

if [[ "$#" -eq 0 ]]; then
  echo "$LD_LIBRARY_PATH"
  exit 0
fi

exec "$@"
