import sqlite3
from datetime import datetime, timezone

from fastapi.testclient import TestClient

from app.database import DATABASE_PATH
from app.main import app


def test_health_and_signal_round_trip() -> None:
    payload = {
        "instrument": "BTCUSD",
        "strategy": "connection_test",
        "direction": "long",
        "timeframe": "5m",
        "price": 120000.0,
        "timestamp": datetime.now(timezone.utc).isoformat(),
    }

    with TestClient(app) as client:
        assert client.get("/health").json() == {"status": "ok"}
        response = client.post("/signal", json=payload)

    assert response.status_code == 200
    signal_id = response.json()["signal_id"]
    with sqlite3.connect(DATABASE_PATH) as connection:
        row = connection.execute(
            "SELECT instrument, direction, strategy FROM signals WHERE signal_id = ?",
            (signal_id,),
        ).fetchone()
    assert row == ("BTCUSD", "long", "connection_test")


def test_invalid_direction_is_rejected() -> None:
    payload = {
        "instrument": "BTCUSD",
        "strategy": "connection_test",
        "direction": "flat",
        "timeframe": "5m",
        "price": 120000.0,
        "timestamp": datetime.now(timezone.utc).isoformat(),
    }

    with TestClient(app) as client:
        response = client.post("/signal", json=payload)

    assert response.status_code == 422

