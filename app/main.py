import logging
import json
import hashlib
from contextlib import asynccontextmanager
from typing import Annotated

from fastapi import Depends, FastAPI

from app.database import (
    copy_orb_v2_snapshot_decision,
    get_orb_v2_decision_by_snapshot,
    get_orb_v2_validation,
    has_recent_duplicate,
    get_llm_validation,
    initialize_database,
    insert_orb_v2_validation_request,
    insert_llm_validation_request,
    insert_signal,
    insert_trade_event,
    persist_orb_v2_decision,
    persist_llm_validation_decision,
    persist_risk_review,
    record_orb_v2_invalidation,
)
from app.llm_validator import LlmValidator, build_llm_validator_from_environment
from app.market_context import (
    ContextUnavailable,
    FmpMarketContextService,
    build_market_context_service_from_environment,
)
from app.models import (
    ContextStatus,
    LlmProviderDecision,
    LlmValidationAccepted,
    LlmValidationIn,
    MesOrbPullbackV2Accepted,
    MesOrbPullbackV2ValidationIn,
    SignalAccepted,
    SignalIn,
    TradeEventAccepted,
    TradeEventIn,
)
from config.risk_config import COOLDOWN_MINUTES
from risk.engine import review_signal


logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s %(levelname)s %(name)s: %(message)s",
)
logger = logging.getLogger("traderai.bridge")
llm_validator = build_llm_validator_from_environment()
market_context_service = build_market_context_service_from_environment()


def get_llm_validator() -> LlmValidator:
    return llm_validator


def get_market_context_service() -> FmpMarketContextService:
    return market_context_service


@asynccontextmanager
async def lifespan(_: FastAPI):
    initialize_database()
    market_context_service.start()
    logger.info("TraderAI signal bridge ready on 127.0.0.1")
    yield
    market_context_service.stop()


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


@app.post("/events", response_model=TradeEventAccepted)
def receive_trade_event(event: TradeEventIn) -> TradeEventAccepted:
    event_db_id, duplicate = insert_trade_event(event)
    logger.info(
        "TRADE EVENT: %s %s %s duplicate=%s",
        event.event_type,
        event.instrument,
        event.strategy_instance_id,
        duplicate,
    )
    return TradeEventAccepted(event_db_id=event_db_id, duplicate=duplicate)


def _validation_response(row, duplicate: bool) -> LlmValidationAccepted:
    return LlmValidationAccepted(
        validation_db_id=int(row["validation_db_id"]),
        duplicate=duplicate,
        validation_id=row["validation_id"],
        decision=row["decision"],
        confidence=row["confidence"],
        reason_codes=json.loads(row["reason_codes"]),
        summary=row["summary"],
        provider=row["provider"],
        model=row["model"],
        latency_ms=row["latency_ms"],
    )


def _orb_v2_response(row, duplicate: bool) -> MesOrbPullbackV2Accepted:
    return MesOrbPullbackV2Accepted(
        validation_db_id=int(row["validation_db_id"]),
        duplicate=duplicate,
        validation_id=row["validation_id"],
        decision=row["decision"],
        confidence=row["confidence"],
        reason_codes=json.loads(row["reason_codes"]),
        summary=row["summary"],
        provider=row["provider"],
        model=row["model"],
        latency_ms=row["latency_ms"],
        stop_loss=row["recommended_stop"],
        take_profit=row["recommended_target"],
        snapshot_hash=row["snapshot_hash"],
        decided_at=row["decided_at"],
    )


@app.post("/validate", response_model=LlmValidationAccepted)
def validate_entry_candidate(
    validation: LlmValidationIn,
    validator: Annotated[LlmValidator, Depends(get_llm_validator)],
) -> LlmValidationAccepted:
    validation_db_id, duplicate = insert_llm_validation_request(validation)
    existing = get_llm_validation(validation.validation_id)
    if duplicate and existing is not None and existing["decision"] is not None:
        return _validation_response(existing, duplicate=True)

    try:
        decision = validator.validate(validation)
    except Exception as exc:
        logger.exception("LLM validator raised unexpectedly")
        decision = LlmProviderDecision(
            decision="reject",
            confidence=0,
            reason_codes=["llm_validator_exception"],
            summary=f"LLM validator failed closed ({type(exc).__name__}).",
            provider="bridge",
            model="none",
            latency_ms=0,
        )
    persist_llm_validation_decision(validation.validation_id, decision)
    row = get_llm_validation(validation.validation_id)
    if row is None:
        raise RuntimeError("persisted validation could not be reloaded")
    logger.info(
        "LLM VALIDATION: %s %s confidence=%.2f reasons=%s duplicate=%s",
        validation.validation_id,
        decision.decision.upper(),
        decision.confidence,
        ",".join(decision.reason_codes),
        duplicate,
    )
    return _validation_response(row, duplicate=duplicate)


@app.get("/context/status", response_model=ContextStatus)
def context_status(
    service: Annotated[FmpMarketContextService, Depends(get_market_context_service)],
) -> ContextStatus:
    return service.status()


@app.post("/validate/orb-v2", response_model=MesOrbPullbackV2Accepted)
def validate_orb_v2_candidate(
    validation: MesOrbPullbackV2ValidationIn,
    validator: Annotated[LlmValidator, Depends(get_llm_validator)],
    context_service: Annotated[
        FmpMarketContextService, Depends(get_market_context_service)
    ],
) -> MesOrbPullbackV2Accepted:
    existing = get_orb_v2_validation(validation.validation_id)
    if existing is not None and existing["decision"] is not None:
        return _orb_v2_response(existing, duplicate=True)

    context_error: str | None = None
    try:
        complete = context_service.enrich(validation)
    except ContextUnavailable as exc:
        complete = validation
        context_error = str(exc)

    canonical = complete.model_dump_json(exclude={"validation_id"})
    request_hash = hashlib.sha256(canonical.encode("utf-8")).hexdigest()
    _, duplicate = insert_orb_v2_validation_request(complete, request_hash)

    if duplicate:
        row = get_orb_v2_validation(complete.validation_id)
        if row is not None and row["decision"] is not None:
            return _orb_v2_response(row, duplicate=True)

    if context_error:
        decision = LlmProviderDecision(
            decision="reject",
            confidence=0,
            reason_codes=[context_error],
            summary="Required point-in-time market context is unavailable; candidate failed closed.",
            provider="context",
            model="none",
            latency_ms=0,
        )
        record_orb_v2_invalidation(
            complete.validation_id, complete.snapshot_hash, context_error
        )
        persist_orb_v2_decision(complete.validation_id, decision)
        row = get_orb_v2_validation(complete.validation_id)
        if row is None:
            raise RuntimeError("persisted V2 validation could not be reloaded")
        return _orb_v2_response(row, duplicate=duplicate)

    replay = get_orb_v2_decision_by_snapshot(complete.snapshot_hash)
    if replay is not None and replay["validation_id"] != complete.validation_id:
        if replay["request_hash"] == request_hash:
            copy_orb_v2_snapshot_decision(replay, complete.validation_id)
            row = get_orb_v2_validation(complete.validation_id)
            if row is None:
                raise RuntimeError("replayed V2 validation could not be reloaded")
            return _orb_v2_response(row, duplicate=False)
        decision = LlmProviderDecision(
            decision="reject",
            confidence=0,
            reason_codes=["snapshot_hash_collision"],
            summary="Snapshot hash was reused for a different immutable request.",
            provider="bridge",
            model="none",
            latency_ms=0,
        )
        record_orb_v2_invalidation(
            complete.validation_id, complete.snapshot_hash, "snapshot_hash_collision"
        )
    else:
        try:
            decision = validator.validate(complete)
        except Exception as exc:
            logger.exception("V2 LLM validator raised unexpectedly")
            decision = LlmProviderDecision(
                decision="reject",
                confidence=0,
                reason_codes=["llm_validator_exception"],
                summary=f"LLM validator failed closed ({type(exc).__name__}).",
                provider="bridge",
                model="none",
                latency_ms=0,
            )
        if decision.decision == "allow" and decision.confidence < 0.75:
            decision = LlmProviderDecision(
                decision="reject",
                confidence=decision.confidence,
                reason_codes=["llm_confidence_below_threshold"],
                summary="Model allow confidence was below the required 0.75 threshold.",
                provider=decision.provider,
                model=decision.model,
                latency_ms=decision.latency_ms,
            )
        if decision.latency_ms > 5000:
            decision = LlmProviderDecision(
                decision="reject",
                confidence=0,
                reason_codes=["llm_timeout"],
                summary="Model decision exceeded the five-second validation budget.",
                provider=decision.provider,
                model=decision.model,
                latency_ms=decision.latency_ms,
            )

    if decision.decision == "reject":
        for reason in decision.reason_codes:
            record_orb_v2_invalidation(
                complete.validation_id, complete.snapshot_hash, reason
            )
    persist_orb_v2_decision(complete.validation_id, decision)
    row = get_orb_v2_validation(complete.validation_id)
    if row is None:
        raise RuntimeError("persisted V2 validation could not be reloaded")
    logger.info(
        "ORB V2 VALIDATION: %s %s confidence=%.2f latency=%sms",
        complete.validation_id,
        decision.decision.upper(),
        decision.confidence,
        decision.latency_ms,
    )
    return _orb_v2_response(row, duplicate=duplicate)
