from datetime import datetime
from typing import Literal

from pydantic import BaseModel, ConfigDict, Field, field_validator


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
