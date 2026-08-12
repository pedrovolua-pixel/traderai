# MES ORB Pullback V2 — Deployment and Usage

`MesOrbPullbackV2` is simulation-only. It accepts only accounts exactly named
`Sim101` or `Playback101`, and quantity is hard-limited to one MES contract. Do
not enable it on a live account. No strategy or LLM can guarantee profitability,
a stop fill, or a maximum realized loss during gaps or slippage.

## What the strategy does

1. On a 5-minute MES chart, it combines the three completed bars from 9:30
   through 9:45 a.m. Eastern into one opening-range candle. The highest wick and
   lowest wick are the OR boundaries.
2. A 5-minute candle must close strictly above the OR high for long bias or
   strictly below the OR low for short bias. A wick outside is ignored.
3. That completed breakout close is the entry candidate. V2 no longer waits for
   an OR retest or swing pullback. An opposite close outside the OR replaces the
   current bias before an entry has been submitted.
4. The bridge asks the LLM both to veto/allow the market conditions and, on an
   allow, recommend the stop-loss and take-profit prices.
5. LLM prices are accepted only when they are on the correct sides of entry,
   tick-aligned, and form exactly a 2:1 target. V2 has no per-trade risk cap.
6. A genuine market-condition `reject` remains a veto. Missing keys, stale or
   incomplete context, provider errors, malformed output, low-confidence output,
   and timeout use the deterministic bracket instead of cancelling the trade.
7. The deterministic stop is one tick through the broken OR boundary with no
   dollar-risk cap; the target is exactly 2R. Native NinjaTrader stop and
   target orders are configured before market entry. The stop remains fixed;
   only the target is adjusted after fill to preserve exactly 2R.
8. There is no entry cutoff. After an exit, price must close back inside the OR
   before another same-direction breakout can qualify. It flattens at 3:55 p.m.

The LLM cannot change direction, entry, quantity, account, or the
2R rule. It can recommend only stop and target prices inside those constraints.

## Files

- `C:\TraderAI\strategies\MesOrbPullbackV2.cs` — V2 strategy
- `C:\TraderAI\strategies\MesOrbStructureV1.cs` — disabled compatibility V1
- `C:\TraderAI\app\main.py` — local API
- `C:\TraderAI\app\market_context.py` — FMP cache
- `C:\TraderAI\app\llm_validator.py` — OpenAI veto gate
- `C:\TraderAI\database\traderai.db` — local audit database

## 1. Configure credentials

Open PowerShell. Keep credentials in the bridge process only; never place them
in NinjaTrader properties or source code.

```powershell
cd C:\TraderAI
$openAiSecure = Read-Host 'OpenAI API key' -AsSecureString
$env:OPENAI_API_KEY = [System.Net.NetworkCredential]::new('', $openAiSecure).Password
$env:TRADERAI_LLM_MODEL = 'gpt-5.6-terra'
$env:TRADERAI_LLM_TIMEOUT_SECONDS = '5.0'
```

`FMP_API_KEY` is optional. Without it, current `Sim101` candidates are sent to
the LLM with the deterministic MES price/action, higher-timeframe, volatility,
and ORB context only. Economic calendar, earnings, and headline fields remain
empty and are not treated as an error. To include cached external context,
add this before starting the bridge:

```powershell
$fmpSecure = Read-Host 'FMP API key (optional)' -AsSecureString
$env:FMP_API_KEY = [System.Net.NetworkCredential]::new('', $fmpSecure).Password
```

The bridge uses the OpenAI Responses API with `store: false`, strict structured
output, and low reasoning effort, following current official
[OpenAI model guidance](https://developers.openai.com/api/docs/guides/latest-model).

Normally leave these optional overrides unset:

```powershell
$env:OPENAI_BASE_URL = 'https://api.openai.com/v1'
$env:FMP_BASE_URL = 'https://financialmodelingprep.com/stable'
```

## 2. Start and verify the bridge

```powershell
cd C:\TraderAI
if (-not (Test-Path .venv)) { python -m venv .venv }
.\.venv\Scripts\python.exe -m pip install -r requirements.txt
.\.venv\Scripts\python.exe -m uvicorn app.main:app --host 127.0.0.1 --port 8000
```

Leave that window open. In another PowerShell window:

```powershell
Invoke-RestMethod http://127.0.0.1:8000/health
Invoke-RestMethod http://127.0.0.1:8000/context/status | Format-List
```

Prefer enabling V2 when context status is `ready`. The bridge refreshes economic
events every 10 minutes, earnings every 3 hours, estimated top-20 S&P weights
daily, and headlines every 10 minutes. Validation performs no live web search;
it uses this bounded cache. Missing keys, provider errors, stale data, malformed
output, or timeouts activate the deterministic stop/target fallback.

## 3. Deploy and compile NinjaScript

Disable every V1/V2 instance first, then copy the source:

```powershell
$source = 'C:\TraderAI\strategies\MesOrbPullbackV2.cs'
$destination = Join-Path $env:USERPROFILE 'Documents\NinjaTrader 8\bin\Custom\Strategies\MesOrbPullbackV2.cs'
Copy-Item -LiteralPath $source -Destination $destination -Force
```

In NinjaTrader:

1. Open **New → NinjaScript Editor**.
2. Right-click and choose **Compile**, or press **F5**.
3. Confirm there are no errors.
4. Leave `MesOrbStructureV1` disabled and create a new V2 instance. Do not reuse
   a saved V1 template.

Repository-side installed-assembly check:

```powershell
cd C:\TraderAI
powershell -ExecutionPolicy Bypass -File scripts\verify_ninjascript_compile.ps1
```

This does not replace NinjaTrader's own F5 compile.

## 4. Create the chart

1. Connect to Playback for `Playback101`, or your data feed for `Sim101`.
2. Open **New → Chart** and select the current liquid MES contract.
3. Set **Type: Minute**, **Value: 5**.
4. Select the CME US index-futures ETH trading-hours template.
5. Load enough days for the internal daily and higher-timeframe series.
6. Add `MesOrbPullbackV2` and select exactly `Playback101` or `Sim101`.
7. Keep all endpoints on `127.0.0.1`, quantity at `1`, and enable only when the
   account is flat with no working MES orders.

Scheduling is converted to Eastern Time internally. The chart displays the OR,
bias, breakout state, validation count, LLM state, optional external-context
status, next cached economic event/earnings when FMP is enabled, planned risk,
lock state, and position.

## 5. Playback point-in-time limitation

Every context item has `known_at`. The API rejects bars, events, actuals,
earnings results, headlines, or refresh timestamps later than the Playback clock.

The current FMP cache is not a historical point-in-time database. The LLM-only
mode is permitted for current `Sim101` use. On an old Playback session,
unavailable trustworthy external context fails closed; it never injects today's
news into an old replay. Snapshot
hashes replay a stored decision only when the immutable request hash also
matches. Recent/current Playback is suitable for learning mechanics; old CPI,
payroll, FOMC, and earnings sessions require an archive first.

## 6. Daily checklist

- Bridge is running; `/health` is `ok`; `/context/status` is either `ready`
  (FMP enabled) or `disabled` (LLM-only mode).
- Chart is current MES, 5-minute, CME index-futures ETH.
- Account is exactly `Sim101` or `Playback101`, flat, with no working MES orders.
- V1 is disabled and only one V2 instance controls MES on the account.
- The 9:45 OR matches the three 5-minute bars including wicks.
- Native stop and target appear immediately after every fill.
- The strategy is flat at 3:55 p.m. ET.

Connection loss, order/protection rejection, or position mismatch stops the
strategy and requires manual re-enablement. Investigate NinjaTrader Log/Trace
and SQLite audit records first.

## 7. Tests and rollout gates

```powershell
cd C:\TraderAI
.\.venv\Scripts\python.exe -m pytest -q
powershell -ExecutionPolicy Bypass -File scripts\verify_ninjascript_compile.ps1
```

Before any later live-account design, still complete:

- Playback scenarios for trends, ranges, failed/double breaks, gaps, major
  releases, FOMC, and weighted-constituent earnings.
- At least 100 labeled candidates comparing LLM and human labels.
- At least 20 `Sim101` sessions with zero unprotected fills, duplicates,
  lookahead, stale approvals, orphan orders, scope violations, or unexplained
  rejections.
- A non-optimized two-year MES backtest with commissions and conservative
  slippage. Required-mode historical runs need a point-in-time context archive.

Those acceptance runs are not produced merely by compiling and are not claimed
complete here.

## Audit query

```powershell
cd C:\TraderAI
.\.venv\Scripts\python.exe -c "import sqlite3; c=sqlite3.connect(r'database\traderai.db'); print(c.execute('select validation_id,decision,confidence,reason_codes,latency_ms from orb_v2_validations order by validation_db_id desc limit 20').fetchall())"
```

Credentials and full account identifiers are not accepted by the schemas or
stored in audit rows.
