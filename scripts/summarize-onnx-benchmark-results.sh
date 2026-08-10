#!/usr/bin/env bash
set -euo pipefail

inputs=("$@")
if [[ "${#inputs[@]}" -eq 0 ]]; then
  latest="$(find .vyral/benchmarks -maxdepth 1 -type f -name 'onnx-[0-9]*.jsonl' 2>/dev/null | sort | tail -1)"
  if [[ -n "$latest" ]]; then
    inputs=("$latest")
  fi
fi

if [[ "${#inputs[@]}" -eq 0 ]]; then
  echo "No ONNX benchmark JSONL file found." >&2
  exit 1
fi

for input in "${inputs[@]}"; do
  if [[ ! -f "$input" ]]; then
    echo "ONNX benchmark JSONL file not found: $input" >&2
    exit 1
  fi
done

if ! command -v jq >/dev/null 2>&1; then
  echo "jq is required to summarize ONNX benchmark results." >&2
  exit 1
fi

echo "ONNX benchmark summary: ${inputs[*]}"
echo
printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' \
  "profile" "mean_ms" "p95_ms" "eps" "cpu_core_eq" "rss_mb" "hit@1" "hit@3" "mrr" "worst_rank"

jq -s -r '
  sort_by(-.HitAt1, -.MeanReciprocalRank, .P95Ms)[]
  | [
      .ModelId,
      ((.MeanMs * 100 | round) / 100),
      ((.P95Ms * 100 | round) / 100),
      ((.EmbeddingsPerSecond * 10 | round) / 10),
      ((.CpuCoreEquivalent * 100 | round) / 100),
      ((.WorkingSetMb * 10 | round) / 10),
      ((.HitAt1 * 100 | round) / 100),
      ((.HitAt3 * 100 | round) / 100),
      ((.MeanReciprocalRank * 100 | round) / 100),
      .WorstExpectedRank
    ]
  | @tsv
' "${inputs[@]}"

best_cpu="$(
  jq -s -r '
    map(select(.ActiveExecutionProvider == "cpu" and .HitAt1 == 1 and .WorstExpectedRank == 1))
    | sort_by(.P95Ms)
    | .[0].ModelId // ""
  ' "${inputs[@]}"
)"

degraded="$(
  jq -s -r '
    map(select(.HitAt1 < 1 or .WorstExpectedRank > 1))
    | map(.ModelId + " (hit@1=" + ((.HitAt1 * 100 | round) / 100 | tostring) + ", worst_rank=" + (.WorstExpectedRank | tostring) + ")")
    | join(", ")
  ' "${inputs[@]}"
)"

echo
if [[ -n "$best_cpu" ]]; then
  echo "recommended_cpu_profile=$best_cpu"
else
  echo "recommended_cpu_profile=none"
fi

if [[ -n "$degraded" ]]; then
  echo "degraded_quality_profiles=$degraded"
else
  echo "degraded_quality_profiles=none"
fi
