import json
import sqlite3
from datetime import datetime, timezone
from pathlib import Path

from app.models import (
    LlmProviderDecision,
    LlmValidationIn,
    MesOrbPullbackV2ValidationIn,
    SignalIn,
    TradeEventIn,
)
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
        connection.execute(
            """
            CREATE TABLE IF NOT EXISTS trade_events (
                event_db_id INTEGER PRIMARY KEY AUTOINCREMENT,
                event_id TEXT NOT NULL UNIQUE,
                strategy_instance_id TEXT NOT NULL,
                event_type TEXT NOT NULL,
                strategy TEXT NOT NULL,
                instrument TEXT NOT NULL,
                timestamp TEXT NOT NULL,
                received_at TEXT NOT NULL,
                execution_mode TEXT NOT NULL,
                direction TEXT,
                quantity INTEGER,
                price REAL,
                entry REAL,
                stop REAL,
                target REAL,
                planned_risk REAL,
                realized_pnl REAL,
                reason_code TEXT
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
        connection.execute(
            """
            CREATE TABLE IF NOT EXISTS llm_validations (
                validation_db_id INTEGER PRIMARY KEY AUTOINCREMENT,
                validation_id TEXT NOT NULL UNIQUE,
                strategy_instance_id TEXT NOT NULL,
                strategy TEXT NOT NULL,
                instrument TEXT NOT NULL,
                timestamp TEXT NOT NULL,
                received_at TEXT NOT NULL,
                execution_mode TEXT NOT NULL,
                direction TEXT NOT NULL,
                entry_reference REAL NOT NULL,
                stop REAL NOT NULL,
                target REAL NOT NULL,
                planned_risk REAL NOT NULL,
                request_payload TEXT NOT NULL,
                decision TEXT,
                confidence REAL,
                reason_codes TEXT,
                summary TEXT,
                provider TEXT,
                model TEXT,
                latency_ms INTEGER,
                decided_at TEXT
            )
            """
        )
        connection.execute(
            """
            CREATE TABLE IF NOT EXISTS context_snapshots (
                snapshot_hash TEXT PRIMARY KEY,
                request_hash TEXT NOT NULL,
                playback_time TEXT NOT NULL,
                captured_at TEXT NOT NULL,
                payload TEXT NOT NULL
            )
            """
        )
        connection.execute(
            """
            CREATE TABLE IF NOT EXISTS orb_v2_validations (
                validation_db_id INTEGER PRIMARY KEY AUTOINCREMENT,
                validation_id TEXT NOT NULL UNIQUE,
                strategy_instance_id TEXT NOT NULL,
                strategy TEXT NOT NULL,
                instrument TEXT NOT NULL,
                timestamp TEXT NOT NULL,
                playback_time TEXT NOT NULL,
                received_at TEXT NOT NULL,
                execution_mode TEXT NOT NULL,
                direction TEXT NOT NULL,
                setup_type TEXT NOT NULL,
                entry_reference REAL NOT NULL,
                stop REAL NOT NULL,
                target REAL NOT NULL,
                planned_risk REAL NOT NULL,
                snapshot_hash TEXT NOT NULL,
                request_hash TEXT NOT NULL,
                request_payload TEXT NOT NULL,
                decision TEXT,
                confidence REAL,
                reason_codes TEXT,
                summary TEXT,
                provider TEXT,
                model TEXT,
                latency_ms INTEGER,
                decided_at TEXT,
                recommended_stop REAL,
                recommended_target REAL,
                FOREIGN KEY (snapshot_hash) REFERENCES context_snapshots(snapshot_hash)
            )
            """
        )
        orb_v2_columns = {
            row["name"] for row in connection.execute("PRAGMA table_info(orb_v2_validations)")
        }
        for column in ("recommended_stop", "recommended_target"):
            if column not in orb_v2_columns:
                connection.execute(f"ALTER TABLE orb_v2_validations ADD COLUMN {column} REAL")
        connection.execute(
            """
            CREATE INDEX IF NOT EXISTS ix_orb_v2_snapshot_decision
            ON orb_v2_validations(snapshot_hash, decision)
            """
        )
        connection.execute(
            """
            CREATE TABLE IF NOT EXISTS context_economic_events (
                snapshot_hash TEXT NOT NULL,
                event_id TEXT NOT NULL,
                release_time TEXT NOT NULL,
                known_at TEXT NOT NULL,
                normalized_payload TEXT NOT NULL,
                PRIMARY KEY (snapshot_hash, event_id),
                FOREIGN KEY (snapshot_hash) REFERENCES context_snapshots(snapshot_hash)
            )
            """
        )
        connection.execute(
            """
            CREATE TABLE IF NOT EXISTS context_earnings (
                snapshot_hash TEXT NOT NULL,
                event_id TEXT NOT NULL,
                release_time TEXT NOT NULL,
                known_at TEXT NOT NULL,
                estimated_index_weight REAL NOT NULL,
                normalized_payload TEXT NOT NULL,
                PRIMARY KEY (snapshot_hash, event_id),
                FOREIGN KEY (snapshot_hash) REFERENCES context_snapshots(snapshot_hash)
            )
            """
        )
        connection.execute(
            """
            CREATE TABLE IF NOT EXISTS context_headlines (
                snapshot_hash TEXT NOT NULL,
                headline_id TEXT NOT NULL,
                published_at TEXT NOT NULL,
                known_at TEXT NOT NULL,
                normalized_payload TEXT NOT NULL,
                PRIMARY KEY (snapshot_hash, headline_id),
                FOREIGN KEY (snapshot_hash) REFERENCES context_snapshots(snapshot_hash)
            )
            """
        )
        connection.execute(
            """
            CREATE TABLE IF NOT EXISTS orb_v2_candidate_invalidations (
                invalidation_id INTEGER PRIMARY KEY AUTOINCREMENT,
                validation_id TEXT NOT NULL,
                snapshot_hash TEXT NOT NULL,
                reason_code TEXT NOT NULL,
                recorded_at TEXT NOT NULL
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


def insert_trade_event(event: TradeEventIn) -> tuple[int, bool]:
    """Insert an audit event, returning its database id and duplicate status."""

    received_at = datetime.now(timezone.utc).isoformat()
    with connect() as connection:
        existing = connection.execute(
            "SELECT event_db_id FROM trade_events WHERE event_id = ?",
            (event.event_id,),
        ).fetchone()
        if existing is not None:
            return int(existing["event_db_id"]), True

        try:
            cursor = connection.execute(
                """
                INSERT INTO trade_events (
                    event_id, strategy_instance_id, event_type, strategy,
                    instrument, timestamp, received_at, execution_mode,
                    direction, quantity, price, entry, stop, target,
                    planned_risk, realized_pnl, reason_code
                ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
                """,
                (
                    event.event_id,
                    event.strategy_instance_id,
                    event.event_type,
                    event.strategy,
                    event.instrument,
                    event.timestamp.isoformat(),
                    received_at,
                    event.execution_mode,
                    event.direction,
                    event.quantity,
                    event.price,
                    event.entry,
                    event.stop,
                    event.target,
                    event.planned_risk,
                    event.realized_pnl,
                    event.reason_code,
                ),
            )
            return int(cursor.lastrowid), False
        except sqlite3.IntegrityError:
            # A concurrent retry can win between the lookup and insert.
            existing = connection.execute(
                "SELECT event_db_id FROM trade_events WHERE event_id = ?",
                (event.event_id,),
            ).fetchone()
            if existing is None:
                raise
            return int(existing["event_db_id"]), True


def get_llm_validation(validation_id: str) -> sqlite3.Row | None:
    with connect() as connection:
        return connection.execute(
            "SELECT * FROM llm_validations WHERE validation_id = ?",
            (validation_id,),
        ).fetchone()


def insert_llm_validation_request(validation: LlmValidationIn) -> tuple[int, bool]:
    received_at = datetime.now(timezone.utc).isoformat()
    with connect() as connection:
        existing = connection.execute(
            "SELECT validation_db_id FROM llm_validations WHERE validation_id = ?",
            (validation.validation_id,),
        ).fetchone()
        if existing is not None:
            return int(existing["validation_db_id"]), True

        try:
            cursor = connection.execute(
                """
                INSERT INTO llm_validations (
                    validation_id, strategy_instance_id, strategy, instrument,
                    timestamp, received_at, execution_mode, direction,
                    entry_reference, stop, target, planned_risk, request_payload
                ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
                """,
                (
                    validation.validation_id,
                    validation.strategy_instance_id,
                    validation.strategy,
                    validation.instrument,
                    validation.timestamp.isoformat(),
                    received_at,
                    validation.execution_mode,
                    validation.direction,
                    validation.entry_reference,
                    validation.stop,
                    validation.target,
                    validation.planned_risk,
                    validation.model_dump_json(),
                ),
            )
            return int(cursor.lastrowid), False
        except sqlite3.IntegrityError:
            existing = connection.execute(
                "SELECT validation_db_id FROM llm_validations WHERE validation_id = ?",
                (validation.validation_id,),
            ).fetchone()
            if existing is None:
                raise
            return int(existing["validation_db_id"]), True


def persist_llm_validation_decision(
    validation_id: str, decision: LlmProviderDecision
) -> None:
    decided_at = datetime.now(timezone.utc).isoformat()
    with connect() as connection:
        connection.execute(
            """
            UPDATE llm_validations
            SET decision = ?, confidence = ?, reason_codes = ?, summary = ?,
                provider = ?, model = ?, latency_ms = ?, decided_at = ?
            WHERE validation_id = ?
            """,
            (
                decision.decision,
                decision.confidence,
                json.dumps(decision.reason_codes, separators=(",", ":")),
                decision.summary,
                decision.provider,
                decision.model,
                decision.latency_ms,
                decided_at,
                validation_id,
            ),
        )


def get_orb_v2_validation(validation_id: str) -> sqlite3.Row | None:
    with connect() as connection:
        return connection.execute(
            "SELECT * FROM orb_v2_validations WHERE validation_id = ?",
            (validation_id,),
        ).fetchone()


def get_orb_v2_decision_by_snapshot(snapshot_hash: str) -> sqlite3.Row | None:
    with connect() as connection:
        return connection.execute(
            """
            SELECT * FROM orb_v2_validations
            WHERE snapshot_hash = ? AND decision IS NOT NULL
            ORDER BY validation_db_id ASC LIMIT 1
            """,
            (snapshot_hash,),
        ).fetchone()


def insert_orb_v2_validation_request(
    validation: MesOrbPullbackV2ValidationIn, request_hash: str
) -> tuple[int, bool]:
    received_at = datetime.now(timezone.utc).isoformat()
    payload = validation.model_dump_json()
    with connect() as connection:
        existing = connection.execute(
            "SELECT validation_db_id FROM orb_v2_validations WHERE validation_id = ?",
            (validation.validation_id,),
        ).fetchone()
        if existing is not None:
            return int(existing["validation_db_id"]), True

        connection.execute(
            """
            INSERT OR IGNORE INTO context_snapshots (
                snapshot_hash, request_hash, playback_time, captured_at, payload
            ) VALUES (?, ?, ?, ?, ?)
            """,
            (
                validation.snapshot_hash,
                request_hash,
                validation.playback_time.isoformat(),
                received_at,
                payload,
            ),
        )
        for event in validation.economic_events:
            connection.execute(
                """
                INSERT OR IGNORE INTO context_economic_events (
                    snapshot_hash, event_id, release_time, known_at, normalized_payload
                ) VALUES (?, ?, ?, ?, ?)
                """,
                (
                    validation.snapshot_hash,
                    event.event_id,
                    event.release_time.isoformat(),
                    event.known_at.isoformat(),
                    event.model_dump_json(),
                ),
            )
        for event in validation.earnings:
            connection.execute(
                """
                INSERT OR IGNORE INTO context_earnings (
                    snapshot_hash, event_id, release_time, known_at,
                    estimated_index_weight, normalized_payload
                ) VALUES (?, ?, ?, ?, ?, ?)
                """,
                (
                    validation.snapshot_hash,
                    event.event_id,
                    event.release_time.isoformat(),
                    event.known_at.isoformat(),
                    event.estimated_index_weight,
                    event.model_dump_json(),
                ),
            )
        for headline in validation.headlines:
            connection.execute(
                """
                INSERT OR IGNORE INTO context_headlines (
                    snapshot_hash, headline_id, published_at, known_at, normalized_payload
                ) VALUES (?, ?, ?, ?, ?)
                """,
                (
                    validation.snapshot_hash,
                    headline.headline_id,
                    headline.published_at.isoformat(),
                    headline.known_at.isoformat(),
                    headline.model_dump_json(),
                ),
            )
        try:
            cursor = connection.execute(
                """
                INSERT INTO orb_v2_validations (
                    validation_id, strategy_instance_id, strategy, instrument,
                    timestamp, playback_time, received_at, execution_mode,
                    direction, setup_type, entry_reference, stop, target,
                    planned_risk, snapshot_hash, request_hash, request_payload
                ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
                """,
                (
                    validation.validation_id,
                    validation.strategy_instance_id,
                    validation.strategy,
                    validation.instrument,
                    validation.timestamp.isoformat(),
                    validation.playback_time.isoformat(),
                    received_at,
                    validation.execution_mode,
                    validation.proposed_trade.direction,
                    validation.pullback.setup_type,
                    validation.proposed_trade.entry_reference,
                    validation.proposed_trade.stop,
                    validation.proposed_trade.target,
                    validation.proposed_trade.planned_risk,
                    validation.snapshot_hash,
                    request_hash,
                    payload,
                ),
            )
            return int(cursor.lastrowid), False
        except sqlite3.IntegrityError:
            existing = connection.execute(
                "SELECT validation_db_id FROM orb_v2_validations WHERE validation_id = ?",
                (validation.validation_id,),
            ).fetchone()
            if existing is None:
                raise
            return int(existing["validation_db_id"]), True


def persist_orb_v2_decision(
    validation_id: str, decision: LlmProviderDecision
) -> None:
    decided_at = datetime.now(timezone.utc).isoformat()
    with connect() as connection:
        connection.execute(
            """
            UPDATE orb_v2_validations
            SET decision = ?, confidence = ?, reason_codes = ?, summary = ?,
                provider = ?, model = ?, latency_ms = ?, decided_at = ?,
                recommended_stop = ?, recommended_target = ?
            WHERE validation_id = ?
            """,
            (
                decision.decision,
                decision.confidence,
                json.dumps(decision.reason_codes, separators=(",", ":")),
                decision.summary,
                decision.provider,
                decision.model,
                decision.latency_ms,
                decided_at,
                decision.stop_loss,
                decision.take_profit,
                validation_id,
            ),
        )


def copy_orb_v2_snapshot_decision(source: sqlite3.Row, validation_id: str) -> None:
    with connect() as connection:
        connection.execute(
            """
            UPDATE orb_v2_validations
            SET decision = ?, confidence = ?, reason_codes = ?, summary = ?,
                provider = 'snapshot_replay', model = ?, latency_ms = 0,
                decided_at = ?, recommended_stop = ?, recommended_target = ?
            WHERE validation_id = ?
            """,
            (
                source["decision"],
                source["confidence"],
                source["reason_codes"],
                source["summary"],
                source["model"],
                datetime.now(timezone.utc).isoformat(),
                source["recommended_stop"],
                source["recommended_target"],
                validation_id,
            ),
        )


def record_orb_v2_invalidation(
    validation_id: str, snapshot_hash: str, reason_code: str
) -> None:
    with connect() as connection:
        connection.execute(
            """
            INSERT INTO orb_v2_candidate_invalidations (
                validation_id, snapshot_hash, reason_code, recorded_at
            ) VALUES (?, ?, ?, ?)
            """,
            (
                validation_id,
                snapshot_hash,
                reason_code,
                datetime.now(timezone.utc).isoformat(),
            ),
        )
