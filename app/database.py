import sqlite3
from datetime import datetime, timezone
from pathlib import Path

from app.models import SignalIn
from risk.engine import RiskReview


PROJECT_ROOT = Path(__file__).resolve().parent.parent
DATABASE_PATH = PROJECT_ROOT / "database" / "traderai.db"


def connect() -> sqlite3.Connection:
    connection = sqlite3.connect(DATABASE_PATH, timeout=10)
    connection.row_factory = sqlite3.Row
    connection.execute("PRAGMA foreign_keys = ON")
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
        existing_columns = {
            row["name"] for row in connection.execute("PRAGMA table_info(signals)")
        }
        migrations = {
            "entry": "REAL",
            "stop": "REAL",
            "target1": "REAL",
            "target2": "REAL",
            "ema20": "REAL",
            "ema50": "REAL",
            "vwap": "REAL",
            "atr": "REAL",
            "recent_swing_low": "REAL",
            "regime": "TEXT",
            "setup_state": "TEXT",
            "raw_rr": "REAL",
            "risk_status": "TEXT",
        }
        for column, sql_type in migrations.items():
            if column not in existing_columns:
                connection.execute(
                    f"ALTER TABLE signals ADD COLUMN {column} {sql_type}"
                )
        connection.execute(
            """
            CREATE TABLE IF NOT EXISTS risk_reviews (
                risk_review_id INTEGER PRIMARY KEY AUTOINCREMENT,
                signal_id INTEGER NOT NULL,
                accepted INTEGER NOT NULL,
                rejection_reason TEXT,
                risk_score REAL,
                reviewed_at TEXT NOT NULL,
                FOREIGN KEY (signal_id) REFERENCES signals(signal_id)
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
                price, timestamp, received_at, entry, stop, target1, target2,
                ema20, ema50, vwap, atr, recent_swing_low, regime,
                setup_state, raw_rr, risk_status
            ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            """,
            (
                signal.instrument,
                signal.strategy,
                signal.direction,
                signal.timeframe,
                signal.price,
                signal.timestamp.isoformat(),
                received_at,
                signal.entry,
                signal.stop,
                signal.target1,
                signal.target2,
                signal.ema20,
                signal.ema50,
                signal.vwap,
                signal.atr,
                signal.recent_swing_low,
                signal.regime,
                signal.setup_state,
                signal.raw_rr,
                "pending",
            ),
        )
        return int(cursor.lastrowid)


def has_recent_duplicate(
    signal: SignalIn, signal_id: int, cooldown_minutes: int
) -> bool:
    cutoff = datetime.now(timezone.utc).timestamp() - cooldown_minutes * 60
    with connect() as connection:
        rows = connection.execute(
            """
            SELECT received_at FROM signals
            WHERE signal_id <> ? AND instrument = ? AND strategy = ? AND direction = ?
            """,
            (signal_id, signal.instrument, signal.strategy, signal.direction),
        ).fetchall()
    return any(datetime.fromisoformat(row["received_at"]).timestamp() >= cutoff for row in rows)


def persist_risk_review(signal_id: int, review: RiskReview) -> None:
    reviewed_at = datetime.now(timezone.utc).isoformat()
    status = "accepted" if review.accepted else "rejected"
    with connect() as connection:
        connection.execute(
            """
            INSERT INTO risk_reviews (
                signal_id, accepted, rejection_reason, risk_score, reviewed_at
            ) VALUES (?, ?, ?, ?, ?)
            """,
            (
                signal_id,
                int(review.accepted),
                review.rejection_reason,
                review.risk_score,
                reviewed_at,
            ),
        )
        connection.execute(
            "UPDATE signals SET risk_status = ? WHERE signal_id = ?",
            (status, signal_id),
        )
