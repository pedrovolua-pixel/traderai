import json
import os
import time
from typing import Protocol

import httpx

from app.models import (
    LlmProviderDecision,
    LlmValidationIn,
    MesOrbPullbackV2ValidationIn,
)


SYSTEM_INSTRUCTIONS = """You are a conservative veto gate for a simulation-only MES opening-range breakout strategy.
The deterministic strategy has already confirmed its mechanical entry rules. Evaluate only the supplied snapshot.
ALLOW only when the stated market structure is coherent in the bars, the breakout is clear rather than marginal or
choppy, and the structural stop remains sensible. REJECT ambiguous, contradictory, overextended, or poor-quality
setups. Never change the direction, quantity, stop, target, risk limit, or account. Use concise snake_case reason codes.
This is a veto decision, not a request for trading advice or a new trade plan."""


ORB_V2_SYSTEM_INSTRUCTIONS = """You are a risk gate for a simulation-only MES 15-minute opening-range
breakout strategy. The deterministic strategy owns direction, entry, quantity, and account scope,
and the required 2:1 reward-to-risk ratio. You may only recommend stop-loss and take-profit prices.

Evaluate only the immutable point-in-time snapshot. ALLOW only when the 15m/60m/240m/daily trend and regime,
breakout quality, volatility, and overextension are coherent for the proposed direction. Economic events,
weighted S&P constituent earnings, and headlines are optional inputs: if their lists are empty and their refresh
timestamps are null, perform price-action-only validation and do not reject merely because those optional fields
are absent. If present, assess their risk. REJECT contradictory, choppy, overextended, low-quality,
event-sensitive, stale provider data, or ambiguous candidates. On ALLOW, choose a
tick-aligned stop on the correct side of entry for one MES contract, and a take-profit
exactly two times that price risk from entry. On REJECT, both prices must be null. Events are judgment
inputs rather than hard blackout rules. Do not use tools, browse, assume facts outside the payload, or provide a
different direction, entry, or quantity. Return exactly the required schema with one or more allowed reason codes."""


V2_REASON_CODES = [
    "aligned_higher_timeframes",
    "clean_breakout_pullback",
    "acceptable_event_risk",
    "acceptable_earnings_risk",
    "price_action_only_context",
    "trend_conflict",
    "choppy_regime",
    "weak_breakout",
    "poor_pullback_quality",
    "overextended_entry",
    "excessive_volatility",
    "economic_event_risk",
    "earnings_concentration_risk",
    "headline_risk",
    "insufficient_context",
    "ambiguous_conditions",
]


DECISION_SCHEMA = {
    "type": "object",
    "properties": {
        "decision": {"type": "string", "enum": ["allow", "reject"]},
        "confidence": {"type": "number", "minimum": 0, "maximum": 1},
        "reason_codes": {
            "type": "array",
            "minItems": 1,
            "maxItems": 8,
            "items": {"type": "string", "pattern": "^[a-z0-9_]{1,64}$"},
        },
        "summary": {"type": "string", "minLength": 1, "maxLength": 256},
    },
    "required": ["decision", "confidence", "reason_codes", "summary"],
    "additionalProperties": False,
}


ORB_V2_DECISION_SCHEMA = {
    **DECISION_SCHEMA,
    "properties": {
        **DECISION_SCHEMA["properties"],
        "reason_codes": {
            "type": "array",
            "minItems": 1,
            "maxItems": 8,
            "items": {"type": "string", "enum": V2_REASON_CODES},
        },
        "stop_loss": {"anyOf": [{"type": "number"}, {"type": "null"}]},
        "take_profit": {"anyOf": [{"type": "number"}, {"type": "null"}]},
    },
    "required": [*DECISION_SCHEMA["required"], "stop_loss", "take_profit"],
}


class LlmValidator(Protocol):
    def validate(
        self, validation: LlmValidationIn | MesOrbPullbackV2ValidationIn
    ) -> LlmProviderDecision: ...


class DisabledLlmValidator:
    def __init__(self, reason: str = "openai_api_key_missing") -> None:
        self.reason = reason

    def validate(
        self, validation: LlmValidationIn | MesOrbPullbackV2ValidationIn
    ) -> LlmProviderDecision:
        return LlmProviderDecision(
            decision="reject",
            confidence=0,
            reason_codes=[self.reason],
            summary="LLM validation provider is unavailable; trade must fail closed.",
            provider="disabled",
            model="none",
            latency_ms=0,
        )


class OpenAIResponsesValidator:
    def __init__(
        self,
        api_key: str,
        model: str = "gpt-5.6-terra",
        timeout_seconds: float = 5.0,
        base_url: str = "https://api.openai.com/v1",
        client: httpx.Client | None = None,
    ) -> None:
        self.api_key = api_key
        self.model = model
        self.timeout_seconds = timeout_seconds
        self.base_url = base_url.rstrip("/")
        self.client = client or httpx.Client(timeout=timeout_seconds)

    def validate(
        self, validation: LlmValidationIn | MesOrbPullbackV2ValidationIn
    ) -> LlmProviderDecision:
        started = time.perf_counter()
        is_v2 = isinstance(validation, MesOrbPullbackV2ValidationIn)
        payload = {
            "model": self.model,
            "instructions": ORB_V2_SYSTEM_INSTRUCTIONS if is_v2 else SYSTEM_INSTRUCTIONS,
            "input": validation.model_dump_json(),
            "max_output_tokens": 300,
            "store": False,
            "reasoning": {"effort": "low"},
            "text": {
                "format": {
                    "type": "json_schema",
                    "name": "mes_orb_pullback_v2_validation" if is_v2 else "mes_orb_validation",
                    "strict": True,
                    "schema": ORB_V2_DECISION_SCHEMA if is_v2 else DECISION_SCHEMA,
                }
            },
        }
        try:
            response = self.client.post(
                f"{self.base_url}/responses",
                headers={
                    "Authorization": f"Bearer {self.api_key}",
                    "Content-Type": "application/json",
                },
                json=payload,
                timeout=self.timeout_seconds,
            )
            response.raise_for_status()
            parsed = self._parse_output(response.json())
            latency_ms = int((time.perf_counter() - started) * 1000)
            return LlmProviderDecision(
                **parsed,
                provider="openai",
                model=self.model,
                latency_ms=latency_ms,
            )
        except Exception as exc:
            latency_ms = int((time.perf_counter() - started) * 1000)
            reason = "llm_timeout" if isinstance(exc, httpx.TimeoutException) else "llm_provider_error"
            return LlmProviderDecision(
                decision="reject",
                confidence=0,
                reason_codes=[reason],
                summary=f"LLM validation failed closed ({type(exc).__name__}).",
                provider="openai",
                model=self.model,
                latency_ms=latency_ms,
            )

    @staticmethod
    def _parse_output(payload: dict) -> dict:
        if payload.get("status") != "completed":
            raise ValueError("OpenAI response did not complete")
        for item in payload.get("output", []):
            if item.get("type") != "message":
                continue
            for content in item.get("content", []):
                if content.get("type") == "refusal":
                    raise ValueError("OpenAI response was refused")
                if content.get("type") == "output_text":
                    return json.loads(content["text"])
        raise ValueError("OpenAI response contained no output_text")


def build_llm_validator_from_environment() -> LlmValidator:
    api_key = os.getenv("OPENAI_API_KEY", "").strip()
    if not api_key:
        return DisabledLlmValidator()

    model = os.getenv("TRADERAI_LLM_MODEL", "gpt-5.6-terra").strip()
    timeout = float(os.getenv("TRADERAI_LLM_TIMEOUT_SECONDS", "5.0"))
    base_url = os.getenv("OPENAI_BASE_URL", "https://api.openai.com/v1").strip()
    return OpenAIResponsesValidator(
        api_key=api_key,
        model=model,
        timeout_seconds=max(0.5, min(timeout, 5.0)),
        base_url=base_url,
    )
