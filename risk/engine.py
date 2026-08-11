from dataclasses import dataclass

from app.models import SignalIn
from config.risk_config import MAX_STOP_ATR_MULTIPLE, MIN_RAW_RR, MIN_STOP_ATR_MULTIPLE


@dataclass(frozen=True)
class RiskReview:
    accepted: bool
    rejection_reason: str | None
    risk_score: float
    stop_distance: float | None = None


REQUIRED_SETUP_FIELDS = (
    "entry",
    "stop",
    "target1",
    "target2",
    "ema20",
    "ema50",
    "atr",
    "recent_swing_low",
    "regime",
    "setup_state",
    "raw_rr",
)


def reject(reason: str, stop_distance: float | None = None) -> RiskReview:
    return RiskReview(False, reason, 0.0, stop_distance)


def review_signal(signal: SignalIn, duplicate_found: bool = False) -> RiskReview:
    missing = [name for name in REQUIRED_SETUP_FIELDS if getattr(signal, name) is None]
    if missing:
        return reject("missing_required_fields:" + ",".join(missing))

    assert signal.entry is not None
    assert signal.stop is not None
    assert signal.atr is not None
    assert signal.raw_rr is not None
    assert signal.ema20 is not None
    assert signal.ema50 is not None

    if signal.direction == "long" and signal.entry <= signal.stop:
        return reject("entry_not_above_stop")

    stop_distance = abs(signal.entry - signal.stop)
    if stop_distance <= 0:
        return reject("stop_distance_not_positive", stop_distance)
    if signal.atr <= 0:
        return reject("atr_not_positive", stop_distance)
    if stop_distance < MIN_STOP_ATR_MULTIPLE * signal.atr:
        return reject("stop_distance_lt_0_25_atr", stop_distance)
    if stop_distance > MAX_STOP_ATR_MULTIPLE * signal.atr:
        return reject("stop_distance_gt_3_atr", stop_distance)
    if signal.raw_rr < MIN_RAW_RR:
        return reject("raw_rr_below_minimum", stop_distance)
    if signal.strategy == "trend_pullback_v1" and signal.direction == "long":
        if signal.ema20 <= signal.ema50:
            return reject("ema20_not_above_ema50", stop_distance)
    if duplicate_found:
        return reject("duplicate_within_cooldown", stop_distance)

    return RiskReview(True, None, 1.0, stop_distance)

