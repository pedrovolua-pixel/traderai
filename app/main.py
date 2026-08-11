import logging
from contextlib import asynccontextmanager

from fastapi import FastAPI

from app.database import initialize_database, insert_signal
from app.models import SignalAccepted, SignalIn


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
        "Signal received #%s: %s %s %s",
        signal_id,
        signal.instrument,
        signal.direction.upper(),
        signal.strategy,
    )
    return SignalAccepted(signal_id=signal_id)

