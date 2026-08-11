import sqlite3
from datetime import datetime, timezone
from pathlib import Path

from app.models import SignalIn


PROJECT_ROOT = Path(__file__).resolve().parent.parent
DATABASE_PATH = PROJECT_ROOT / "database" / "traderai.db"


def connect() -> sqlite3.Connection:
    connection = sqlite3.connect(DATABASE_PATH, timeout=10)
    connection.row_factory = sqlite3.Row
    return connection


def initialize_database() -> None:
    DATABASE_PATH.parent.mkdir(parents=True, exist_ok=True)
    with connect() as connection:
        connection.execute(
            """
            CREATE TABLE IF NOT EXISTS signals (
                signal_id INTEGER PRIMARY KEY AUTOINCREMENT,
                instrument TEXT NOT NULL,
                strategy TEXT NOT NULL,
                direction TEXT NOT NULL,
                timeframe TEXT NOT NULL,
                price REAL NOT NULL,
                timestamp TEXT NOT NULL,
                received_at TEXT NOT NULL
            )
            """
        )


def insert_signal(signal: SignalIn) -> int:
    received_at = datetime.now(timezone.utc).isoformat()
    with connect() as connection:
        cursor = connection.execute(
            """
            INSERT INTO signals (
                instrument, strategy, direction, timeframe,
                price, timestamp, received_at
            ) VALUES (?, ?, ?, ?, ?, ?, ?)
            """,
            (
                signal.instrument,
                signal.strategy,
                signal.direction,
                signal.timeframe,
                signal.price,
                signal.timestamp.isoformat(),
                received_at,
            ),
        )
        return int(cursor.lastrowid)

