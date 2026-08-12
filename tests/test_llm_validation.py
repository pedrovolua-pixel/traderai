import json
import sqlite3
from datetime import datetime, timedelta, timezone
from uuid import uuid4

import httpx
from fastapi.testclient import TestClient

from app.database import DATABASE_PATH
from app.llm_validator import DisabledLlmValidator, OpenAIResponsesValidator
from app.main import app, get_llm_validator
from app.models import LlmProviderDecision, LlmValidationIn


def _bars(interval_minutes: int, count: int, start: float) -> list[dict]:
    now = datetime.now(timezone.utc).replace(second=0, microsecond=0)
    bars: list[dict] = []
    for index in range(count):
        close = start + index * 0.25
        bars.append(
            {
                "timestamp": (now - timedelta(minutes=interval_minutes * (count - index))).isoformat(),
                "open": close - 0.25,
                "high": close + 0.5,
                "low": close - 0.5,
                "close": close,
                "volume": 100 + index,
            }
        )
    return bars


def validation_payload(**overrides) -> dict:
    values = {
        "validation_id": str(uuid4()),
        "strategy_instance_id": "MesOrbStructureV1-test-instance",
        "strategy": "mes_orb_structure_v1",
        "instrument": "MES SEP26",
        "timestamp": datetime.now(timezone.utc).isoformat(),
        "execution_mode": "playback",
        "direction": "long",
        "quantity": 1,
        "opening_range_high": 6500.0,
        "opening_range_low": 6490.0,
        "breakout_close": 6501.0,
        "entry_reference": 6501.0,
        "stop": 6493.0,
        "target": 6517.0,
        "planned_risk": 40.0,
        "reward_multiple": 2.0,
        "structure_direction": "bullish",
        "confirmed_swing_highs": [6498.0, 6500.0],
        "confirmed_swing_lows": [6491.0, 6493.25],
        "primary_bars": _bars(1, 12, 6497.0),
        "structure_bars": _bars(5, 12, 6488.0),
    }
    values.update(overrides)
    return values


class FakeValidator:
    def __init__(self, decision: str = "allow") -> None:
        self.decision = decision
        self.calls = 0

    def validate(self, validation: LlmValidationIn) -> LlmProviderDecision:
        self.calls += 1
        return LlmProviderDecision(
            decision=self.decision,
            confidence=0.84 if self.decision == "allow" else 0.91,
            reason_codes=["clean_breakout" if self.decision == "allow" else "choppy_breakout"],
            summary="Deterministic test decision.",
            provider="test",
            model="fake-validator",
            latency_ms=7,
        )


def test_llm_validation_allow_round_trip() -> None:
    payload = validation_payload()
    validator = FakeValidator("allow")
    app.dependency_overrides[get_llm_validator] = lambda: validator
    try:
        with TestClient(app) as client:
            response = client.post("/validate", json=payload)
    finally:
        app.dependency_overrides.clear()

    assert response.status_code == 200
    body = response.json()
    assert body["decision"] == "allow"
    assert body["reason_codes"] == ["clean_breakout"]
    assert body["duplicate"] is False
    assert validator.calls == 1

    with sqlite3.connect(DATABASE_PATH) as connection:
        row = connection.execute(
            """
            SELECT decision, provider, model, request_payload
            FROM llm_validations WHERE validation_id = ?
            """,
            (payload["validation_id"],),
        ).fetchone()
    assert row[:3] == ("allow", "test", "fake-validator")
    assert "account" not in json.loads(row[3])


def test_llm_validation_duplicate_is_idempotent() -> None:
    payload = validation_payload()
    validator = FakeValidator("reject")
    app.dependency_overrides[get_llm_validator] = lambda: validator
    try:
        with TestClient(app) as client:
            first = client.post("/validate", json=payload)
            second = client.post("/validate", json=payload)
    finally:
        app.dependency_overrides.clear()

    assert first.status_code == 200
    assert second.status_code == 200
    assert first.json()["validation_db_id"] == second.json()["validation_db_id"]
    assert second.json()["duplicate"] is True
    assert validator.calls == 1


def test_llm_validation_rejects_bad_geometry_and_account_field() -> None:
    invalid = validation_payload(stop=6502.0, account="Sim101")
    with TestClient(app) as client:
        response = client.post("/validate", json=invalid)
    assert response.status_code == 422


def test_disabled_validator_fails_closed() -> None:
    payload = validation_payload()
    app.dependency_overrides[get_llm_validator] = lambda: DisabledLlmValidator()
    try:
        with TestClient(app) as client:
            response = client.post("/validate", json=payload)
    finally:
        app.dependency_overrides.clear()

    assert response.status_code == 200
    assert response.json()["decision"] == "reject"
    assert response.json()["reason_codes"] == ["openai_api_key_missing"]


def test_openai_responses_validator_parses_strict_output() -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        sent = json.loads(request.content)
        assert sent["store"] is False
        assert sent["text"]["format"]["strict"] is True
        return httpx.Response(
            200,
            json={
                "status": "completed",
                "output": [
                    {
                        "type": "message",
                        "content": [
                            {
                                "type": "output_text",
                                "text": json.dumps(
                                    {
                                        "decision": "allow",
                                        "confidence": 0.77,
                                        "reason_codes": ["aligned_structure"],
                                        "summary": "Structure and breakout agree.",
                                    }
                                ),
                            }
                        ],
                    }
                ],
            },
        )

    validator = OpenAIResponsesValidator(
        api_key="test-key",
        client=httpx.Client(transport=httpx.MockTransport(handler)),
    )
    decision = validator.validate(LlmValidationIn.model_validate(validation_payload()))
    assert decision.decision == "allow"
    assert decision.provider == "openai"
    assert decision.reason_codes == ["aligned_structure"]
