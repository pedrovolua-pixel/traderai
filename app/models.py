from datetime import datetime
from typing import Literal

from pydantic import BaseModel, ConfigDict, Field, field_validator, model_validator


class SignalIn(BaseModel):
    model_config = ConfigDict(extra="forbid", str_strip_whitespace=True)

    instrument: str = Field(min_length=1)
    strategy: str = Field(min_length=1)
    direction: Literal["long", "short"]
    timeframe: str = Field(min_length=1)
    price: float = Field(gt=0)
    timestamp: datetime
    entry: float | None = Field(default=None, gt=0)
    stop: float | None = Field(default=None, gt=0)
    target1: float | None = Field(default=None, gt=0)
    target2: float | None = Field(default=None, gt=0)
    ema20: float | None = Field(default=None, gt=0)
    ema50: float | None = Field(default=None, gt=0)
    vwap: float | None = Field(default=None, gt=0)
    atr: float | None = Field(default=None, ge=0)
    recent_swing_low: float | None = Field(default=None, gt=0)
    regime: str | None = None
    setup_state: str | None = None
    raw_rr: float | None = Field(default=None, ge=0)

    @field_validator("timestamp")
    @classmethod
    def timestamp_must_include_timezone(cls, value: datetime) -> datetime:
        if value.tzinfo is None or value.utcoffset() is None:
            raise ValueError("timestamp must include a timezone")
        return value


class SignalAccepted(BaseModel):
    accepted: Literal[True] = True
    signal_id: int
    risk_status: Literal["accepted", "rejected"]
    risk_reason: str | None


TradeEventType = Literal[
    "setup_armed",
    "setup_skipped",
    "entry_submitted",
    "entry_filled",
    "protection_active",
    "exit_filled",
    "order_rejected",
    "risk_lockout",
    "connection_lost",
]


class TradeEventIn(BaseModel):
    """Redacted, local-only audit event emitted by a NinjaTrader strategy."""

    model_config = ConfigDict(extra="forbid", str_strip_whitespace=True)

    event_id: str = Field(min_length=1, max_length=128)
    strategy_instance_id: str = Field(min_length=1, max_length=128)
    event_type: TradeEventType
    strategy: str = Field(min_length=1, max_length=128)
    instrument: str = Field(min_length=1, max_length=64)
    timestamp: datetime
    execution_mode: Literal["historical", "playback", "simulation"]
    direction: Literal["long", "short"] | None = None
    quantity: int | None = Field(default=None, ge=0, le=1)
    price: float | None = Field(default=None, gt=0)
    entry: float | None = Field(default=None, gt=0)
    stop: float | None = Field(default=None, gt=0)
    target: float | None = Field(default=None, gt=0)
    planned_risk: float | None = Field(default=None, ge=0)
    realized_pnl: float | None = None
    reason_code: str | None = Field(default=None, max_length=256)

    @field_validator("timestamp")
    @classmethod
    def event_timestamp_must_include_timezone(cls, value: datetime) -> datetime:
        if value.tzinfo is None or value.utcoffset() is None:
            raise ValueError("timestamp must include a timezone")
        return value


class TradeEventAccepted(BaseModel):
    accepted: Literal[True] = True
    event_db_id: int
    duplicate: bool


class MarketBar(BaseModel):
    model_config = ConfigDict(extra="forbid")

    timestamp: datetime
    open: float = Field(gt=0)
    high: float = Field(gt=0)
    low: float = Field(gt=0)
    close: float = Field(gt=0)
    volume: float = Field(ge=0)

    @field_validator("timestamp")
    @classmethod
    def bar_timestamp_must_include_timezone(cls, value: datetime) -> datetime:
        if value.tzinfo is None or value.utcoffset() is None:
            raise ValueError("timestamp must include a timezone")
        return value

    @model_validator(mode="after")
    def validate_ohlc_geometry(self) -> "MarketBar":
        if self.high < max(self.open, self.close, self.low):
            raise ValueError("high must be the greatest OHLC value")
        if self.low > min(self.open, self.close, self.high):
            raise ValueError("low must be the least OHLC value")
        return self


class LlmValidationIn(BaseModel):
    """Redacted market snapshot for a simulation-only entry veto decision."""

    model_config = ConfigDict(extra="forbid", str_strip_whitespace=True)

    validation_id: str = Field(min_length=1, max_length=128)
    strategy_instance_id: str = Field(min_length=1, max_length=128)
    strategy: Literal["mes_orb_structure_v1"]
    instrument: str = Field(pattern=r"^MES(?:\s|$)", max_length=64)
    timestamp: datetime
    execution_mode: Literal["playback", "simulation"]
    direction: Literal["long", "short"]
    quantity: Literal[1]
    opening_range_high: float = Field(gt=0)
    opening_range_low: float = Field(gt=0)
    breakout_close: float = Field(gt=0)
    entry_reference: float = Field(gt=0)
    stop: float = Field(gt=0)
    target: float = Field(gt=0)
    planned_risk: float = Field(gt=0, le=50)
    reward_multiple: float = Field(ge=1, le=10)
    structure_direction: Literal["bullish", "bearish"]
    confirmed_swing_highs: list[float] = Field(min_length=2, max_length=8)
    confirmed_swing_lows: list[float] = Field(min_length=2, max_length=8)
    primary_bars: list[MarketBar] = Field(min_length=3, max_length=20)
    structure_bars: list[MarketBar] = Field(min_length=3, max_length=20)

    @field_validator("timestamp")
    @classmethod
    def validation_timestamp_must_include_timezone(cls, value: datetime) -> datetime:
        if value.tzinfo is None or value.utcoffset() is None:
            raise ValueError("timestamp must include a timezone")
        return value

    @field_validator("confirmed_swing_highs", "confirmed_swing_lows")
    @classmethod
    def pivots_must_be_positive(cls, values: list[float]) -> list[float]:
        if any(value <= 0 for value in values):
            raise ValueError("confirmed pivots must be positive")
        return values

    @model_validator(mode="after")
    def validate_trade_geometry(self) -> "LlmValidationIn":
        if self.opening_range_high <= self.opening_range_low:
            raise ValueError("opening range high must exceed opening range low")
        if self.direction == "long":
            if self.structure_direction != "bullish":
                raise ValueError("long validation requires bullish structure")
            if self.breakout_close <= self.opening_range_high:
                raise ValueError("long breakout must close above the opening range")
            if not self.stop < self.entry_reference < self.target:
                raise ValueError("invalid long stop/entry/target geometry")
        else:
            if self.structure_direction != "bearish":
                raise ValueError("short validation requires bearish structure")
            if self.breakout_close >= self.opening_range_low:
                raise ValueError("short breakout must close below the opening range")
            if not self.target < self.entry_reference < self.stop:
                raise ValueError("invalid short stop/entry/target geometry")
        return self


class LlmProviderDecision(BaseModel):
    model_config = ConfigDict(extra="forbid", str_strip_whitespace=True)

    decision: Literal["allow", "reject"]
    confidence: float = Field(ge=0, le=1)
    reason_codes: list[str] = Field(min_length=1, max_length=8)
    summary: str = Field(min_length=1, max_length=256)
    provider: str = Field(min_length=1, max_length=64)
    model: str = Field(min_length=1, max_length=128)
    latency_ms: int = Field(ge=0, le=120_000)
    stop_loss: float | None = Field(default=None, gt=0)
    take_profit: float | None = Field(default=None, gt=0)

    @field_validator("reason_codes")
    @classmethod
    def validate_reason_codes(cls, values: list[str]) -> list[str]:
        cleaned: list[str] = []
        for value in values:
            normalized = value.strip().lower()
            if not normalized or len(normalized) > 64:
                raise ValueError("reason codes must contain 1-64 characters")
            if any(character not in "abcdefghijklmnopqrstuvwxyz0123456789_" for character in normalized):
                raise ValueError("reason codes must be lowercase snake_case")
            if normalized not in cleaned:
                cleaned.append(normalized)
        return cleaned


class LlmValidationAccepted(BaseModel):
    accepted: Literal[True] = True
    validation_db_id: int
    duplicate: bool
    validation_id: str
    decision: Literal["allow", "reject"]
    confidence: float = Field(ge=0, le=1)
    reason_codes: list[str]
    summary: str
    provider: str
    model: str
    latency_ms: int = Field(ge=0)
    stop_loss: float | None = Field(default=None, gt=0)
    take_profit: float | None = Field(default=None, gt=0)


class KnownFloat(BaseModel):
    model_config = ConfigDict(extra="forbid")

    value: float
    known_at: datetime

    @field_validator("known_at")
    @classmethod
    def known_at_must_include_timezone(cls, value: datetime) -> datetime:
        if value.tzinfo is None or value.utcoffset() is None:
            raise ValueError("known_at must include a timezone")
        return value


class OpeningRangeCandle(BaseModel):
    model_config = ConfigDict(extra="forbid")

    start: datetime
    end: datetime
    open: float = Field(gt=0)
    high: float = Field(gt=0)
    low: float = Field(gt=0)
    close: float = Field(gt=0)

    @field_validator("start", "end")
    @classmethod
    def or_timestamp_must_include_timezone(cls, value: datetime) -> datetime:
        if value.tzinfo is None or value.utcoffset() is None:
            raise ValueError("opening-range timestamps must include a timezone")
        return value

    @model_validator(mode="after")
    def validate_geometry(self) -> "OpeningRangeCandle":
        if self.end <= self.start:
            raise ValueError("opening-range end must follow start")
        if self.high < max(self.open, self.close, self.low):
            raise ValueError("opening-range high is invalid")
        if self.low > min(self.open, self.close, self.high):
            raise ValueError("opening-range low is invalid")
        return self


class OrbBreakout(BaseModel):
    model_config = ConfigDict(extra="forbid")

    direction: Literal["long", "short"]
    bar_timestamp: datetime
    close: float = Field(gt=0)
    distance_points: float = Field(ge=0)

    @field_validator("bar_timestamp")
    @classmethod
    def timestamp_must_include_timezone(cls, value: datetime) -> datetime:
        if value.tzinfo is None or value.utcoffset() is None:
            raise ValueError("breakout timestamp must include a timezone")
        return value


class OrbPullback(BaseModel):
    model_config = ConfigDict(extra="forbid")

    setup_type: Literal["or_retest", "swing_pullback", "breakout_close"]
    candidate_id: str = Field(min_length=1, max_length=128)
    bar_timestamp: datetime
    trigger_price: float = Field(gt=0)
    structural_price: float = Field(gt=0)
    quality_score: float = Field(ge=0, le=1)
    bars_since_breakout: int = Field(ge=1, le=500)

    @field_validator("bar_timestamp")
    @classmethod
    def timestamp_must_include_timezone(cls, value: datetime) -> datetime:
        if value.tzinfo is None or value.utcoffset() is None:
            raise ValueError("pullback timestamp must include a timezone")
        return value


class ProposedOrbTrade(BaseModel):
    model_config = ConfigDict(extra="forbid")

    direction: Literal["long", "short"]
    quantity: Literal[1]
    entry_reference: float = Field(gt=0)
    stop: float = Field(gt=0)
    target: float = Field(gt=0)
    planned_risk: float = Field(gt=0)
    reward_multiple: float = Field(ge=1, le=10)

    @model_validator(mode="after")
    def validate_geometry(self) -> "ProposedOrbTrade":
        if self.direction == "long" and not self.stop < self.entry_reference < self.target:
            raise ValueError("invalid long trade geometry")
        if self.direction == "short" and not self.target < self.entry_reference < self.stop:
            raise ValueError("invalid short trade geometry")
        return self


class MarketRegime(BaseModel):
    model_config = ConfigDict(extra="forbid")

    previous_day_high: KnownFloat
    previous_day_low: KnownFloat
    previous_day_close: KnownFloat
    overnight_high: KnownFloat
    overnight_low: KnownFloat
    gap_points: KnownFloat
    rth_vwap: KnownFloat
    atr_14_daily: KnownFloat
    relative_volume: KnownFloat
    realized_range: KnownFloat
    or_width_atr: KnownFloat
    breakout_distance: KnownFloat
    pullback_quality: KnownFloat
    planned_risk: KnownFloat


class EconomicEvent(BaseModel):
    model_config = ConfigDict(extra="forbid", str_strip_whitespace=True)

    event_id: str = Field(min_length=1, max_length=256)
    name: str = Field(min_length=1, max_length=256)
    country: str = Field(min_length=1, max_length=32)
    release_time: datetime
    known_at: datetime
    importance: Literal["low", "medium", "high", "unknown"] = "unknown"
    forecast: str | None = Field(default=None, max_length=64)
    actual: str | None = Field(default=None, max_length=64)
    previous: str | None = Field(default=None, max_length=64)
    surprise: float | None = None
    minutes_from_release: int

    @field_validator("release_time", "known_at")
    @classmethod
    def timestamp_must_include_timezone(cls, value: datetime) -> datetime:
        if value.tzinfo is None or value.utcoffset() is None:
            raise ValueError("event timestamps must include a timezone")
        return value


class EarningsEvent(BaseModel):
    model_config = ConfigDict(extra="forbid", str_strip_whitespace=True)

    event_id: str = Field(min_length=1, max_length=256)
    symbol: str = Field(pattern=r"^[A-Z.\-]{1,12}$")
    company: str = Field(min_length=1, max_length=256)
    release_time: datetime
    known_at: datetime
    session: Literal["previous_postmarket", "current_premarket", "current_session", "next_postmarket"]
    estimated_index_weight: float = Field(ge=0, le=1)
    eps_estimate: str | None = Field(default=None, max_length=64)
    eps_actual: str | None = Field(default=None, max_length=64)
    surprise: float | None = None
    minutes_from_release: int

    @field_validator("release_time", "known_at")
    @classmethod
    def timestamp_must_include_timezone(cls, value: datetime) -> datetime:
        if value.tzinfo is None or value.utcoffset() is None:
            raise ValueError("earnings timestamps must include a timezone")
        return value


class MarketHeadline(BaseModel):
    model_config = ConfigDict(extra="forbid", str_strip_whitespace=True)

    headline_id: str = Field(min_length=1, max_length=256)
    title: str = Field(min_length=1, max_length=512)
    symbols: list[str] = Field(default_factory=list, max_length=20)
    published_at: datetime
    known_at: datetime
    source: str = Field(min_length=1, max_length=128)

    @field_validator("published_at", "known_at")
    @classmethod
    def timestamp_must_include_timezone(cls, value: datetime) -> datetime:
        if value.tzinfo is None or value.utcoffset() is None:
            raise ValueError("headline timestamps must include a timezone")
        return value


class ContextTimestamps(BaseModel):
    model_config = ConfigDict(extra="forbid")

    market_data_known_at: datetime
    economic_events_refreshed_at: datetime | None = None
    earnings_refreshed_at: datetime | None = None
    constituents_refreshed_at: datetime | None = None
    headlines_refreshed_at: datetime | None = None

    @field_validator("market_data_known_at", "economic_events_refreshed_at", "earnings_refreshed_at", "constituents_refreshed_at", "headlines_refreshed_at")
    @classmethod
    def timestamp_must_include_timezone(cls, value: datetime | None) -> datetime | None:
        if value is not None and (value.tzinfo is None or value.utcoffset() is None):
            raise ValueError("context timestamps must include a timezone")
        return value


class MesOrbPullbackV2ValidationIn(BaseModel):
    """Complete point-in-time snapshot for the V2 veto-only decision."""

    model_config = ConfigDict(extra="forbid", str_strip_whitespace=True)

    validation_id: str = Field(min_length=1, max_length=128)
    strategy_instance_id: str = Field(min_length=1, max_length=128)
    strategy: Literal["mes_orb_pullback_v2"]
    instrument: str = Field(pattern=r"^MES(?:\s|$)", max_length=64)
    timestamp: datetime
    playback_time: datetime
    execution_mode: Literal["playback", "simulation"]
    opening_range: OpeningRangeCandle
    breakout: OrbBreakout
    pullback: OrbPullback
    proposed_trade: ProposedOrbTrade
    bars_15m: list[MarketBar] = Field(min_length=3, max_length=12)
    bars_60m: list[MarketBar] = Field(min_length=3, max_length=12)
    bars_240m: list[MarketBar] = Field(min_length=3, max_length=8)
    bars_daily: list[MarketBar] = Field(min_length=3, max_length=10)
    market_regime: MarketRegime
    economic_events: list[EconomicEvent] = Field(default_factory=list, max_length=20)
    earnings: list[EarningsEvent] = Field(default_factory=list, max_length=20)
    headlines: list[MarketHeadline] = Field(default_factory=list, max_length=10)
    context_timestamps: ContextTimestamps
    snapshot_hash: str = Field(pattern=r"^[a-f0-9]{64}$")

    @field_validator("timestamp", "playback_time")
    @classmethod
    def timestamp_must_include_timezone(cls, value: datetime) -> datetime:
        if value.tzinfo is None or value.utcoffset() is None:
            raise ValueError("validation timestamps must include a timezone")
        return value

    @model_validator(mode="after")
    def validate_snapshot(self) -> "MesOrbPullbackV2ValidationIn":
        if self.breakout.direction != self.proposed_trade.direction:
            raise ValueError("breakout and trade direction must match")
        if self.opening_range.high <= self.opening_range.low:
            raise ValueError("opening range high must exceed low")
        if self.breakout.direction == "long":
            if self.breakout.close <= self.opening_range.high:
                raise ValueError("long breakout must close above OR high")
            if self.pullback.trigger_price < self.opening_range.high:
                raise ValueError("long trigger must remain above OR high")
        else:
            if self.breakout.close >= self.opening_range.low:
                raise ValueError("short breakout must close below OR low")
            if self.pullback.trigger_price > self.opening_range.low:
                raise ValueError("short trigger must remain below OR low")
        if self.context_timestamps.market_data_known_at > self.playback_time:
            raise ValueError("market context cannot be known after playback time")
        if self.opening_range.end > self.playback_time:
            raise ValueError("opening range cannot end after playback time")
        if self.breakout.bar_timestamp > self.playback_time or self.pullback.bar_timestamp > self.playback_time:
            raise ValueError("signal bars cannot occur after playback time")
        for bar in [*self.bars_15m, *self.bars_60m, *self.bars_240m, *self.bars_daily]:
            if bar.timestamp > self.playback_time:
                raise ValueError("higher-timeframe bars cannot occur after playback time")
        for context_time in self.context_timestamps.model_dump().values():
            if context_time is not None and context_time > self.playback_time:
                raise ValueError("provider context cannot be known after playback time")
        for item in [*self.economic_events, *self.earnings, *self.headlines]:
            if item.known_at > self.playback_time:
                raise ValueError("point-in-time context contains lookahead data")
        for metric in self.market_regime.model_dump().values():
            if metric["known_at"] > self.playback_time:
                raise ValueError("market regime contains lookahead data")
        return self


class MesOrbPullbackV2Accepted(LlmValidationAccepted):
    snapshot_hash: str = Field(pattern=r"^[a-f0-9]{64}$")
    decided_at: datetime


class ContextStatus(BaseModel):
    status: Literal["ready", "stale", "unavailable", "disabled"]
    provider: str
    archive_ready: bool
    economic_events_refreshed_at: datetime | None = None
    earnings_refreshed_at: datetime | None = None
    constituents_refreshed_at: datetime | None = None
    headlines_refreshed_at: datetime | None = None
    economic_events_fresh: bool
    earnings_fresh: bool
    constituents_fresh: bool
    headlines_fresh: bool
    next_economic_event: str | None = None
    next_earnings: str | None = None
    detail: str
