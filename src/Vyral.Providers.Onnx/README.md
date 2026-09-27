# Vyral.Providers.Onnx

Local, in-process `IProviderTarget` implementations backed by
`Vyral.Embeddings.Onnx`. No network, no auth. Two targets:

- `OnnxCrossEncoderRerankerProviderTarget` (`ai.rerank`) — see its own doc
  comments; unchanged by this note.
- `OnnxNliJudgeProviderTarget` (`ai.judge`) — a zero-shot NLI/entailment
  scorer: each candidate option/level is scored as an entailment hypothesis
  against the question's premise, and per-option entailment logits are
  softmaxed into a probability distribution. Supports all three `ai.judge`
  question types: Choice and Score share the same underlying entailment-scoring
  mechanism (`ScoreOptionsByEntailmentAsync`) and only differ in how the
  resulting distribution is interpreted — argmax for Choice, a probability-
  weighted position for Score (via a separate `ScoreHypothesisTemplate`,
  since an ordered-scale rating reads more naturally than a topic-membership
  phrasing); Noul scores the prompt itself as the sole hypothesis.

This target's calibration (below) is fit against Vyral's own hand-labeled
ground truth. It has never been calibrated against, or otherwise derived
from, TypeSafe Jev's outputs — the two `ai.judge` implementers are
independent by construction, not just by omission. See
`Vyral.Providers.Jev`'s README for why that independence specifically
matters (TypeSafe's Master Customer Agreement §2.3(b)).

## Claim verification vs. topic selection

The Choice path (above) is shaped for topic selection: one premise, compare
it against N *different* candidate hypotheses, pick the best. That's the
wrong shape for claim verification — "does this ONE specific claim hold
against this passage" — which is genuine single-hypothesis NLI, not a
comparison across options. Routing a claim-verification task through Choice
(e.g. options `supports` / `contradicts` / `says_nothing`, each a templated
hypothesis) changes the question into a comparison between candidate
hypotheses. The default Choice template also omits the question prompt,
so identical context and option labels produce identical inputs even when
the claim changes. Use the native single-hypothesis path for this task.

For that task, use a **Noul** question instead and read
`AiJudgeAnswer.RawClassProbabilities`. A genuine entailment/neutral/
contradiction checkpoint already computes its full 3-class distribution
internally for the single (premise, claim) pair before this package collapses
it to one entailment probability for the portable `Probability` field;
`RawClassProbabilities` exposes that native breakdown directly, labeled by
`OnnxNliJudgeProviderOptions.ClassLabels` (default `["entailment", "neutral",
"contradiction"]`, matching the shipped model's own `config.json` id2label
order — confirm and update this alongside `EntailmentIndex` when swapping
checkpoints). No per-option hypothesis engineering, no new calibration data:
`entailment` **is** supports, `contradiction` **is** contradicts, `neutral`
**is** says-nothing, read straight off the checkpoint's own training
objective. `RawClassProbabilities` is additive, provider-specific enrichment
— only an implementer backed by a genuine multi-class classifier can honestly
populate it; the deterministic stub and Jev leave it null rather than fake
one, and `ClassLabels = null` disables it here too rather than guessing names
for an unconfirmed checkpoint.

## Judge target: model selection

Default model: **`Xenova/mobilebert-uncased-mnli`**. Selected after checking
several NLI checkpoints:

| Checkpoint family | Why not the default |
| --- | --- |
| DeBERTa-v3 (MoritzLaurer's zero-shot models, generally the strongest accuracy) | SentencePiece tokenizer — this package's tokenizer (shared with the reranker) only reads a WordPiece `vocab.txt` |
| RoBERTa (`cross-encoder/nli-*`) | Byte-pair tokenizer (`merges.txt`/`vocab.json`) — same WordPiece-only constraint |
| DistilBERT-base-uncased-mnli | WordPiece-compatible and a reasonable alternative, but ~64MB quantized vs. MobileBERT's ~27MB |

MobileBERT-uncased-mnli is WordPiece (matches the tokenizer this package
already has, and the reranker's own default model family), well-established,
and small — fitting the local-first "no large download by default" posture.

**Class order is not assumed.** The model's own `config.json` `id2label` is
`{0: ENTAILMENT, 1: NEUTRAL, 2: CONTRADICTION}`, confirmed directly, not
inferred from the common (but not universal) "entailment at index 2"
convention some other MNLI checkpoints use. `EntailmentIndex` defaults to 0
for this reason. **Swapping the default model requires re-checking this
value against the new checkpoint's own `config.json`** — do not assume it
carries over.

Default paths (`.vyral/models/mobilebert-uncased-mnli-quantized/...` for CPU,
`...-fp32/...` for GPU) are untracked; nothing is downloaded automatically.
Fetch the files from the model's Hugging Face repo (`onnx/model_quantized.onnx`
or `onnx/model.onnx`, and `vocab.txt`) into those paths, or point
`ModelPath`/`VocabPath` elsewhere.

## Calibration

`tools/Vyral.OnnxJudgeCalibration` is a console harness: given a JSONL file
of labeled examples (`{context, options: [{id, label}], correctId}`), it runs
the real model with the production hypothesis template, collects entailment
logits, and fits one temperature via `OnnxJudgeCalibrationFitter` (minimizing
negative log-likelihood by golden-section search — a real, minimal
calibration procedure, not a stand-in for retraining the model itself, which
this repo's local-first, no-training-framework posture does not attempt).

```sh
dotnet run --project tools/Vyral.OnnxJudgeCalibration -- \
  tools/Vyral.OnnxJudgeCalibration/examples/generic-topic-entailment.jsonl \
  .vyral/models/mobilebert-uncased-mnli-quantized \
  .vyral/models/mobilebert-uncased-mnli-quantized/vocab.txt \
  0 \
  .vyral/models/mobilebert-uncased-mnli-quantized/calibration.json \
  Xenova/mobilebert-uncased-mnli:model_quantized
```

The bundled example set (30 hand-labeled, domain-neutral topic-classification
examples — no case-specific or third-party data) measured 86.7% raw accuracy
and fit temperature 0.89 against the default model. That is a smoke test of
the calibration *mechanism*, not a qualification result — see
`OnnxJudgeCalibrationFitter`'s and `OnnxJudgeCalibration`'s doc comments for
what temperature scaling can and cannot fix, and `AiJudgeAnswer.Calibrated`
for how callers should treat the result either way. A different
`ChoiceHypothesisTemplate` (e.g. for citation-relation-style classification
rather than topic classification) needs its own labeled set and its own fit;
the shipped calibration is specific to the default template.

## Premise length and truncation

A premise longer than `MaxTokens` (default 512 — MobileBERT-uncased-mnli's
own `max_position_embeddings` ceiling, confirmed against its `config.json`,
not an arbitrary number) gets truncated, not rejected outright. Truncation
prioritizes the **premise**, not the hypothesis: `WordPieceTokenizer.EncodePair`
is called with `truncateFirstBeforeSecond: true` here specifically, the
opposite of the reranker's (query, document) pairing, because the hypothesis
is the part that varies per option — truncating it away first made every
option's hypothesis collapse toward nothing, so every option encoded the
same (premise-only) content and the model returned identical, uniform output
for genuinely different questions. That was a real, shipped bug
(BUG-20260918-061159-20F269): schema-valid, confidence-bearing, and wrong,
with nothing about the response shape signaling it. If a premise is long
enough that even fully discarding it still can't fit alongside the
hypothesis (a pathologically long `Prompt` embedded via a custom
`ChoiceHypothesisTemplate`, for instance), the classifier now fails closed
with a clear `InvalidOperationException` (surfaced as `Failed` /
`configuration`) instead of silently proceeding with an empty segment.

Judging passages routinely longer than ~512 tokens needs premise chunking
upstream — raising `MaxTokens` further isn't available; it's capped by what
the checkpoint's position embeddings actually support.

## Conformance

`OnnxNliJudgeConformanceTests.cs` (`tests/Vyral.Tests.Providers`) subclasses
the shared `AiJudgeProviderConformanceTests` (`Vyral.Tests.Conformance`),
gated on `VYRAL_ONNX_JUDGE_MODEL_DIR`. It proves this target satisfies the
same structural contract as the deterministic stub and the remote Jev
adapter — probability ranges, batching, id consistency, rejection behavior —
not just that it produces plausible-looking output on its own.

## Explicit non-support

- Calibration corrects confidence, not accuracy — a consistently wrong model
  can be consistently confident about being wrong; temperature scaling
  cannot detect or fix that, only whether stated confidence tracks actual
  correctness once the model's calls are fixed.

## Qualification posture

`prototype` — real model, real (small-sample) calibration harness, no
isolated live-qualification gate. See `docs/contributing/adapter-contributor.md`.
