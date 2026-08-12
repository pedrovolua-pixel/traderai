from __future__ import annotations

import hashlib
import logging
import os
import threading
from dataclasses import dataclass, field
from datetime import date, datetime, time, timedelta, timezone
from typing import Any
from zoneinfo import ZoneInfo

import httpx

from app.models import (
    ContextStatus,
    EarningsEvent,
    EconomicEvent,
    MarketHeadline,
    MesOrbPullbackV2ValidationIn,
)


logger = logging.getLogger("traderai.context")
ET = ZoneInfo("America/New_York")


class ContextUnavailable(RuntimeError):
    pass


@dataclass
class ContextCache:
    economic_events: list[EconomicEvent] = field(default_factory=list)
    earnings: list[EarningsEvent] = field(default_factory=list)
    headlines: list[MarketHeadline] = field(default_factory=list)
    constituents: dict[str, tuple[str, float]] = field(default_factory=dict)
    economic_events_refreshed_at: datetime | None = None
    earnings_refreshed_at: datetime | None = None
    constituents_refreshed_at: datetime | None = None
    headlines_refreshed_at: datetime | None = None
    last_error: str | None = None


class FmpMarketContextService:
    """Bounded local cache; it is never called synchronously from an entry request."""

    ECONOMIC_TTL = timedelta(minutes=10)
    EARNINGS_TTL = timedelta(hours=3)
    CONSTITUENTS_TTL = timedelta(hours=24)
    HEADLINES_TTL = timedelta(minutes=10)

    def __init__(
        self,
        api_key: str | None = None,
        base_url: str = "https://financialmodelingprep.com/stable",
        client: httpx.Client | None = None,
    ) -> None:
        self.api_key = (api_key if api_key is not None else os.getenv("FMP_API_KEY", "")).strip()
        self.base_url = base_url.rstrip("/")
        self.client = client or httpx.Client(timeout=8.0)
        self.cache = ContextCache()
        self._lock = threading.RLock()
        self._stop = threading.Event()
        self._worker: threading.Thread | None = None

    def start(self) -> None:
        if not self.api_key or (self._worker and self._worker.is_alive()):
            return
        self._stop.clear()
        self._worker = threading.Thread(target=self._refresh_loop, name="fmp-context", daemon=True)
        self._worker.start()

    def stop(self) -> None:
        self._stop.set()
        if self._worker:
            self._worker.join(timeout=2.0)

    def _refresh_loop(self) -> None:
        while not self._stop.is_set():
            try:
                self.refresh_due()
            except Exception:
                logger.exception("FMP context refresh failed")
            self._stop.wait(30.0)

    def refresh_due(self, now: datetime | None = None) -> None:
        now = now or datetime.now(timezone.utc)
        with self._lock:
            cache = self.cache
            economic_due = self._is_stale(cache.economic_events_refreshed_at, self.ECONOMIC_TTL, now)
            earnings_due = self._is_stale(cache.earnings_refreshed_at, self.EARNINGS_TTL, now)
            constituents_due = (
                cache.constituents_refreshed_at is None
                or cache.constituents_refreshed_at.astimezone(ET).date() < now.astimezone(ET).date()
            )
            headlines_due = self._is_stale(cache.headlines_refreshed_at, self.HEADLINES_TTL, now)
        try:
            if constituents_due:
                self._refresh_constituents(now)
            if economic_due:
                self._refresh_economic(now)
            if earnings_due:
                self._refresh_earnings(now)
            if headlines_due:
                self._refresh_headlines(now)
            with self._lock:
                self.cache.last_error = None
        except Exception as exc:
            with self._lock:
                self.cache.last_error = f"{type(exc).__name__}: {exc}"
            raise

    @staticmethod
    def _is_stale(value: datetime | None, ttl: timedelta, now: datetime) -> bool:
        return value is None or value > now or now - value > ttl

    def status(self, now: datetime | None = None) -> ContextStatus:
        now = now or datetime.now(timezone.utc)
        with self._lock:
            cache = self.cache
            if not self.api_key:
                return ContextStatus(
                    status="disabled",
                    provider="disabled",
                    archive_ready=False,
                    economic_events_refreshed_at=None,
                    earnings_refreshed_at=None,
                    constituents_refreshed_at=None,
                    headlines_refreshed_at=None,
                    economic_events_fresh=False,
                    earnings_fresh=False,
                    constituents_fresh=False,
                    headlines_fresh=False,
                    next_economic_event=None,
                    next_earnings=None,
                    detail=(
                        "FMP context is disabled; current simulation candidates use "
                        "price-action-only LLM validation."
                    ),
                )
            economic_fresh = not self._is_stale(cache.economic_events_refreshed_at, self.ECONOMIC_TTL, now)
            earnings_fresh = not self._is_stale(cache.earnings_refreshed_at, self.EARNINGS_TTL, now)
            constituents_fresh = (
                cache.constituents_refreshed_at is not None
                and cache.constituents_refreshed_at.astimezone(ET).date() == now.astimezone(ET).date()
            )
            headlines_fresh = not self._is_stale(cache.headlines_refreshed_at, self.HEADLINES_TTL, now)
            ready = bool(self.api_key) and all(
                [economic_fresh, earnings_fresh, constituents_fresh, headlines_fresh]
            )
            any_data = any(
                [
                    cache.economic_events_refreshed_at,
                    cache.earnings_refreshed_at,
                    cache.constituents_refreshed_at,
                    cache.headlines_refreshed_at,
                ]
            )
            status = "ready" if ready else "stale" if any_data else "unavailable"
            detail = cache.last_error or ("context cache ready" if ready else "context cache incomplete or stale")
            next_event = next(
                (item for item in sorted(cache.economic_events, key=lambda item: item.release_time)
                 if item.release_time >= now),
                None,
            )
            next_earning = next(
                (item for item in sorted(cache.earnings, key=lambda item: item.release_time)
                 if item.release_time >= now),
                None,
            )
            return ContextStatus(
                status=status,
                provider="fmp",
                archive_ready=ready,
                economic_events_refreshed_at=cache.economic_events_refreshed_at,
                earnings_refreshed_at=cache.earnings_refreshed_at,
                constituents_refreshed_at=cache.constituents_refreshed_at,
                headlines_refreshed_at=cache.headlines_refreshed_at,
                economic_events_fresh=economic_fresh,
                earnings_fresh=earnings_fresh,
                constituents_fresh=constituents_fresh,
                headlines_fresh=headlines_fresh,
                next_economic_event=(
                    f"{next_event.name} @ {next_event.release_time.isoformat()} ({next_event.importance})"
                    if next_event else None
                ),
                next_earnings=(
                    f"{next_earning.symbol} @ {next_earning.release_time.isoformat()} "
                    f"(estimated weight {next_earning.estimated_index_weight:.4f})"
                    if next_earning else None
                ),
                detail=detail,
            )

    def enrich(self, validation: MesOrbPullbackV2ValidationIn) -> MesOrbPullbackV2ValidationIn:
        """Return a point-in-time snapshot without performing network I/O."""
        replay_time = validation.playback_time
        now = datetime.now(timezone.utc)
        supplied_archive = bool(
            validation.context_timestamps.economic_events_refreshed_at
            and validation.context_timestamps.earnings_refreshed_at
            and validation.context_timestamps.constituents_refreshed_at
            and validation.context_timestamps.headlines_refreshed_at
        )
        if supplied_archive:
            self._assert_known_at(validation)
            return validation

        # FMP is optional.  With no provider key, validate a current Sim101
        # candidate from the deterministic price/action snapshot only.  Do not
        # silently use this mode for old Playback because that would remove the
        # historical point-in-time guarantee.
        if not self.api_key:
            if abs((now - replay_time).total_seconds()) > 24 * 3600:
                raise ContextUnavailable("historical_point_in_time_context_missing")
            return validation.model_copy(
                update={
                    "economic_events": [],
                    "earnings": [],
                    "headlines": [],
                    "context_timestamps": validation.context_timestamps.model_copy(
                        update={
                            "economic_events_refreshed_at": None,
                            "earnings_refreshed_at": None,
                            "constituents_refreshed_at": None,
                            "headlines_refreshed_at": None,
                        }
                    ),
                }
            )

        # A current cache is point-in-time safe only when it was fetched no later
        # than the strategy clock. Old Playback sessions require an archive.
        if abs((now - replay_time).total_seconds()) > 24 * 3600:
            raise ContextUnavailable("historical_point_in_time_context_missing")

        status = self.status(now)
        if status.status != "ready":
            raise ContextUnavailable("market_context_unavailable_or_stale")

        with self._lock:
            cache = self.cache
            refreshes = [
                cache.economic_events_refreshed_at,
                cache.earnings_refreshed_at,
                cache.constituents_refreshed_at,
                cache.headlines_refreshed_at,
            ]
            if any(value is None or value > replay_time for value in refreshes):
                raise ContextUnavailable("market_context_known_after_playback_time")
            update = {
                "economic_events": [
                    item.model_copy(update={"minutes_from_release": int((item.release_time - replay_time).total_seconds() / 60)})
                    for item in cache.economic_events if item.known_at <= replay_time
                ][:20],
                "earnings": [
                    item.model_copy(update={"minutes_from_release": int((item.release_time - replay_time).total_seconds() / 60)})
                    for item in cache.earnings if item.known_at <= replay_time
                ][:20],
                "headlines": [item for item in cache.headlines if item.known_at <= replay_time][:10],
                "context_timestamps": validation.context_timestamps.model_copy(
                    update={
                        "economic_events_refreshed_at": cache.economic_events_refreshed_at,
                        "earnings_refreshed_at": cache.earnings_refreshed_at,
                        "constituents_refreshed_at": cache.constituents_refreshed_at,
                        "headlines_refreshed_at": cache.headlines_refreshed_at,
                    }
                ),
            }
        enriched = validation.model_copy(update=update)
        self._assert_known_at(enriched)
        return enriched

    @staticmethod
    def _assert_known_at(validation: MesOrbPullbackV2ValidationIn) -> None:
        replay_time = validation.playback_time
        if any(item.known_at > replay_time for item in validation.economic_events):
            raise ContextUnavailable("economic_event_lookahead")
        if any(item.known_at > replay_time for item in validation.earnings):
            raise ContextUnavailable("earnings_lookahead")
        if any(item.known_at > replay_time for item in validation.headlines):
            raise ContextUnavailable("headline_lookahead")

    def _get(self, path: str, **params: str) -> list[dict[str, Any]]:
        response = self.client.get(
            f"{self.base_url}/{path.lstrip('/')}",
            params={**params, "apikey": self.api_key},
        )
        response.raise_for_status()
        payload = response.json()
        if not isinstance(payload, list):
            raise ValueError(f"FMP {path} returned a non-list payload")
        return [item for item in payload if isinstance(item, dict)]

    def _refresh_constituents(self, fetched_at: datetime) -> None:
        rows = self._get("sp500-constituent")
        market_caps = {
            str(row.get("symbol", "")).upper(): max(float(row.get("marketCap") or 0), 0)
            for row in rows
            if row.get("symbol")
        }
        total = sum(market_caps.values())
        if total <= 0:
            raise ValueError("FMP constituents did not contain usable market caps")
        weighted = sorted(market_caps.items(), key=lambda item: item[1], reverse=True)[:20]
        names = {str(row.get("symbol", "")).upper(): str(row.get("name") or row.get("symbol")) for row in rows}
        with self._lock:
            self.cache.constituents = {
                symbol: (names.get(symbol, symbol), market_cap / total)
                for symbol, market_cap in weighted
            }
            self.cache.constituents_refreshed_at = fetched_at

    def _refresh_economic(self, fetched_at: datetime) -> None:
        et_day = fetched_at.astimezone(ET).date()
        rows = self._get(
            "economic-calendar",
            **{"from": (et_day - timedelta(days=1)).isoformat(), "to": (et_day + timedelta(days=2)).isoformat()},
        )
        events: list[EconomicEvent] = []
        for row in rows:
            release = self._parse_datetime(row.get("date"), ET)
            if release is None:
                continue
            name = str(row.get("event") or row.get("name") or "Economic event")
            country = str(row.get("country") or "US")
            if country.upper() not in {"US", "USA", "UNITED STATES"}:
                continue
            importance = self._importance(row.get("impact") or row.get("importance"))
            events.append(
                EconomicEvent(
                    event_id=self._id("economic", name, release.isoformat()),
                    name=name,
                    country=country,
                    release_time=release,
                    known_at=fetched_at,
                    importance=importance,
                    forecast=self._text(row.get("estimate") or row.get("forecast")),
                    actual=self._text(row.get("actual")),
                    previous=self._text(row.get("previous")),
                    surprise=self._float_or_none(row.get("changePercentage") or row.get("surprise")),
                    minutes_from_release=int((release - fetched_at).total_seconds() / 60),
                )
            )
        events.sort(key=lambda item: (abs(item.minutes_from_release), item.release_time))
        with self._lock:
            self.cache.economic_events = events[:20]
            self.cache.economic_events_refreshed_at = fetched_at

    def _refresh_earnings(self, fetched_at: datetime) -> None:
        et_day = fetched_at.astimezone(ET).date()
        rows = self._get(
            "earnings-calendar",
            **{"from": (et_day - timedelta(days=1)).isoformat(), "to": (et_day + timedelta(days=1)).isoformat()},
        )
        with self._lock:
            constituents = dict(self.cache.constituents)
        earnings: list[EarningsEvent] = []
        for row in rows:
            symbol = str(row.get("symbol") or "").upper()
            if symbol not in constituents:
                continue
            release = self._earnings_datetime(row, ET)
            if release is None:
                continue
            company, weight = constituents[symbol]
            session = self._earnings_session(release, et_day)
            if session is None:
                continue
            earnings.append(
                EarningsEvent(
                    event_id=self._id("earnings", symbol, release.isoformat()),
                    symbol=symbol,
                    company=company,
                    release_time=release,
                    known_at=fetched_at,
                    session=session,
                    estimated_index_weight=weight,
                    eps_estimate=self._text(row.get("epsEstimated") or row.get("epsEstimate")),
                    eps_actual=self._text(row.get("eps")),
                    surprise=self._float_or_none(row.get("surprisePercentage") or row.get("surprise")),
                    minutes_from_release=int((release - fetched_at).total_seconds() / 60),
                )
            )
        earnings.sort(key=lambda item: item.estimated_index_weight, reverse=True)
        with self._lock:
            self.cache.earnings = earnings[:20]
            self.cache.earnings_refreshed_at = fetched_at

    def _refresh_headlines(self, fetched_at: datetime) -> None:
        with self._lock:
            symbols = list(self.cache.constituents)[:20]
        rows = self._get("news/stock-latest", symbols=",".join(symbols), limit="40")
        rows += self._get("news/general-latest", limit="20")
        headlines: list[MarketHeadline] = []
        for row in rows:
            published = self._parse_datetime(row.get("publishedDate") or row.get("date"), ET)
            title = str(row.get("title") or "").strip()
            if not title or published is None or published > fetched_at:
                continue
            symbol = str(row.get("symbol") or "").upper()
            headlines.append(
                MarketHeadline(
                    headline_id=self._id("headline", title, published.isoformat()),
                    title=title[:512],
                    symbols=[symbol] if symbol else [],
                    published_at=published,
                    known_at=fetched_at,
                    source=str(row.get("site") or row.get("publisher") or "FMP")[:128],
                )
            )
        headlines.sort(key=lambda item: item.published_at, reverse=True)
        with self._lock:
            self.cache.headlines = headlines[:10]
            self.cache.headlines_refreshed_at = fetched_at

    @staticmethod
    def _parse_datetime(value: Any, assumed_zone: ZoneInfo) -> datetime | None:
        if not value:
            return None
        try:
            parsed = datetime.fromisoformat(str(value).replace("Z", "+00:00"))
            if parsed.tzinfo is None:
                parsed = parsed.replace(tzinfo=assumed_zone)
            return parsed.astimezone(timezone.utc)
        except ValueError:
            return None

    @classmethod
    def _earnings_datetime(cls, row: dict[str, Any], zone: ZoneInfo) -> datetime | None:
        parsed = cls._parse_datetime(row.get("date"), zone)
        if parsed is None:
            return None
        release_label = str(row.get("time") or "").lower()
        local_day = parsed.astimezone(zone).date()
        local_time = time(8, 0) if release_label in {"bmo", "before market open"} else time(16, 15)
        return datetime.combine(local_day, local_time, zone).astimezone(timezone.utc)

    @staticmethod
    def _earnings_session(release: datetime, current_day: date) -> str | None:
        local = release.astimezone(ET)
        if local.date() == current_day - timedelta(days=1) and local.time() >= time(16):
            return "previous_postmarket"
        if local.date() == current_day and local.time() < time(9, 30):
            return "current_premarket"
        if local.date() == current_day and local.time() < time(16):
            return "current_session"
        if local.date() in {current_day, current_day + timedelta(days=1)}:
            return "next_postmarket"
        return None

    @staticmethod
    def _importance(value: Any) -> str:
        normalized = str(value or "").lower()
        if "high" in normalized or normalized == "3":
            return "high"
        if "medium" in normalized or "moderate" in normalized or normalized == "2":
            return "medium"
        if "low" in normalized or normalized == "1":
            return "low"
        return "unknown"

    @staticmethod
    def _text(value: Any) -> str | None:
        if value is None or value == "":
            return None
        return str(value)[:64]

    @staticmethod
    def _float_or_none(value: Any) -> float | None:
        try:
            return float(value) if value not in (None, "") else None
        except (TypeError, ValueError):
            return None

    @staticmethod
    def _id(prefix: str, *parts: str) -> str:
        digest = hashlib.sha256("|".join(parts).encode("utf-8")).hexdigest()[:32]
        return f"{prefix}-{digest}"


def build_market_context_service_from_environment() -> FmpMarketContextService:
    return FmpMarketContextService(
        base_url=os.getenv("FMP_BASE_URL", "https://financialmodelingprep.com/stable")
    )
