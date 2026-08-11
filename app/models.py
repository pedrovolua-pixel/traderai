from datetime import datetime
from typing import Literal

from pydantic import BaseModel, ConfigDict, Field


class SignalIn(BaseModel):
    model_config = ConfigDict(extra="forbid", str_strip_whitespace=True)

    instrument: str = Field(min_length=1)
    strategy: str = Field(min_length=1)
    direction: Literal["long", "short"]
    timeframe: str = Field(min_length=1)
    price: float
    timestamp: datetime


class SignalAccepted(BaseModel):
    accepted: Literal[True] = True
    signal_id: int

