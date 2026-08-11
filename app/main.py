import logging
from contextlib import asynccontextmanager

from fastapi import FastAPI

from app.database import (
    has_recent_duplicate,
    initialize_database,
    insert_signal,
    persist_risk_review,
)
from app.models import SignalAccepted, SignalIn
from config.risk_config import COOLDOWN_MINUTES
from risk.engine import review_signal


logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s %(levelname)s %(name)s: %(message)s",
)
logger = logging.getLogger("traderai.bridge")


@asynccontextmanager
async def lifespan(_: FastAPI):
    initialize_database()
    logger.info("TraderAI signal bridge ready on 127.0.0.1")
    yield


app = FastAPI(title="TraderAI Signal Bridge", version="0.1.0", lifespan=lifespan)


@app.get("/health")
def health() -> dict[str, str]:
    return {"status": "ok"}


@app.post("/signal", response_model=SignalAccepted)
def receive_signal(signal: SignalIn) -> SignalAccepted:
    signal_id = insert_signal(signal)
    logger.info(
        "SIGNAL RECEIVED: Signal #%s %s %s %s received",
        signal_id,
        signal.instrument,
        signal.direction.upper(),
        signal.strategy,
    )
    duplicate = has_recent_duplicate(signal, signal_id, COOLDOWN_MINUTES)
    review = review_signal(signal, duplicate_found=duplicate)
    persist_risk_review(signal_id, review)

    if review.accepted:
        logger.info(
            "RISK ACCEPTED #%s RR=%s ATR=%s StopDistance=%s",
            signal_id,
            signal.raw_rr,
            signal.atr,
            review.stop_distance,
        )
    else:
        logger.info(
            "RISK REJECTED #%s Reason=%s",
            signal_id,
            review.rejection_reason,
        )

    return SignalAccepted(
        signal_id=signal_id,
        risk_status="accepted" if review.accepted else "rejected",
        risk_reason=review.rejection_reason,
    )
