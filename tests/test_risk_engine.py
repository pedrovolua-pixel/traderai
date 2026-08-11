from datetime import datetime, timezone

import pytest

from app.models import SignalIn
from risk.engine import review_signal


def valid_signal(**overrides) -> SignalIn:
    values = {
        "instrument": "MNQ SEP26",
        "strategy": "trend_pullback_v1",
        "direction": "long",
        "timeframe": "15Minute",
        "price": 20000.0,
        "timestamp": datetime.now(timezone.utc),
        "entry": 20000.0,
        "stop": 19950.0,
        "target1": 20050.0,
        "target2": 20100.0,
        "ema20": 19980.0,
        "ema50": 19900.0,
        "vwap": None,
        "atr": 40.0,
        "recent_swing_low": 19960.0,
        "regime": "bullish",
        "setup_state": "qualified",
        "raw_rr": 2.0,
    }
    values.update(overrides)
    return SignalIn(**values)


def test_valid_long_setup_accepted() -> None:
    assert review_signal(valid_signal()).accepted


@pytest.mark.parametrize(
    ("overrides", "reason"),
    [
        ({"ema20": 19900.0, "ema50": 19900.0}, "ema20_not_above_ema50"),
        ({"entry": 19950.0, "stop": 19950.0}, "entry_not_above_stop"),
        ({"raw_rr": 1.49}, "raw_rr_below_minimum"),
        ({"stop": 19995.0}, "stop_distance_lt_0_25_atr"),
        ({"stop": 19800.0}, "stop_distance_gt_3_atr"),
    ],
)
def test_invalid_setup_rejected(overrides: dict, reason: str) -> None:
    review = review_signal(valid_signal(**overrides))
    assert not review.accepted
    assert review.rejection_reason == reason


def test_duplicate_signal_rejected() -> None:
    review = review_signal(valid_signal(), duplicate_found=True)
    assert not review.accepted
    assert review.rejection_reason == "duplicate_within_cooldown"
