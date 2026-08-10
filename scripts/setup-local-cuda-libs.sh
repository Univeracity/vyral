#!/usr/bin/env bash
set -euo pipefail

root="$(realpath -m "${1:-.vyral/cuda-libs}")"

python3 -m pip install \
  --target "$root" \
  --upgrade \
  nvidia-cuda-runtime-cu12 \
  nvidia-cublas-cu12 \
  nvidia-cufft-cu12 \
  nvidia-curand-cu12 \
  nvidia-cudnn-cu12

cat <<EOF

Installed local CUDA/cuDNN runtime libraries under $root.

Run VYRAL GPU commands through:
  scripts/with-local-cuda-libs.sh <command>

Example:
  scripts/with-local-cuda-libs.sh dotnet test tests/Vyral.Tests.Local/Vyral.Tests.Local.csproj -p:VyralOnnxRuntime=Gpu --filter OnnxProvider_GeneratesNormalizedSemanticVectorFromUntrackedModel
EOF
