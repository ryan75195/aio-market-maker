# Model Experiments

Chronological record of ML model experiments for the variant classifier pipeline. Each document captures the hypothesis, approach, results, and lessons learned.

## Experiment Timeline

| Date | Document | Model | Outcome |
|------|----------|-------|---------|
| 2026-02-07 | [v5 BERT Cross-Encoder](2026-02-07-variant-classifier-v5-bert.md) | bert-base-uncased (110M) | F1=0.871. Proved cross-encoder approach works, but needs more data |
| 2026-02-18 | [Evaluator LLM](2026-02-18-evaluator-llm-experiment.md) | Qwen3-8B/0.6B | Failed. 4.6% misclassification recall, too slow for production |
| 2026-02-22 | [Ensemble Calibration](2026-02-22-ensemble-calibration.md) | Logistic regression on ONNX + similarity | 21% NLL improvement, calibration at 0.90-0.95: 71%->91% |
| 2026-03-01 | [Qwen3-4B Pre-Training Analysis](2026-03-01-qwen3-4b-pre-training.md) | Qwen3-4B (QLoRA) | Design doc. Zero-shot: 55%, after 400 examples: 67% |
| 2026-03-01 | [Qwen3-4B Post-Mortem](2026-03-01-qwen3-4b-post-mortem.md) | Qwen3-4B (QLoRA) | Failed. 62% accuracy (target: 90%). Root cause: flawed training labels |
| 2026-03-01 | [LLM Classifier Integration](2026-03-01-llm-classifier-integration.md) | Qwen3-4B via llama.cpp | Not executed. Blocked by Phase 1 accuracy gate |
| 2026-03-01 | [LLM Classifier Integration Plan](2026-03-01-llm-classifier-integration-plan.md) | (implementation plan) | Not executed. Companion to integration design |

## Training Version History

Detailed training logs for each classifier version live with the training scripts per convention:

| Version | Location | Model | F1 |
|---------|----------|-------|----|
| v5 | `AIOMarketMaker.ML/Training/v5/README.md` | bert-base-uncased | 0.871 |
| v6 | `AIOMarketMaker.ML/Training/v6/README.md` | roberta-large | 0.903 |
| v7 | `AIOMarketMaker.ML/Training/v7/README.md` | roberta-large | 0.920 |
| v8 | `AIOMarketMaker.ML/Training/v8/README.md` | roberta-large | 0.913 |
| v9 | `AIOMarketMaker.ML/Training/v9/README.md` | roberta-large | (corrections only) |

## Production Model

The current production classifier is **v10** (RoBERTa-large, ONNX), which is v8 re-exported. See `AIOMarketMaker.ML/Training/CONVENTIONS.md` for training directory structure.

## Key Lessons (Cross-Cutting)

1. **Data quality is the ceiling.** Model capacity (4B params) doesn't help when 79% of labels encode the wrong rules (Qwen3 post-mortem).
2. **Labeling prompt = model behavior.** GPT-generated labels inherit every assumption in the prompt (Qwen3 post-mortem).
3. **Independent models carry complementary signals.** RoBERTa + OpenAI embeddings improve calibration even without retraining (ensemble experiment).
4. **Generative LLMs are too slow for classification.** Token-by-token generation is 50-100x slower than a single forward pass (evaluator experiment).
5. **Validate a sample before bulk labeling.** 100 manually reviewed pairs would have caught the condition blindness issue for $0.02 (Qwen3 post-mortem).
