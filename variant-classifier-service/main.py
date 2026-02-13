"""Variant classifier inference service.

Serves a trained roberta-large cross-encoder model via FastAPI.
Accepts batches of listing pairs and returns same/different verdicts
with confidence scores.

Usage:
    uvicorn main:app --port 8010
    python main.py
"""

import os
import logging
from contextlib import asynccontextmanager

import torch
from fastapi import FastAPI
from pydantic import BaseModel
from transformers import AutoTokenizer, AutoModelForSequenceClassification

logger = logging.getLogger("variant-classifier")

MODEL_PATH = os.environ.get(
    "MODEL_PATH",
    "E:/Dev/ml-training/variant-classifier/model_v6_lr1e5",
)
MAX_LENGTH = int(os.environ.get("MAX_LENGTH", "256"))
CONFIDENCE_THRESHOLD = float(os.environ.get("CONFIDENCE_THRESHOLD", "0.80"))

model = None
tokenizer = None


class ListingPair(BaseModel):
    title_a: str
    description_a: str
    title_b: str
    description_b: str


class ClassifyRequest(BaseModel):
    pairs: list[ListingPair]


class PairResult(BaseModel):
    is_comparable: bool
    confidence: float
    needs_fallback: bool


class ClassifyResponse(BaseModel):
    results: list[PairResult]


@asynccontextmanager
async def lifespan(app: FastAPI):
    global model, tokenizer
    logger.info("Loading model from %s", MODEL_PATH)
    tokenizer = AutoTokenizer.from_pretrained(MODEL_PATH)
    model = AutoModelForSequenceClassification.from_pretrained(MODEL_PATH)
    model.eval()
    logger.info("Model loaded successfully")
    yield


app = FastAPI(title="Variant Classifier", lifespan=lifespan)


@app.get("/health")
def health():
    return {"status": "healthy", "model_loaded": model is not None}


@app.post("/classify", response_model=ClassifyResponse)
def classify(request: ClassifyRequest):
    texts_a = [f"{p.title_a} | {p.description_a}" for p in request.pairs]
    texts_b = [f"{p.title_b} | {p.description_b}" for p in request.pairs]

    inputs = tokenizer(
        texts_a,
        texts_b,
        return_tensors="pt",
        max_length=MAX_LENGTH,
        truncation=True,
        padding=True,
    )

    with torch.no_grad():
        logits = model(**inputs).logits
        probs = torch.softmax(logits, dim=-1)

    results = []
    for i in range(len(request.pairs)):
        p_same = probs[i][1].item()
        p_different = probs[i][0].item()
        confidence = max(p_same, p_different)
        is_comparable = p_same > p_different

        results.append(
            PairResult(
                is_comparable=is_comparable,
                confidence=round(confidence, 4),
                needs_fallback=confidence < CONFIDENCE_THRESHOLD,
            )
        )

    return ClassifyResponse(results=results)


if __name__ == "__main__":
    import uvicorn

    uvicorn.run(app, host="0.0.0.0", port=8010)
