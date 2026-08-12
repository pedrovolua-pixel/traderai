import json
import sqlite3
from datetime import datetime, timedelta, timezone
from uuid import uuid4

from fastapi.testclient import TestClient

from app.database import DATABASE_PATH
from app.main import app, get_llm_validator, get_market_context_service
from app.market_context import FmpMarketContextService
from app.models import LlmProviderDecision


NOW = datetime.now(timezone.utc).replace(microsecond=0)


def bars(minutes: int, count: int, start: float) -> list[dict]:
    values = []
    for index in range(count):
        close = start + index * 0.25
        values.append(
            {
                "timestamp": (NOW - timedelta(minutes=minutes * (count - index))).isoformat(),
                "open": close - 0.25,
                "high": close + 0.5,
                "low": close - 0.5,
                "close": close,
                "volume": 1000 + index,
            }
        )
    return values


def known(value: float) -> dict:
    return {"value": value, "known_at": (NOW - timedelta(seconds=5)).isoformat()}


def v2_payload(**overrides) -> dict:
    refresh = (NOW - timedelta(minutes=1)).isoformat()
    payload = {
        "validation_id": str(uuid4()),
        "strategy_instance_id": "MesOrbPullbackV2-test",
        "strategy": "mes_orb_pullback_v2",
        "instrument": "MES SEP26",
        "timestamp": NOW.isoformat(),
        "playback_time": NOW.isoformat(),
        "execution_mode": "playback",
        "opening_range": {
            "start": (NOW - timedelta(hours=1)).isoformat(),
            "end": (NOW - timedelta(minutes=45)).isoformat(),
            "open": 6500.0,
            "high": 6505.0,
            "low": 6495.0,
            "close": 6501.0,
        },
        "breakout": {
            "direction": "long",
            "bar_timestamp": (NOW - timedelta(minutes=30)).isoformat(),
            "close": 6506.0,
            "distance_points": 1.0,
        },
        "pullback": {
            "setup_type": "or_retest",
            "candidate_id": "or-retest-1",
            "bar_timestamp": NOW.isoformat(),
            "trigger_price": 6505.5,
            "structural_price": 6504.75,
            "quality_score": 0.82,
            "bars_since_breakout": 3,
        },
        "proposed_trade": {
            "direction": "long",
            "quantity": 1,
            "entry_reference": 6505.5,
            "stop": 6504.5,
            "target": 6507.5,
            "planned_risk": 5.0,
            "reward_multiple": 2.0,
        },
        "bars_15m": bars(15, 12, 6495),
        "bars_60m": bars(60, 12, 6475),
        "bars_240m": bars(240, 8, 6400),
        "bars_daily": bars(1440, 10, 6300),
        "market_regime": {
            "previous_day_high": known(6499),
            "previous_day_low": known(6450),
            "previous_day_close": known(6488),
            "overnight_high": known(6501),
            "overnight_low": known(6480),
            "gap_points": known(12),
            "rth_vwap": known(6501),
            "atr_14_daily": known(50),
            "relative_volume": known(1.2),
            "realized_range": known(22),
            "or_width_atr": known(0.2),
            "breakout_distance": known(1),
            "pullback_quality": known(0.82),
            "planned_risk": known(5),
        },
        "economic_events": [],
        "earnings": [],
        "headlines": [],
        "context_timestamps": {
            "market_data_known_at": (NOW - timedelta(seconds=5)).isoformat(),
            "economic_events_refreshed_at": refresh,
            "earnings_refreshed_at": refresh,
            "constituents_refreshed_at": refresh,
            "headlines_refreshed_at": refresh,
        },
        "snapshot_hash": uuid4().hex + uuid4().hex,
    }
    payload.update(overrides)
    return payload


class FakeValidator:
    def __init__(self, decision="allow", confidence=0.9, latency_ms=12):
        self.decision = decision
        self.confidence = confidence
        self.latency_ms = latency_ms
        self.calls = 0

    def validate(self, _validation):
        self.calls += 1
        return LlmProviderDecision(
            decision=self.decision,
            confidence=self.confidence,
            reason_codes=["clean_breakout_pullback"],
            summary="Point-in-time test decision.",
            provider="test",
            model="fake-terra",
            latency_ms=self.latency_ms,
        )


def test_orb_v2_allow_persists_snapshot_and_normalized_context() -> None:
    payload = v2_payload()
    validator = FakeValidator()
    app.dependency_overrides[get_llm_validator] = lambda: validator
    try:
        with TestClient(app) as client:
            response = client.post("/validate/orb-v2", json=payload)
    finally:
        app.dependency_overrides.clear()
    assert response.status_code == 200
    assert response.json()["decision"] == "allow"
    assert response.json()["snapshot_hash"] == payload["snapshot_hash"]
    assert validator.calls == 1
    with sqlite3.connect(DATABASE_PATH) as connection:
        row = connection.execute(
            "SELECT request_hash, request_payload FROM orb_v2_validations WHERE validation_id = ?",
            (payload["validation_id"],),
        ).fetchone()
    assert len(row[0]) == 64
    assert "account" not in json.loads(row[1])


def test_orb_v2_idempotency_and_snapshot_replay() -> None:
    payload = v2_payload()
    validator = FakeValidator()
    app.dependency_overrides[get_llm_validator] = lambda: validator
    try:
        with TestClient(app) as client:
            first = client.post("/validate/orb-v2", json=payload)
            duplicate = client.post("/validate/orb-v2", json=payload)
            replay_payload = {**payload, "validation_id": str(uuid4())}
            replay = client.post("/validate/orb-v2", json=replay_payload)
    finally:
        app.dependency_overrides.clear()
    assert first.status_code == duplicate.status_code == replay.status_code == 200
    assert duplicate.json()["duplicate"] is True
    assert replay.json()["provider"] == "snapshot_replay"
    assert validator.calls == 1


def test_orb_v2_same_snapshot_different_request_fails_closed() -> None:
    payload = v2_payload()
    validator = FakeValidator()
    app.dependency_overrides[get_llm_validator] = lambda: validator
    try:
        with TestClient(app) as client:
            assert client.post("/validate/orb-v2", json=payload).status_code == 200
            changed = v2_payload(snapshot_hash=payload["snapshot_hash"])
            changed["pullback"]["candidate_id"] = "different-candidate"
            changed["pullback"]["trigger_price"] = 6505.75
            changed["proposed_trade"]["entry_reference"] = 6505.75
            changed["proposed_trade"]["target"] = 6508.25
            changed["proposed_trade"]["planned_risk"] = 6.25
            response = client.post("/validate/orb-v2", json=changed)
    finally:
        app.dependency_overrides.clear()
    assert response.json()["decision"] == "reject"
    assert response.json()["reason_codes"] == ["snapshot_hash_collision"]


def test_orb_v2_low_confidence_and_slow_allow_fail_closed() -> None:
    for validator, reason in [
        (FakeValidator(confidence=0.74), "llm_confidence_below_threshold"),
        (FakeValidator(latency_ms=5001), "llm_timeout"),
    ]:
        app.dependency_overrides[get_llm_validator] = lambda validator=validator: validator
        try:
            with TestClient(app) as client:
                response = client.post("/validate/orb-v2", json=v2_payload())
        finally:
            app.dependency_overrides.clear()
        assert response.json()["decision"] == "reject"
        assert response.json()["reason_codes"] == [reason]


def test_orb_v2_rejects_future_known_at_and_bad_breakout() -> None:
    future = (NOW + timedelta(minutes=1)).isoformat()
    payload = v2_payload()
    payload["context_timestamps"]["headlines_refreshed_at"] = future
    with TestClient(app) as client:
        lookahead = client.post("/validate/orb-v2", json=payload)
        bad_breakout_payload = v2_payload()
        bad_breakout_payload["breakout"]["close"] = 6505.0
        bad_breakout = client.post("/validate/orb-v2", json=bad_breakout_payload)
    assert lookahead.status_code == 422
    assert bad_breakout.status_code == 422


def test_orb_v2_blocks_future_actuals_earnings_and_headlines() -> None:
    future = NOW + timedelta(minutes=1)
    cases = []
    economic = v2_payload()
    economic["economic_events"] = [{
        "event_id": "cpi", "name": "CPI", "country": "US",
        "release_time": (NOW - timedelta(minutes=5)).isoformat(),
        "known_at": future.isoformat(), "importance": "high", "forecast": "0.2%",
        "actual": "0.4%", "previous": "0.1%", "surprise": 0.2,
        "minutes_from_release": -5,
    }]
    cases.append(economic)
    earnings = v2_payload()
    earnings["earnings"] = [{
        "event_id": "earn-aapl", "symbol": "AAPL", "company": "Apple",
        "release_time": (NOW - timedelta(minutes=5)).isoformat(),
        "known_at": future.isoformat(), "session": "current_session",
        "estimated_index_weight": 0.07, "eps_estimate": "1.40",
        "eps_actual": "1.50", "surprise": 0.1, "minutes_from_release": -5,
    }]
    cases.append(earnings)
    headline = v2_payload()
    headline["headlines"] = [{
        "headline_id": "headline-1", "title": "Future headline", "symbols": ["AAPL"],
        "published_at": future.isoformat(), "known_at": future.isoformat(), "source": "test",
    }]
    cases.append(headline)
    with TestClient(app) as client:
        responses = [client.post("/validate/orb-v2", json=payload) for payload in cases]
    assert [response.status_code for response in responses] == [422, 422, 422]


def test_orb_v2_missing_point_in_time_archive_fails_closed() -> None:
    payload = v2_payload(
        playback_time=(NOW - timedelta(days=30)).isoformat(),
        timestamp=(NOW - timedelta(days=30)).isoformat(),
    )
    old = (NOW - timedelta(days=30, minutes=1)).isoformat()
    payload["opening_range"]["start"] = (NOW - timedelta(days=30, hours=1)).isoformat()
    payload["opening_range"]["end"] = (NOW - timedelta(days=30, minutes=45)).isoformat()
    payload["breakout"]["bar_timestamp"] = (NOW - timedelta(days=30, minutes=30)).isoformat()
    payload["pullback"]["bar_timestamp"] = (NOW - timedelta(days=30)).isoformat()
    for series in ("bars_15m", "bars_60m", "bars_240m", "bars_daily"):
        for bar in payload[series]:
            bar["timestamp"] = (datetime.fromisoformat(bar["timestamp"]) - timedelta(days=30)).isoformat()
    payload["context_timestamps"] = {
        "market_data_known_at": old,
        "economic_events_refreshed_at": None,
        "earnings_refreshed_at": None,
        "constituents_refreshed_at": None,
        "headlines_refreshed_at": None,
    }
    for metric in payload["market_regime"].values():
        metric["known_at"] = old
    with TestClient(app) as client:
        response = client.post("/validate/orb-v2", json=payload)
    assert response.status_code == 200
    assert response.json()["decision"] == "reject"
    assert response.json()["reason_codes"] == ["historical_point_in_time_context_missing"]


def test_orb_v2_allows_current_price_action_only_llm_validation_without_fmp() -> None:
    payload = v2_payload()
    validator = FakeValidator()
    context_service = FmpMarketContextService(api_key="")
    app.dependency_overrides[get_llm_validator] = lambda: validator
    app.dependency_overrides[get_market_context_service] = lambda: context_service
    try:
        with TestClient(app) as client:
            response = client.post("/validate/orb-v2", json=payload)
            status = client.get("/context/status")
    finally:
        app.dependency_overrides.clear()
        context_service.stop()
    assert response.status_code == 200
    assert response.json()["decision"] == "allow"
    assert validator.calls == 1
    assert status.json()["status"] == "disabled"
    assert status.json()["provider"] == "disabled"


def test_context_status_redacts_provider_credentials() -> None:
    with TestClient(app) as client:
        response = client.get("/context/status")
    assert response.status_code == 200
    assert response.json()["provider"] in {"fmp", "disabled"}
    assert "api_key" not in response.text.lower()
