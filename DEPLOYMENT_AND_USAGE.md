# TraderAI MES ORB + LLM Deployment and Usage Guide

This guide covers installation, OpenAI API configuration, NinjaTrader deployment,
safe simulation operation, updates, and troubleshooting for `MesOrbStructureV1`.

> **Simulation only:** Version 1 permits only the exact NinjaTrader account names
> `Sim101` and `Playback101`. It is not approved or designed for a live account.
> Futures trading is risky, and neither deterministic rules nor an LLM can guarantee
> fills, stop execution, or profitability.

## 1. How the system works

There are two independent runtime components:

1. **NinjaTrader 8** calculates the opening range and five-minute market structure,
   decides whether a deterministic setup exists, submits the simulated order, and
   manages the native stop and target.
2. **The TraderAI Python bridge** receives audit events and, when requested, sends a
   redacted market snapshot to the OpenAI Responses API for a veto-only decision.

The LLM cannot change the trade direction, quantity, entry, stop, target, account,
or risk limits. It can only return `allow` or `reject`. NinjaTrader never depends on
the bridge for protective-order management.

Codex does not need to remain open while the system runs.

## 2. LLM modes

| Mode | Behavior |
|---|---|
| `Off` | No LLM request. The deterministic strategy operates by itself. |
| `Shadow` | The LLM decision is recorded, but it cannot delay or veto the trade. Use this first. |
| `Required` | NinjaTrader waits for a fresh `allow` decision above the confidence threshold. Any error or uncertainty skips the entry. |

`Required` is fail-closed. A missing API key, provider error, timeout, malformed
response, refusal, stale response, low confidence, or changed setup results in no
entry.

## 3. Files and locations

Repository:

```text
C:\TraderAI
```

Important files:

```text
C:\TraderAI\strategies\MesOrbStructureV1.cs
C:\TraderAI\app\main.py
C:\TraderAI\app\llm_validator.py
C:\TraderAI\database\traderai.db
C:\TraderAI\requirements.txt
```

Installed NinjaTrader strategy:

```text
%USERPROFILE%\Documents\NinjaTrader 8\bin\Custom\Strategies\MesOrbStructureV1.cs
```

## 4. Prerequisites

- Windows with NinjaTrader 8 installed.
- NinjaTrader connected to simulation or Playback data.
- A one-minute MES chart using the CME index-futures ETH trading-hours template.
- Python 3.10 or newer.
- An OpenAI API account and API key for LLM validation.
- MES historical data for backtesting and Playback validation.

Creating an OpenAI API key is separate from signing in to NinjaTrader, ChatGPT, or
Codex. Create and manage keys in the
[OpenAI API dashboard](https://platform.openai.com/api-keys). OpenAI recommends
loading API keys from an environment variable and not exposing them in client code.
See the [official OpenAI quickstart](https://developers.openai.com/api/docs/quickstart).

## 5. First-time Python bridge setup

Open a normal PowerShell window and run:

```powershell
Set-Location -LiteralPath 'C:\TraderAI'

py -3 -m venv .venv
& '.\.venv\Scripts\python.exe' -m pip install --upgrade pip
& '.\.venv\Scripts\python.exe' -m pip install -r requirements.txt
```

If `py` is unavailable, install Python and ensure it is available from PowerShell.
The repository may already contain `.venv`; recreating it is unnecessary when the
existing environment works.

Run the automated tests:

```powershell
Set-Location -LiteralPath 'C:\TraderAI'
& '.\.venv\Scripts\python.exe' -m pytest -q
powershell -ExecutionPolicy Bypass -File '.\scripts\verify_ninjascript_compile.ps1'
```

The second command compiles the strategy against the installed NinjaTrader API
assemblies. It supplements, but does not replace, NinjaTrader's own F5 compile.

## 6. Set the OpenAI API key securely

### Recommended: session-only key

Use the same PowerShell window that will start the bridge. This avoids saving the
key in the repository or PowerShell command history:

```powershell
$traderAiSecureKey = Read-Host 'OpenAI API key' -AsSecureString
$env:OPENAI_API_KEY = [System.Net.NetworkCredential]::new('', $traderAiSecureKey).Password
Remove-Variable traderAiSecureKey
```

Confirm only that a value is present; do not print the key:

```powershell
if ([string]::IsNullOrWhiteSpace($env:OPENAI_API_KEY)) {
    'OPENAI_API_KEY is missing'
} else {
    'OPENAI_API_KEY is present'
}
```

The session-only key disappears when that PowerShell process closes.

### Optional: persistent user environment variable

For unattended startup, use Windows **System Properties → Environment Variables**
and create a user variable named `OPENAI_API_KEY`. Open a new PowerShell window
afterward. Avoid putting the key in this repository, a `.ps1` file, screenshots,
logs, or chat messages.

The current application does not automatically read a `.env` file.

### Optional LLM settings

The default model is `gpt-5.4-nano`, a low-latency, cost-sensitive model that
supports the Responses API and Structured Outputs. See the
[official model page](https://developers.openai.com/api/docs/models/gpt-5.4-nano).

Set optional values before starting the bridge:

```powershell
$env:TRADERAI_LLM_MODEL = 'gpt-5.4-nano'
$env:TRADERAI_LLM_TIMEOUT_SECONDS = '2.5'
```

The provider timeout is restricted by the application to 0.5–10 seconds. Keep it
below NinjaTrader's `Decision timeout (ms)`, which defaults to 3500 ms.

`OPENAI_BASE_URL` defaults to `https://api.openai.com/v1` and normally should not
be changed.

## 7. Start and verify the bridge

Start the bridge from the same PowerShell process containing the API key:

```powershell
Set-Location -LiteralPath 'C:\TraderAI'
& '.\.venv\Scripts\python.exe' -m uvicorn app.main:app --host 127.0.0.1 --port 8000
```

Leave this window open. In a second PowerShell window, verify health:

```powershell
Invoke-RestMethod -Method Get -Uri 'http://127.0.0.1:8000/health'
```

Expected response:

```text
status
------
ok
```

Available local endpoints:

| Endpoint | Purpose |
|---|---|
| `GET /health` | Bridge health check |
| `POST /events` | Idempotent strategy audit events |
| `POST /validate` | Idempotent LLM veto decisions |
| `POST /signal` | Backward-compatible legacy signal endpoint |

Stop the bridge with `Ctrl+C` in its PowerShell window.

Whenever you change an environment variable, stop and restart the bridge. An
already-running process cannot see variables added to a different PowerShell
window.

## 8. Deploy and compile the NinjaTrader strategy

Close or disable every existing `MesOrbStructureV1` instance before updating its
source. Confirm the strategy position is flat and that there are no working orders.

Copy the source into NinjaTrader:

```powershell
$traderAiSource = 'C:\TraderAI\strategies\MesOrbStructureV1.cs'
$ninjaDestination = Join-Path $env:USERPROFILE 'Documents\NinjaTrader 8\bin\Custom\Strategies\MesOrbStructureV1.cs'
Copy-Item -LiteralPath $traderAiSource -Destination $ninjaDestination -Force
```

Then compile inside NinjaTrader:

1. Open **Control Center → New → NinjaScript Editor**.
2. Expand **Strategies** and open `MesOrbStructureV1`.
3. Press **F5**.
4. Wait for compilation to finish.
5. Confirm the Errors grid is empty.

Do not enable the strategy if any compilation error remains. After a material code
update, remove and re-add the chart strategy instance so NinjaTrader loads the new
default properties.

## 9. Create the MES chart and add the strategy

1. Open **Control Center → New → Chart**.
2. Select the active MES futures contract.
3. Use a **1 Minute** primary data series.
4. Select the CME index-futures **ETH** trading-hours template.
5. Confirm the workstation/NinjaTrader time zone is Eastern Time. Strategy schedule
   values are ET.
6. Open the chart's **Strategies** dialog.
7. Add `MesOrbStructureV1`.
8. Select exactly `Playback101` or `Sim101` as the account.
9. Review every property before enabling it.

The strategy internally adds its own five-minute series. Do not add a second strategy
instance to another MES chart using the same account.

### Default strategy properties

| Property | Default |
|---|---:|
| Opening range start | `093000` ET |
| Opening range end | `094500` ET |
| Flatten time | `155500` ET |
| Structure lookback start | `083000` ET |
| Pivot strength | `2` |
| Quantity | `1` MES |
| Maximum planned risk | `$50` |
| Reward multiple | `2.0` |
| Daily loss lock | `$100` |
| Allowed accounts | `Sim101,Playback101` |
| Audit endpoint | `http://127.0.0.1:8000/events` |
| Validation mode | `Off` |
| Validation endpoint | `http://127.0.0.1:8000/validate` |
| Decision timeout | `3500` ms |
| Maximum decision age | `6` seconds |
| Minimum allow confidence | `0.70` |

The allowed-account property can narrow the built-in allowlist but cannot authorize
a live account. The validation endpoint must be a loopback address.

## 10. Recommended rollout

Follow this progression without skipping stages:

### Stage 1: deterministic Playback

- Use `Playback101`.
- Set Validation mode to `Off`.
- Verify opening-range calculation, structure pivots, entries, stop/target behavior,
  one-trade lock, and 3:55 p.m. flattening.

### Stage 2: LLM Shadow

- Start the bridge with `OPENAI_API_KEY` configured.
- Set Validation mode to `Shadow`.
- Playback representative trend, range, gap, and high-volatility sessions.
- Compare the recorded LLM decisions with deterministic outcomes.
- Confirm that Shadow decisions never change entry timing.

### Stage 3: LLM Required in Playback

- Set Validation mode to `Required`.
- Verify allowed setups, rejected setups, timeout behavior, stale responses, bridge
  shutdown, and network interruption.
- Confirm that every unavailable or ambiguous validation results in no entry.

### Stage 4: Sim101 forward test

- Run at least 20 trading sessions on `Sim101`.
- Require zero unprotected fills, duplicate entries, orphaned orders,
  account-scope violations, or unexplained order rejections.

Live-account support is outside Version 1.

## 11. Daily operating checklist

Complete this before the 9:30 a.m. ET opening range begins:

- [ ] NinjaTrader is connected to the intended simulation data connection.
- [ ] The active MES contract and one-minute ETH chart are correct.
- [ ] The account is exactly `Playback101` or `Sim101`.
- [ ] The account and strategy positions are flat.
- [ ] There are no unexpected working orders.
- [ ] The TraderAI bridge is running on `127.0.0.1:8000`.
- [ ] `/health` returns `ok`.
- [ ] For Shadow/Required mode, the bridge process has `OPENAI_API_KEY`.
- [ ] The desired LLM mode and confidence threshold are selected.
- [ ] NinjaTrader chart status shows the expected OR/structure and LLM state.
- [ ] Only one `MesOrbStructureV1` instance is enabled for the account.

After the session:

- Confirm the strategy and account are flat.
- Confirm there are no working protective orders.
- Review NinjaTrader's Log and Strategies tabs.
- Review audit and LLM validation records when investigating a decision.

## 12. Inspect the audit database

The SQLite database is:

```text
C:\TraderAI\database\traderai.db
```

Show recent LLM decisions without displaying full market snapshots:

```powershell
Set-Location -LiteralPath 'C:\TraderAI'
& '.\.venv\Scripts\python.exe' -c "import sqlite3; c=sqlite3.connect(r'database/traderai.db'); rows=c.execute('select timestamp,instrument,direction,decision,confidence,reason_codes,provider,model,latency_ms from llm_validations order by validation_db_id desc limit 20').fetchall(); [print(r) for r in rows]"
```

Show recent strategy audit events:

```powershell
Set-Location -LiteralPath 'C:\TraderAI'
& '.\.venv\Scripts\python.exe' -c "import sqlite3; c=sqlite3.connect(r'database/traderai.db'); rows=c.execute('select timestamp,event_type,instrument,direction,price,reason_code from trade_events order by event_db_id desc limit 30').fetchall(); [print(r) for r in rows]"
```

Do not edit or delete the database while the bridge is running. Back it up only when
the bridge is stopped.

## 13. Troubleshooting

### `/health` does not respond

- Confirm the bridge PowerShell window is still open.
- Confirm the command was started from `C:\TraderAI`.
- Check whether port 8000 is already in use:

```powershell
Get-NetTCPConnection -LocalPort 8000 -ErrorAction SilentlyContinue
```

Identify the owning process before stopping anything.

### `openai_api_key_missing`

The key was not present in the environment of the process that launched Uvicorn.
Stop the bridge, set the key in that same PowerShell window, and start it again.

### `llm_provider_error`

Possible causes include an invalid or revoked key, unavailable model access, account
limits, a network/TLS problem, or an OpenAI API error. Review the bridge output and
the OpenAI API dashboard. Do not weaken Required mode to force a trade through.

### `llm_timeout`

The OpenAI request exceeded `TRADERAI_LLM_TIMEOUT_SECONDS`. Confirm network health.
If testing shows more time is consistently required, raise both timeouts carefully
while keeping the bridge timeout below NinjaTrader's decision timeout. Longer waits
can make decisions stale.

### `llm_response_stale`

The market moved or the response exceeded the configured maximum decision age. This
is an intentional rejection. Do not automatically increase the age threshold.

### `llm_confidence_below_threshold`

The LLM returned `allow`, but its confidence was below the NinjaTrader threshold.
The trade is skipped by design.

### `setup_changed_while_waiting` or `market_returned_inside_range`

The setup was no longer equivalent when the response arrived. NinjaTrader discarded
the decision instead of applying it to a changed market.

### `llm_endpoint_must_be_loopback`

Use `http://127.0.0.1:8000/validate` or another loopback URL. The strategy refuses a
remote validation endpoint.

### `account_not_allowed`

The selected account is not exactly `Sim101` or `Playback101`, or it was removed from
the narrower `Allowed accounts` property. Disable the strategy and correct the
account selection.

### Strategy properties do not show the LLM section

NinjaTrader is using an older compiled DLL:

1. Disable the strategy.
2. Confirm the updated `.cs` file is in the NinjaTrader custom Strategies folder.
3. Open NinjaScript Editor and press F5.
4. Confirm zero compile errors.
5. Remove and re-add the chart strategy instance.

### Strategy locks itself

Review the NinjaTrader chart status, Strategies tab, Log tab, and audit events. A
safety lock requires manual review and re-enablement; do not immediately toggle the
strategy back on without identifying the reason.

## 14. Updating TraderAI

For future code updates:

1. Disable `MesOrbStructureV1` and confirm flat positions and no working orders.
2. Stop the Python bridge with `Ctrl+C`.
3. Back up `C:\TraderAI\database\traderai.db` if audit retention matters.
4. Apply the code update.
5. Reinstall dependencies from `requirements.txt`.
6. Run all tests and the NinjaScript API compile.
7. Copy the updated strategy into NinjaTrader's custom Strategies folder.
8. Compile with F5 inside NinjaTrader.
9. Restart the bridge with the API key and optional variables configured.
10. Verify `/health`, then resume in Playback or Shadow mode first.

## 15. Safety boundaries that must not be removed

- MES instrument only.
- One contract only.
- `Sim101` and `Playback101` only.
- One entry submission per trading day.
- Maximum planned risk of $50 by default.
- Daily realized-loss lock of $100 by default.
- Native stop and target configured before entry.
- Forced flattening at 3:55 p.m. ET.
- No overnight holding.
- Fail-closed handling for connection, order, protection, and Required-LLM errors.
- Manual review after a safety lock.

Backtests and LLM opinions do not remove execution risk, slippage, gaps, data errors,
or model error. Keep the system in simulation until the complete acceptance plan has
been satisfied.
