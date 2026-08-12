import sqlite3
from datetime import datetime, timezone
from uuid import uuid4

from fastapi.testclient import TestClient

from app.database import DATABASE_PATH
from app.main import app


def event_payload(**overrides):
    values = {
        "event_id": str(uuid4()),
        "strategy_instance_id": "MesOrbStructureV1-test-instance",
        "event_type": "setup_armed",
        "strategy": "mes_orb_structure_v1",
        "instrument": "MES SEP26",
        "timestamp": datetime.now(timezone.utc).isoformat(),
        "execution_mode": "simulation",
        "direction": "long",
        "quantity": 1,
        "price": 6500.0,
        "entry": 6500.0,
        "stop": 6492.0,
        "target": 6516.0,
        "planned_risk": 40.0,
        "realized_pnl": None,
        "reason_code": None,
    }
    values.update(overrides)
    return values


def test_trade_event_round_trip() -> None:
    payload = event_payload()

    with TestClient(app) as client:
        response = client.post("/events", json=payload)

    assert response.status_code == 200
    body = response.json()
    assert body["accepted"] is True
    assert body["duplicate"] is False

    with sqlite3.connect(DATABASE_PATH) as connection:
        row = connection.execute(
            """
            SELECT event_type, execution_mode, quantity, planned_risk
            FROM trade_events WHERE event_id = ?
            """,
            (payload["event_id"],),
        ).fetchone()
    assert row == ("setup_armed", "simulation", 1, 40.0)


def test_duplicate_event_is_idempotent() -> None:
    payload = event_payload(event_type="entry_submitted")

    with TestClient(app) as client:
        first = client.post("/events", json=payload)
        second = client.post("/events", json=payload)

    assert first.status_code == 200
    assert second.status_code == 200
    assert first.json()["event_db_id"] == second.json()["event_db_id"]
    assert first.json()["duplicate"] is False
    assert second.json()["duplicate"] is True


def test_trade_event_rejects_unknown_fields() -> None:
    payload = event_payload(unredacted_account="not-allowed")

    with TestClient(app) as client:
        response = client.post("/events", json=payload)

    assert response.status_code == 422


def test_trade_event_rejects_invalid_event_type() -> None:
    payload = event_payload(event_type="order_magic")

    with TestClient(app) as client:
        response = client.post("/events", json=payload)

    assert response.status_code == 422


def test_trade_event_rejects_naive_timestamp() -> None:
    payload = event_payload(timestamp="2026-08-11T10:00:00")

    with TestClient(app) as client:
        response = client.post("/events", json=payload)

    assert response.status_code == 422


def test_trade_event_rejects_quantity_over_one() -> None:
    payload = event_payload(quantity=2)

    with TestClient(app) as client:
        response = client.post("/events", json=payload)

    assert response.status_code == 422
