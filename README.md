# TraderAI

Local NinjaTrader signal/audit bridge plus simulation-only strategies.

## MES ORB strategy

`strategies/MesOrbPullbackV2.cs` is the current strategy. It constructs the
complete 9:30-9:45 a.m. ET wick range on a 5-minute MES chart. A completed
5-minute close outside the range is the entry candidate. No pullback is required.
The LLM may veto market conditions and recommend a stop/target bracket;
infrastructure failures use the deterministic OR-boundary, exact-2R fallback.

`MesOrbStructureV1.cs` remains available only for compatibility and should stay
disabled. Saved V1 instances are not migrated to V2. Full instructions are in
`MES_ORB_PULLBACK_V2_DEPLOYMENT.md`.

Safety boundaries:

- Only the MES master instrument is accepted.
- Real-time execution is accepted only for exact account names `Sim101` and
  `Playback101`; changing the configurable account list cannot add another
  account type.
- Quantity is fixed to one contract.
- The bridge is a pre-entry market-condition gate for V2, but it is never responsible
  for order execution or protection after submission.
- The strategy is not automatically enabled on any chart.
- The V2 LLM cannot change direction, entry, quantity, account, or the exact 2R
  rule; its proposed stop and target must pass local validation.

After changing the strategy source, deploy it to the NinjaTrader custom folder
and compile it in NinjaTrader. `scripts/verify_ninjascript_compile.ps1` provides
an additional API/syntax compile against the locally installed NinjaTrader
assemblies, but it does not replace NinjaTrader's own compiler.

## Bridge

Create/activate a virtual environment, install `requirements.txt`, then run:

```powershell
python -m uvicorn app.main:app --host 127.0.0.1 --port 8000
```

Endpoints:

- `POST /validate/orb-v2` — point-in-time V2 veto decisions
- `GET /context/status` — redacted optional FMP cache health and next context
- `GET /health`
- `POST /signal` — backward-compatible signal intake and deterministic review
- `POST /events` — idempotent strategy audit events
- `POST /validate` — idempotent, schema-validated LLM veto decisions

Audit data is stored locally in `database/traderai.db`. No brokerage credentials
or full account identifiers are accepted by the event schema.

## LLM validation

`MesOrbStructureV1` exposes three validation modes:

- `Off` — deterministic strategy behavior; no validation request.
- `Shadow` — records the LLM decision without delaying or changing the trade.
- `Required` — submits a simulated entry only after an explicit, fresh `allow`
  response at or above the configured confidence threshold. Any timeout,
  malformed response, unavailable provider, stale response, or changed setup
  skips the entry.

The bridge uses the OpenAI Responses API with strict Structured Outputs. It
reads credentials only from the bridge process environment; credentials are
never sent to NinjaTrader or stored in SQLite.

Before starting the bridge, provide `OPENAI_API_KEY` in that PowerShell process.
Optional settings are:

```powershell
$env:TRADERAI_LLM_MODEL = "gpt-5.6-terra"
$env:TRADERAI_LLM_TIMEOUT_SECONDS = "5.0"
# Optional: enables cached economic, earnings, and headline context.
$env:FMP_API_KEY = "set-in-the-bridge-process-only"
```

Restart the bridge after changing environment variables. Start in `Shadow`
mode on `Playback101`; do not use `Required` until shadow decisions have been
evaluated. The validation endpoint is deliberately restricted by NinjaScript
to a loopback URL such as `http://127.0.0.1:8000/validate`.

## Tests

```powershell
python -m pytest -q
powershell -ExecutionPolicy Bypass -File scripts/verify_ninjascript_compile.ps1
```

Backtesting, Playback testing, and the 20-session `Sim101` forward test remain
required before designing a separate live-account version. Historical or
simulated results do not guarantee profitability.
