#region Using declarations
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public enum OrbV2HistoricalGateMode
    {
        FailClosed,
        DeterministicOnly
    }

    /// <summary>
    /// Simulation-only MES 15-minute opening-range breakout strategy.
    /// The first completed 5-minute close outside the range is the candidate.
    /// The LLM may approve and price the bracket; infrastructure failures use
    /// a deterministic $50-max-risk stop and an exact 2R target.
    /// </summary>
    public class MesOrbPullbackV2 : Strategy
    {
        private const string LongEntrySignal = "MES_ORB_V2_LONG";
        private const string ShortEntrySignal = "MES_ORB_V2_SHORT";
        private const string StrategyCode = "mes_orb_pullback_v2";
        private const int AuditQueueCapacity = 128;
        private const int LlmQueueCapacity = 4;
        private const int PointValueDollars = 5;

        private sealed class TradePlan
        {
            public int Direction;
            public string SetupType;
            public string CandidateId;
            public DateTime CandidateTime;
            public double TriggerPrice;
            public double StructuralPrice;
            public double EntryReference;
            public double Stop;
            public double Target;
            public double RiskDollars;
            public double Quality;
            public int BarsSinceBreakout;
        }

        private sealed class LlmWork
        {
            public string ValidationId;
            public string SnapshotHash;
            public string Json;
            public TradePlan Plan;
            public DateTime RequestedAtUtc;
        }

        private sealed class LlmResult
        {
            public string ValidationId;
            public string SnapshotHash;
            public TradePlan Plan;
            public DateTime RequestedAtUtc;
            public string Decision;
            public double Confidence;
            public string Reason;
            public string Provider;
            public string Model;
            public double StopLoss;
            public double TakeProfit;
        }

        private sealed class BridgeContextResult
        {
            public string Status;
            public string NextEvent;
            public string NextEarnings;
        }

        private readonly HashSet<string> validatedCandidates = new HashSet<string>(StringComparer.Ordinal);
        private BlockingCollection<string> auditQueue;
        private CancellationTokenSource auditCancellation;
        private Task auditWorker;
        private BlockingCollection<LlmWork> llmQueue;
        private CancellationTokenSource llmCancellation;
        private Task llmWorker;
        private TimeZoneInfo easternTimeZone;

        private DateTime activeDate = DateTime.MinValue;
        private string strategyInstanceId;
        private long eventSequence;
        private long validationSequence;
        private bool runtimeValidated;
        private bool safetyLocked;
        private bool entryAttempted;
        private bool flattenIssued;
        private bool validationPending;
        private string pendingValidationId;
        private int validationsToday;
        private string llmStatus;
        private string contextStatus;
        private string nextContextEvent;
        private string nextContextEarnings;
        private bool contextPollPending;
        private DateTime lastContextPollUtc;

        private bool orStarted;
        private bool orComplete;
        private double orOpen;
        private double orHigh;
        private double orLow;
        private double orClose;
        private int orBarCount;
        private int orStartAbsoluteIndex;
        private int bias;
        private DateTime breakoutTime;
        private int breakoutAbsoluteIndex;
        private double breakoutClose;

        private double overnightHigh;
        private double overnightLow;
        private double rthHigh;
        private double rthLow;
        private double rthVolume;
        private double rthTypicalVolume;

        private double plannedStopPrice;
        private double plannedTargetPrice;
        private double plannedRiskDollars;
        private double entryFillPrice;
        private double dailyRealizedPnl;
        private int activeTradeDirection;
        private int entryAbsoluteIndex;
        private Order entryOrder;

        [NinjaScriptProperty]
        [Range(93000, 93000)]
        [Display(Name = "OR start (ET HHmmss)", Order = 1, GroupName = "Schedule")]
        public int OpeningRangeStart { get; set; }

        [NinjaScriptProperty]
        [Range(94500, 94500)]
        [Display(Name = "OR end (ET HHmmss)", Order = 2, GroupName = "Schedule")]
        public int OpeningRangeEnd { get; set; }

        [NinjaScriptProperty]
        [Range(155500, 155500)]
        [Display(Name = "Flatten time (ET HHmmss)", Order = 3, GroupName = "Schedule")]
        public int FlattenTime { get; set; }

        [NinjaScriptProperty]
        [Range(1, 1)]
        [Display(Name = "Quantity", Order = 1, GroupName = "Risk")]
        public int TradeQuantity { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, 10.0)]
        [Display(Name = "Reward multiple", Order = 2, GroupName = "Risk")]
        public double RewardMultiple { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, 100.0)]
        [Display(Name = "Daily loss lock ($)", Order = 3, GroupName = "Risk")]
        public double DailyLossLimit { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Allowed accounts", Description = "May only narrow Sim101/Playback101.", Order = 1, GroupName = "Safety")]
        public string AllowedAccountNames { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Audit endpoint", Order = 1, GroupName = "Bridge")]
        public string AuditEndpoint { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Required validation endpoint", Order = 2, GroupName = "Bridge")]
        public string ValidationEndpoint { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Context status endpoint", Order = 3, GroupName = "Bridge")]
        public string ContextStatusEndpoint { get; set; }

        [NinjaScriptProperty]
        [Range(5000, 5000)]
        [Display(Name = "LLM timeout (ms)", Order = 4, GroupName = "Bridge")]
        public int LlmTimeoutMilliseconds { get; set; }

        [NinjaScriptProperty]
        [Range(10, 10)]
        [Display(Name = "Maximum decision age (seconds)", Order = 4, GroupName = "Bridge")]
        public int MaximumDecisionAgeSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(0.75, 0.75)]
        [Display(Name = "Minimum allow confidence", Order = 5, GroupName = "Bridge")]
        public double MinimumAllowConfidence { get; set; }

        [NinjaScriptProperty]
        [Range(3, 3)]
        [Display(Name = "Maximum validations per day", Order = 6, GroupName = "Bridge")]
        public int MaximumValidationsPerDay { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Historical gate", Description = "Analyzer-only deterministic mode never applies to Playback or real-time simulation.", Order = 1, GroupName = "Backtest")]
        public OrbV2HistoricalGateMode HistoricalGateMode { get; set; }

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "SIM ONLY: MES 9:30-9:45 ET wick OR, direct 5-minute close breakout, LLM bracket with deterministic fallback.";
                Name = "MesOrbPullbackV2";
                Calculate = Calculate.OnBarClose;
                IsOverlay = true;
                BarsRequiredToTrade = 20;
                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.UniqueEntries;
                StopTargetHandling = StopTargetHandling.PerEntryExecution;
                RealtimeErrorHandling = RealtimeErrorHandling.StopCancelClose;
                StartBehavior = StartBehavior.WaitUntilFlat;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 300;
                TimeInForce = TimeInForce.Day;
                TraceOrders = true;
                IncludeCommission = true;
                IsInstantiatedOnEachOptimizationIteration = false;
                PrintTo = PrintTo.OutputTab1;

                OpeningRangeStart = 93000;
                OpeningRangeEnd = 94500;
                FlattenTime = 155500;
                TradeQuantity = 1;
                RewardMultiple = 2.0;
                DailyLossLimit = 100.0;
                AllowedAccountNames = "Sim101,Playback101";
                AuditEndpoint = "http://127.0.0.1:8000/events";
                ValidationEndpoint = "http://127.0.0.1:8000/validate/orb-v2";
                ContextStatusEndpoint = "http://127.0.0.1:8000/context/status";
                LlmTimeoutMilliseconds = 5000;
                MaximumDecisionAgeSeconds = 10;
                MinimumAllowConfidence = 0.75;
                MaximumValidationsPerDay = 3;
                HistoricalGateMode = OrbV2HistoricalGateMode.FailClosed;
            }
            else if (State == State.Configure)
            {
                AddDataSeries(BarsPeriodType.Minute, 15);
                AddDataSeries(BarsPeriodType.Minute, 60);
                AddDataSeries(BarsPeriodType.Minute, 240);
                AddDataSeries(BarsPeriodType.Day, 1);
            }
            else if (State == State.DataLoaded)
            {
                easternTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
                strategyInstanceId = Name + "-" + Guid.NewGuid().ToString("N");
                StartAuditWorker();
                StartLlmWorker();
                ResetTradingDay(DateTime.MinValue);
            }
            else if (State == State.Realtime)
            {
                runtimeValidated = false;
                safetyLocked = false;
                entryAttempted = false;
                flattenIssued = false;
                validationPending = false;
                pendingValidationId = null;
                entryOrder = null;
                llmStatus = "required-ready";
                contextStatus = "prefetched bridge context required";
            }
            else if (State == State.Terminated)
            {
                StopLlmWorker();
                StopAuditWorker();
            }
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0 || CurrentBar < 2)
                return;

            DateTime easternBarTime = ToEastern(Time[0]);
            DateTime tradeDate = TradingDate(easternBarTime);
            EnsureTradingDay(tradeDate);

            if (!ValidateStaticConfiguration())
            {
                RenderStatus();
                return;
            }
            if (State == State.Realtime && !ValidateRealtimeEnvironment())
            {
                RenderStatus();
                return;
            }

            int now = ToTime(easternBarTime);
            QueueContextStatusPoll();
            UpdateSessionContext(easternBarTime, now);
            if (now >= FlattenTime)
            {
                FlattenForSessionEnd();
                RenderStatus();
                return;
            }

            BuildOpeningRange(easternBarTime, now);
            DrawDailyOpeningRange();
            if (orComplete && now > OpeningRangeEnd && !entryAttempted && !safetyLocked
                && Position.MarketPosition == MarketPosition.Flat && !validationPending)
                ProcessBiasAndPullbacks(easternBarTime);

            if (State == State.Realtime && Position.MarketPosition != PositionAccount.MarketPosition)
                TriggerSafetyLock("strategy_account_position_mismatch", true);
            RenderStatus();
        }

        private void BuildOpeningRange(DateTime easternBarTime, int now)
        {
            if (now > OpeningRangeStart && now <= OpeningRangeEnd)
            {
                if (!orStarted)
                {
                    orStarted = true;
                    orOpen = Open[0];
                    orHigh = High[0];
                    orLow = Low[0];
                    orBarCount = 1;
                    orStartAbsoluteIndex = CurrentBar;
                }
                else
                {
                    orHigh = Math.Max(orHigh, High[0]);
                    orLow = Math.Min(orLow, Low[0]);
                    orBarCount++;
                }
                orClose = Close[0];
            }

            if (!orComplete && orStarted && now >= OpeningRangeEnd)
            {
                if (orBarCount != 3)
                {
                    TriggerSafetyLock("opening_range_requires_exactly_three_five_minute_bars", State == State.Realtime);
                    return;
                }
                orComplete = true;
            }
        }

        private void DrawDailyOpeningRange()
        {
            if (!orComplete || orStartAbsoluteIndex < 0)
                return;
            int barsAgo = Math.Max(0, CurrentBar - orStartAbsoluteIndex);
            string date = activeDate.ToString("yyyyMMdd");
            Draw.Line(this, "MES_ORB_V2_HIGH_" + date, false, barsAgo, orHigh, 0, orHigh,
                Brushes.DodgerBlue, DashStyleHelper.Solid, 2);
            Draw.Line(this, "MES_ORB_V2_LOW_" + date, false, barsAgo, orLow, 0, orLow,
                Brushes.DodgerBlue, DashStyleHelper.Solid, 2);
            Draw.Rectangle(this, "MES_ORB_V2_ZONE_" + date, false, barsAgo, orHigh, 0, orLow,
                Brushes.DodgerBlue, Brushes.DodgerBlue, 8);
        }

        private void ProcessBiasAndPullbacks(DateTime easternBarTime)
        {
            int closeDirection = Close[0] > orHigh ? 1 : Close[0] < orLow ? -1 : 0;
            if (closeDirection == 0 && Position.MarketPosition == MarketPosition.Flat && !entryAttempted)
            {
                bias = 0;
                llmStatus = "inside OR; re-armed";
                return;
            }
            if (closeDirection == 0 || closeDirection == bias)
                return;

            bias = closeDirection;
            breakoutTime = easternBarTime;
            breakoutAbsoluteIndex = CurrentBar;
            breakoutClose = Close[0];
            string side = bias == 1 ? "LONG" : "SHORT";
            llmStatus = "breakout " + side.ToLowerInvariant() + "; sizing bracket";
            Notify("breakout-" + activeDate.ToString("yyyyMMdd") + "-" + CurrentBar,
                "MES ORB V2 " + side + " breakout close");
            QueueAuditEvent("setup_armed", bias, Close[0], null, null, null, null, null, "breakout_close");

            TradePlan selected = BuildBreakoutPlan(easternBarTime, Close[0]);
            if (selected == null || validatedCandidates.Contains(selected.CandidateId))
                return;

            if (validationsToday >= MaximumValidationsPerDay)
            {
                validatedCandidates.Add(selected.CandidateId);
                SkipCandidate(selected, "daily_validation_limit");
                return;
            }
            bool deterministicAnalyzer = State == State.Historical
                && IsInStrategyAnalyzer
                && HistoricalGateMode == OrbV2HistoricalGateMode.DeterministicOnly;
            if (!deterministicAnalyzer && State == State.Realtime && !HaveCompleteHigherTimeframes())
            {
                validatedCandidates.Add(selected.CandidateId);
                SubmitDeterministicFallback(selected, "missing_higher_timeframe_bars");
                return;
            }
            if (State == State.Historical)
            {
                validatedCandidates.Add(selected.CandidateId);
                if (deterministicAnalyzer)
                {
                    llmStatus = "analyzer deterministic-only";
                    SubmitEntry(selected);
                    return;
                }
                SkipCandidate(selected, "required_llm_unavailable_in_historical_backtest");
                return;
            }
            if (PositionAccount.MarketPosition != MarketPosition.Flat)
            {
                TriggerSafetyLock("account_position_not_flat", true);
                return;
            }

            validatedCandidates.Add(selected.CandidateId);
            if (!QueueLlmValidation(selected))
            {
                SubmitDeterministicFallback(selected, "llm_queue_unavailable");
                return;
            }
            validationsToday++;
        }

        private TradePlan BuildBreakoutPlan(DateTime candidateTime, double entryReference)
        {
            double stop = bias == 1 ? orHigh - TickSize : orLow + TickSize;
            stop = Instrument.MasterInstrument.RoundToTickSize(stop);
            if ((bias == 1 && stop >= entryReference - TickSize)
                || (bias == -1 && stop <= entryReference + TickSize))
                stop = Instrument.MasterInstrument.RoundToTickSize(
                    bias == 1 ? entryReference - 2 * TickSize : entryReference + 2 * TickSize);
            double distance = bias == 1 ? entryReference - orHigh : orLow - entryReference;
            double quality = Math.Max(0, Math.Min(1,
                distance / Math.Max(orHigh - orLow, TickSize)));
            return BuildPlan("breakout_close", candidateTime, entryReference,
                bias == 1 ? stop + TickSize : stop - TickSize, quality);
        }

        private TradePlan BuildPlan(string setupType, DateTime candidateTime, double entryReference,
            double structuralPrice, double quality)
        {
            double stop = bias == 1 ? structuralPrice - TickSize : structuralPrice + TickSize;
            stop = Instrument.MasterInstrument.RoundToTickSize(stop);
            double riskPoints = bias == 1 ? entryReference - stop : stop - entryReference;
            double riskDollars = riskPoints * Instrument.MasterInstrument.PointValue * TradeQuantity;
            if (riskPoints <= TickSize || riskDollars <= 0)
                return null;
            double target = bias == 1
                ? entryReference + RewardMultiple * riskPoints
                : entryReference - RewardMultiple * riskPoints;
            target = Instrument.MasterInstrument.RoundToTickSize(target);
            string id = setupType + "-" + TradingDate(ToEastern(candidateTime)).ToString("yyyyMMdd")
                + "-" + ToEastern(candidateTime).ToString("HHmmss") + "-" + bias;
            return new TradePlan
            {
                Direction = bias,
                SetupType = setupType,
                CandidateId = id,
                CandidateTime = ToEastern(candidateTime),
                TriggerPrice = entryReference,
                StructuralPrice = structuralPrice,
                EntryReference = entryReference,
                Stop = stop,
                Target = target,
                RiskDollars = riskDollars,
                Quality = quality,
                BarsSinceBreakout = Math.Max(1, CurrentBar - breakoutAbsoluteIndex)
            };
        }

        private bool QueueLlmValidation(TradePlan plan)
        {
            if (llmQueue == null || llmQueue.IsAddingCompleted)
                return false;
            string validationId = strategyInstanceId + "-v2-"
                + Interlocked.Increment(ref validationSequence).ToString(CultureInfo.InvariantCulture);
            string snapshotHash = Sha256(BuildSnapshotSeed(plan));
            string json = BuildValidationJson(validationId, plan, snapshotHash);
            LlmWork work = new LlmWork
            {
                ValidationId = validationId,
                SnapshotHash = snapshotHash,
                Json = json,
                Plan = plan,
                RequestedAtUtc = DateTime.UtcNow
            };
            if (!llmQueue.TryAdd(work))
                return false;
            validationPending = true;
            pendingValidationId = validationId;
            llmStatus = "validating " + plan.SetupType;
            contextStatus = "awaiting optional external context";
            return true;
        }

        private bool HaveCompleteHigherTimeframes()
        {
            return CurrentBars.Length >= 5 && CurrentBars[1] >= 11 && CurrentBars[2] >= 11
                && CurrentBars[3] >= 7 && CurrentBars[4] >= 10;
        }

        private string BuildSnapshotSeed(TradePlan plan)
        {
            return string.Join("|", new string[] {
                plan.CandidateId,
                activeDate.ToString("yyyyMMdd"),
                plan.Direction.ToString(CultureInfo.InvariantCulture),
                plan.EntryReference.ToString("R", CultureInfo.InvariantCulture),
                plan.Stop.ToString("R", CultureInfo.InvariantCulture),
                plan.Target.ToString("R", CultureInfo.InvariantCulture),
                orOpen.ToString("R", CultureInfo.InvariantCulture),
                orHigh.ToString("R", CultureInfo.InvariantCulture),
                orLow.ToString("R", CultureInfo.InvariantCulture),
                orClose.ToString("R", CultureInfo.InvariantCulture),
                breakoutClose.ToString("R", CultureInfo.InvariantCulture),
                SerializeBars(1, 12), SerializeBars(2, 12), SerializeBars(3, 8), SerializeBars(4, 10)
            });
        }

        private string BuildValidationJson(string validationId, TradePlan plan, string snapshotHash)
        {
            DateTime marketTime = ToEastern(Time[0]);
            string knownAt = ToUtcIso(Time[0]);
            double previousHigh = Highs[4][1];
            double previousLow = Lows[4][1];
            double previousClose = Closes[4][1];
            double dailyAtr = CalculateDailyAtr(14);
            double rthVwap = rthVolume > 0 ? rthTypicalVolume / rthVolume : Close[0];
            double gap = orOpen - previousClose;
            double elapsedMinutes = marketTime.TimeOfDay.TotalMinutes - (9 * 60 + 30);
            double elapsedFraction = Math.Max(1.0 / 78.0, Math.Min(1, elapsedMinutes / 390.0));
            double averageDailyVolume = AverageDailyVolume(10);
            double relativeVolume = averageDailyVolume > 0 ? rthVolume / (averageDailyVolume * elapsedFraction) : 0;
            double realizedRange = rthHigh > rthLow ? rthHigh - rthLow : 0;
            double orWidthAtr = dailyAtr > 0 ? (orHigh - orLow) / dailyAtr : 0;
            double breakoutDistance = plan.Direction == 1 ? breakoutClose - orHigh : orLow - breakoutClose;
            string executionMode = Account != null && Account.Name == "Playback101" ? "playback" : "simulation";
            string decisionClock = executionMode == "playback" ? knownAt : DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            if (overnightHigh == double.MinValue) overnightHigh = previousClose;
            if (overnightLow == double.MaxValue) overnightLow = previousClose;
            string hashValue = snapshotHash ?? new string('0', 64);

            StringBuilder json = new StringBuilder(12000);
            json.Append("{\"validation_id\":").Append(JsonString(validationId));
            json.Append(",\"strategy_instance_id\":").Append(JsonString(strategyInstanceId));
            json.Append(",\"strategy\":\"").Append(StrategyCode).Append("\"");
            json.Append(",\"instrument\":").Append(JsonString(Instrument.FullName));
            json.Append(",\"timestamp\":").Append(JsonString(decisionClock));
            json.Append(",\"playback_time\":").Append(JsonString(decisionClock));
            json.Append(",\"execution_mode\":").Append(JsonString(executionMode));
            json.Append(",\"opening_range\":{");
            json.Append("\"start\":").Append(JsonString(EasternClockToUtc(activeDate, 9, 30)));
            json.Append(",\"end\":").Append(JsonString(EasternClockToUtc(activeDate, 9, 45)));
            json.Append(",\"open\":").Append(JsonFinite(orOpen));
            json.Append(",\"high\":").Append(JsonFinite(orHigh));
            json.Append(",\"low\":").Append(JsonFinite(orLow));
            json.Append(",\"close\":").Append(JsonFinite(orClose)).Append("}");
            json.Append(",\"breakout\":{");
            json.Append("\"direction\":").Append(JsonString(plan.Direction == 1 ? "long" : "short"));
            json.Append(",\"bar_timestamp\":").Append(JsonString(EasternToUtcIso(breakoutTime)));
            json.Append(",\"close\":").Append(JsonFinite(breakoutClose));
            json.Append(",\"distance_points\":").Append(JsonFinite(Math.Max(0, breakoutDistance))).Append("}");
            json.Append(",\"pullback\":{");
            json.Append("\"setup_type\":").Append(JsonString(plan.SetupType));
            json.Append(",\"candidate_id\":").Append(JsonString(plan.CandidateId));
            json.Append(",\"bar_timestamp\":").Append(JsonString(EasternToUtcIso(plan.CandidateTime)));
            json.Append(",\"trigger_price\":").Append(JsonFinite(plan.TriggerPrice));
            json.Append(",\"structural_price\":").Append(JsonFinite(plan.StructuralPrice));
            json.Append(",\"quality_score\":").Append(JsonFinite(plan.Quality));
            json.Append(",\"bars_since_breakout\":").Append(plan.BarsSinceBreakout).Append("}");
            json.Append(",\"proposed_trade\":{");
            json.Append("\"direction\":").Append(JsonString(plan.Direction == 1 ? "long" : "short"));
            json.Append(",\"quantity\":1,\"entry_reference\":").Append(JsonFinite(plan.EntryReference));
            json.Append(",\"stop\":").Append(JsonFinite(plan.Stop));
            json.Append(",\"target\":").Append(JsonFinite(plan.Target));
            json.Append(",\"planned_risk\":").Append(JsonFinite(plan.RiskDollars));
            json.Append(",\"reward_multiple\":").Append(JsonFinite(RewardMultiple)).Append("}");
            json.Append(",\"bars_15m\":").Append(SerializeBars(1, 12));
            json.Append(",\"bars_60m\":").Append(SerializeBars(2, 12));
            json.Append(",\"bars_240m\":").Append(SerializeBars(3, 8));
            json.Append(",\"bars_daily\":").Append(SerializeBars(4, 10));
            json.Append(",\"market_regime\":{");
            AppendKnownFloat(json, "previous_day_high", previousHigh, knownAt, false);
            AppendKnownFloat(json, "previous_day_low", previousLow, knownAt, true);
            AppendKnownFloat(json, "previous_day_close", previousClose, knownAt, true);
            AppendKnownFloat(json, "overnight_high", overnightHigh, knownAt, true);
            AppendKnownFloat(json, "overnight_low", overnightLow, knownAt, true);
            AppendKnownFloat(json, "gap_points", gap, knownAt, true);
            AppendKnownFloat(json, "rth_vwap", rthVwap, knownAt, true);
            AppendKnownFloat(json, "atr_14_daily", dailyAtr, knownAt, true);
            AppendKnownFloat(json, "relative_volume", relativeVolume, knownAt, true);
            AppendKnownFloat(json, "realized_range", realizedRange, knownAt, true);
            AppendKnownFloat(json, "or_width_atr", orWidthAtr, knownAt, true);
            AppendKnownFloat(json, "breakout_distance", breakoutDistance, knownAt, true);
            AppendKnownFloat(json, "pullback_quality", plan.Quality, knownAt, true);
            AppendKnownFloat(json, "planned_risk", plan.RiskDollars, knownAt, true);
            json.Append("}");
            json.Append(",\"economic_events\":[],\"earnings\":[],\"headlines\":[]");
            json.Append(",\"context_timestamps\":{");
            json.Append("\"market_data_known_at\":").Append(JsonString(knownAt));
            json.Append(",\"economic_events_refreshed_at\":null");
            json.Append(",\"earnings_refreshed_at\":null");
            json.Append(",\"constituents_refreshed_at\":null");
            json.Append(",\"headlines_refreshed_at\":null}");
            json.Append(",\"snapshot_hash\":").Append(JsonString(hashValue)).Append("}");
            return json.ToString();
        }

        private static void AppendKnownFloat(StringBuilder json, string name, double value,
            string knownAt, bool prependComma)
        {
            if (prependComma)
                json.Append(',');
            json.Append(JsonString(name)).Append(":{\"value\":").Append(JsonFinite(value));
            json.Append(",\"known_at\":").Append(JsonString(knownAt)).Append('}');
        }

        private string SerializeBars(int seriesIndex, int count)
        {
            StringBuilder result = new StringBuilder("[");
            for (int barsAgo = count - 1; barsAgo >= 0; barsAgo--)
            {
                if (barsAgo != count - 1)
                    result.Append(',');
                result.Append("{\"timestamp\":").Append(JsonString(ToUtcIso(Times[seriesIndex][barsAgo])));
                result.Append(",\"open\":").Append(JsonFinite(Opens[seriesIndex][barsAgo]));
                result.Append(",\"high\":").Append(JsonFinite(Highs[seriesIndex][barsAgo]));
                result.Append(",\"low\":").Append(JsonFinite(Lows[seriesIndex][barsAgo]));
                result.Append(",\"close\":").Append(JsonFinite(Closes[seriesIndex][barsAgo]));
                result.Append(",\"volume\":").Append(JsonFinite(Volumes[seriesIndex][barsAgo])).Append('}');
            }
            return result.Append(']').ToString();
        }

        private void HandleLlmResult(object state)
        {
            LlmResult result = state as LlmResult;
            if (result == null || State != State.Realtime || !validationPending
                || !string.Equals(result.ValidationId, pendingValidationId, StringComparison.Ordinal))
                return;
            validationPending = false;
            pendingValidationId = null;
            double age = (DateTime.UtcNow - result.RequestedAtUtc).TotalSeconds;
            if (age < 0 || age > MaximumDecisionAgeSeconds)
            {
                SubmitDeterministicFallback(result.Plan, "llm_response_stale");
                return;
            }
            if (result.Decision != "allow")
            {
                if (IsLlmInfrastructureFailure(result))
                    SubmitDeterministicFallback(result.Plan, result.Reason);
                else
                    RejectLlm(result.Plan, result.Reason);
                return;
            }
            if (result.Confidence < MinimumAllowConfidence)
            {
                SubmitDeterministicFallback(result.Plan, "llm_confidence_below_threshold");
                return;
            }
            if (!ApplyLlmBracket(result.Plan, result.StopLoss, result.TakeProfit))
            {
                SubmitDeterministicFallback(result.Plan, "llm_bracket_invalid");
                return;
            }
            if (entryAttempted || safetyLocked || bias != result.Plan.Direction
                || Position.MarketPosition != MarketPosition.Flat || PositionAccount.MarketPosition != MarketPosition.Flat
                || ToTime(ToEastern(Time[0])) >= FlattenTime)
            {
                RejectLlm(result.Plan, "setup_changed_while_waiting");
                return;
            }

            double currentPrice = Close[0];
            try
            {
                double quote = result.Plan.Direction == 1 ? GetCurrentAsk() : GetCurrentBid();
                if (quote > 0)
                    currentPrice = quote;
            }
            catch { }
            if (Math.Abs(currentPrice - result.Plan.EntryReference) > 4 * TickSize)
            {
                RejectLlm(result.Plan, "entry_price_drift_exceeded");
                return;
            }
            bool beyondBoundary = result.Plan.Direction == 1 ? currentPrice > orHigh : currentPrice < orLow;
            bool throughTrigger = result.Plan.Direction == 1
                ? currentPrice < result.Plan.TriggerPrice - 4 * TickSize
                : currentPrice > result.Plan.TriggerPrice + 4 * TickSize;
            if (!beyondBoundary || throughTrigger)
            {
                RejectLlm(result.Plan, "return_through_trigger");
                return;
            }

            TradePlan refreshed = RefreshPlanAtPrice(result.Plan, currentPrice);
            if (refreshed == null)
            {
                RejectLlm(result.Plan, "post_llm_risk_invalid");
                return;
            }
            llmStatus = "allowed " + result.Plan.SetupType;
            contextStatus = "fresh snapshot " + result.SnapshotHash.Substring(0, 8);
            SubmitEntry(refreshed);
        }

        private bool ApplyLlmBracket(TradePlan plan, double stop, double target)
        {
            if (stop <= 0 || target <= 0)
                return false;
            stop = Instrument.MasterInstrument.RoundToTickSize(stop);
            target = Instrument.MasterInstrument.RoundToTickSize(target);
            double riskPoints = plan.Direction == 1
                ? plan.EntryReference - stop : stop - plan.EntryReference;
            double rewardPoints = plan.Direction == 1
                ? target - plan.EntryReference : plan.EntryReference - target;
            double riskDollars = riskPoints * Instrument.MasterInstrument.PointValue * TradeQuantity;
            if (riskPoints <= TickSize || riskDollars <= 0
                || Math.Abs(rewardPoints - RewardMultiple * riskPoints) > TickSize / 2)
                return false;
            plan.Stop = stop;
            plan.Target = target;
            plan.RiskDollars = riskDollars;
            return true;
        }

        private static bool IsLlmInfrastructureFailure(LlmResult result)
        {
            string reason = (result.Reason ?? string.Empty).ToLowerInvariant();
            string provider = (result.Provider ?? string.Empty).ToLowerInvariant();
            return provider == "disabled" || provider == "bridge" || provider == "context"
                || reason.Contains("timeout") || reason.Contains("unavailable")
                || reason.Contains("provider_error") || reason.Contains("exception")
                || reason.Contains("invalid") || reason.Contains("missing")
                || reason.Contains("stale") || reason.Contains("incomplete");
        }

        private void SubmitDeterministicFallback(TradePlan plan, string reason)
        {
            if (entryAttempted || safetyLocked || bias != plan.Direction
                || Position.MarketPosition != MarketPosition.Flat
                || PositionAccount.MarketPosition != MarketPosition.Flat
                || ToTime(ToEastern(Time[0])) >= FlattenTime)
            {
                RejectLlm(plan, "setup_changed_while_waiting");
                return;
            }
            double currentPrice = Close[0];
            if (Math.Abs(currentPrice - plan.EntryReference) > 4 * TickSize)
            {
                RejectLlm(plan, "entry_price_drift_exceeded");
                return;
            }
            TradePlan fallback = RefreshPlanAtPrice(plan, currentPrice);
            if (fallback == null)
            {
                RejectLlm(plan, "deterministic_fallback_risk_invalid");
                return;
            }
            llmStatus = "deterministic fallback: " + reason;
            QueueAuditEvent("setup_skipped", plan.Direction, currentPrice, currentPrice,
                fallback.Stop, fallback.Target, fallback.RiskDollars, null, "llm_fallback_" + reason);
            SubmitEntry(fallback);
        }

        private TradePlan RefreshPlanAtPrice(TradePlan source, double entryReference)
        {
            double riskPoints = source.Direction == 1 ? entryReference - source.Stop : source.Stop - entryReference;
            double riskDollars = riskPoints * Instrument.MasterInstrument.PointValue * TradeQuantity;
            if (riskPoints <= TickSize || riskDollars <= 0)
                return null;
            source.EntryReference = entryReference;
            source.RiskDollars = riskDollars;
            source.Target = Instrument.MasterInstrument.RoundToTickSize(source.Direction == 1
                ? entryReference + RewardMultiple * riskPoints
                : entryReference - RewardMultiple * riskPoints);
            return source;
        }

        private void RejectLlm(TradePlan plan, string reason)
        {
            llmStatus = "rejected: " + reason;
            contextStatus = reason.IndexOf("context", StringComparison.OrdinalIgnoreCase) >= 0
                ? "unavailable/stale" : contextStatus;
            SkipCandidate(plan, reason);
            Notify("v2-reject-" + validationSequence, "MES ORB V2 candidate rejected: " + reason);
        }

        private void SubmitEntry(TradePlan plan)
        {
            plannedStopPrice = plan.Stop;
            plannedTargetPrice = plan.Target;
            plannedRiskDollars = plan.RiskDollars;
            activeTradeDirection = plan.Direction;
            entryAttempted = true;
            string signal = plan.Direction == 1 ? LongEntrySignal : ShortEntrySignal;
            SetStopLoss(signal, CalculationMode.Price, plannedStopPrice, false);
            SetProfitTarget(signal, CalculationMode.Price, plannedTargetPrice);
            QueueAuditEvent("entry_submitted", plan.Direction, plan.EntryReference, plan.EntryReference,
                plan.Stop, plan.Target, plan.RiskDollars, null, plan.SetupType);
            if (plan.Direction == 1)
                EnterLong(TradeQuantity, signal);
            else
                EnterShort(TradeQuantity, signal);
        }

        private void SkipCandidate(TradePlan plan, string reason)
        {
            QueueAuditEvent("setup_skipped", plan.Direction, CloseSafe(), plan.EntryReference,
                plan.Stop, plan.Target, plan.RiskDollars, null, reason);
            Print(Name + " candidate skipped: " + reason);
        }

        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice,
            int quantity, int filled, double averageFillPrice, OrderState orderState,
            DateTime time, ErrorCode error, string comment)
        {
            if (order == null)
                return;
            if (order.Name == LongEntrySignal || order.Name == ShortEntrySignal)
                entryOrder = order;
            if (orderState != OrderState.Rejected)
                return;

            int direction = order.Name == ShortEntrySignal ? -1 : activeTradeDirection;
            QueueAuditEvent("order_rejected", direction, averageFillPrice > 0 ? averageFillPrice : CloseSafe(),
                entryFillPrice, plannedStopPrice, plannedTargetPrice, plannedRiskDollars, null,
                error + ":" + comment);
            Notify("v2-rejected-" + eventSequence, "MES ORB V2 order rejected; manual re-enable required");
            TriggerSafetyLock(order.Name == LongEntrySignal || order.Name == ShortEntrySignal
                ? "entry_order_rejected" : "protective_order_rejected", true);
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId,
            double price, int quantity, MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (execution == null || execution.Order == null || execution.Order.OrderState != OrderState.Filled)
                return;
            string orderName = execution.Order.Name;
            if (orderName == LongEntrySignal || orderName == ShortEntrySignal)
            {
                activeTradeDirection = orderName == LongEntrySignal ? 1 : -1;
                entryFillPrice = execution.Order.AverageFillPrice;
                entryAbsoluteIndex = CurrentBar;
                if (activeTradeDirection == 1)
                    Draw.ArrowUp(this, "MES_ORB_V2_ENTRY_" + executionId, false, 0,
                        entryFillPrice - 2 * TickSize, Brushes.LimeGreen);
                else
                    Draw.ArrowDown(this, "MES_ORB_V2_ENTRY_" + executionId, false, 0,
                        entryFillPrice + 2 * TickSize, Brushes.OrangeRed);
                double actualRiskPoints = activeTradeDirection == 1
                    ? entryFillPrice - plannedStopPrice : plannedStopPrice - entryFillPrice;
                if (actualRiskPoints <= 0)
                {
                    TriggerSafetyLock("fill_invalidates_stop_geometry", true);
                    return;
                }
                plannedTargetPrice = Instrument.MasterInstrument.RoundToTickSize(activeTradeDirection == 1
                    ? entryFillPrice + RewardMultiple * actualRiskPoints
                    : entryFillPrice - RewardMultiple * actualRiskPoints);
                SetProfitTarget(orderName, CalculationMode.Price, plannedTargetPrice);
                QueueAuditEvent("entry_filled", activeTradeDirection, entryFillPrice, entryFillPrice,
                    plannedStopPrice, plannedTargetPrice, plannedRiskDollars, null, null);
                QueueAuditEvent("protection_active", activeTradeDirection, entryFillPrice, entryFillPrice,
                    plannedStopPrice, plannedTargetPrice, plannedRiskDollars, null, null);
                Notify("v2-entry-" + activeDate.ToString("yyyyMMdd"),
                    "MES ORB V2 entry filled; native stop and 2R target active");
                return;
            }

            if (entryFillPrice > 0 && activeTradeDirection != 0 && marketPosition == MarketPosition.Flat)
            {
                double realized = activeTradeDirection == 1
                    ? (price - entryFillPrice) * Instrument.MasterInstrument.PointValue * quantity
                    : (entryFillPrice - price) * Instrument.MasterInstrument.PointValue * quantity;
                dailyRealizedPnl += realized;
                Brush tradeBrush = realized >= 0 ? Brushes.LimeGreen : Brushes.OrangeRed;
                Draw.Diamond(this, "MES_ORB_V2_EXIT_" + executionId, false, 0, price, tradeBrush);
                if (entryAbsoluteIndex >= 0)
                    Draw.Line(this, "MES_ORB_V2_TRADE_" + executionId, false,
                        Math.Max(0, CurrentBar - entryAbsoluteIndex), entryFillPrice, 0, price,
                        tradeBrush, DashStyleHelper.Dash, 2);
                QueueAuditEvent("exit_filled", activeTradeDirection, price, entryFillPrice,
                    plannedStopPrice, plannedTargetPrice, plannedRiskDollars, realized, orderName);
                Notify("v2-exit-" + eventSequence,
                    "MES ORB V2 exit filled; gross PnL " + realized.ToString("C2", CultureInfo.CurrentCulture));
                entryFillPrice = 0;
                entryAbsoluteIndex = -1;
                activeTradeDirection = 0;
                entryAttempted = false;
                llmStatus = "flat; waiting for OR re-entry";
                if (dailyRealizedPnl <= -DailyLossLimit)
                    TriggerSafetyLock("daily_loss_limit", false);
            }
        }

        protected override void OnConnectionStatusUpdate(ConnectionStatusEventArgs update)
        {
            if (State == State.Realtime && update != null
                && update.PriceStatus == ConnectionStatus.Disconnected)
            {
                QueueAuditEvent("connection_lost", activeTradeDirection, CloseSafe(), entryFillPrice,
                    plannedStopPrice, plannedTargetPrice, plannedRiskDollars, null, "price_feed_disconnected");
                TriggerSafetyLock("connection_lost", true);
            }
        }

        private bool ValidateStaticConfiguration()
        {
            if (Instrument == null || Instrument.MasterInstrument == null
                || !string.Equals(Instrument.MasterInstrument.Name, "MES", StringComparison.OrdinalIgnoreCase))
            {
                TriggerSafetyLock("instrument_not_mes", State == State.Realtime);
                return false;
            }
            if (BarsPeriod.BarsPeriodType != BarsPeriodType.Minute || BarsPeriod.Value != 5)
            {
                TriggerSafetyLock("primary_series_must_be_five_minutes", State == State.Realtime);
                return false;
            }
            if (OpeningRangeStart != 93000 || OpeningRangeEnd != 94500 || FlattenTime != 155500)
            {
                TriggerSafetyLock("v2_schedule_is_hard_locked", State == State.Realtime);
                return false;
            }
            if (!IsLoopbackEndpoint(ValidationEndpoint) || !IsLoopbackEndpoint(AuditEndpoint)
                || !IsLoopbackEndpoint(ContextStatusEndpoint))
            {
                TriggerSafetyLock("bridge_endpoints_must_be_loopback", State == State.Realtime);
                return false;
            }
            return true;
        }

        private bool ValidateRealtimeEnvironment()
        {
            if (runtimeValidated)
                return !safetyLocked;
            runtimeValidated = true;
            if (Account == null || !IsAllowedSimulationAccount(Account.Name))
            {
                TriggerSafetyLock("account_not_allowed", true);
                return false;
            }
            if (TradeQuantity != 1)
            {
                TriggerSafetyLock("v2_risk_configuration_invalid", true);
                return false;
            }
            return true;
        }

        private bool IsAllowedSimulationAccount(string accountName)
        {
            if (accountName != "Sim101" && accountName != "Playback101")
                return false;
            foreach (string configured in (AllowedAccountNames ?? string.Empty).Split(','))
                if (string.Equals(configured.Trim(), accountName, StringComparison.Ordinal))
                    return true;
            return false;
        }

        private static bool IsLoopbackEndpoint(string endpoint)
        {
            Uri uri;
            return Uri.TryCreate(endpoint, UriKind.Absolute, out uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                && uri.IsLoopback;
        }

        private void FlattenForSessionEnd()
        {
            if (flattenIssued)
                return;
            flattenIssued = true;
            validationPending = false;
            pendingValidationId = null;
            if (entryOrder != null && (entryOrder.OrderState == OrderState.Accepted
                || entryOrder.OrderState == OrderState.Submitted || entryOrder.OrderState == OrderState.Working))
                CancelOrder(entryOrder);
            if (Position.MarketPosition == MarketPosition.Long)
                ExitLong("MES_ORB_V2_TIME_EXIT", LongEntrySignal);
            else if (Position.MarketPosition == MarketPosition.Short)
                ExitShort("MES_ORB_V2_TIME_EXIT", ShortEntrySignal);
        }

        private void TriggerSafetyLock(string reason, bool closeStrategy)
        {
            if (!safetyLocked)
            {
                safetyLocked = true;
                validationPending = false;
                pendingValidationId = null;
                llmStatus = "SAFETY LOCK: " + reason;
                QueueAuditEvent("risk_lockout", activeTradeDirection, CloseSafe(), entryFillPrice,
                    plannedStopPrice, plannedTargetPrice, plannedRiskDollars, dailyRealizedPnl, reason);
                Notify("v2-lock-" + eventSequence, "MES ORB V2 safety lock: " + reason);
            }
            if (closeStrategy && State == State.Realtime)
                CloseStrategy(reason);
        }

        private void UpdateSessionContext(DateTime easternBarTime, int now)
        {
            bool overnight = now < 93000 || now >= 180000;
            if (overnight)
            {
                overnightHigh = overnightHigh == double.MinValue ? High[0] : Math.Max(overnightHigh, High[0]);
                overnightLow = overnightLow == double.MaxValue ? Low[0] : Math.Min(overnightLow, Low[0]);
            }
            if (now > 93000 && now <= 160000)
            {
                rthHigh = rthHigh == double.MinValue ? High[0] : Math.Max(rthHigh, High[0]);
                rthLow = rthLow == double.MaxValue ? Low[0] : Math.Min(rthLow, Low[0]);
                rthVolume += Volume[0];
                rthTypicalVolume += ((High[0] + Low[0] + Close[0]) / 3.0) * Volume[0];
            }
        }

        private double CalculateDailyAtr(int period)
        {
            int available = Math.Min(period, CurrentBars[4]);
            if (available <= 0)
                return 0;
            double total = 0;
            for (int i = 0; i < available; i++)
            {
                double previousClose = Closes[4][i + 1];
                total += Math.Max(Highs[4][i] - Lows[4][i],
                    Math.Max(Math.Abs(Highs[4][i] - previousClose), Math.Abs(Lows[4][i] - previousClose)));
            }
            return total / available;
        }

        private double AverageDailyVolume(int period)
        {
            int available = Math.Min(period, CurrentBars[4]);
            if (available <= 0)
                return 0;
            double total = 0;
            for (int i = 1; i <= available; i++)
                total += Volumes[4][i];
            return total / available;
        }

        private void EnsureTradingDay(DateTime date)
        {
            if (activeDate != date)
                ResetTradingDay(date);
        }

        private void ResetTradingDay(DateTime date)
        {
            activeDate = date;
            validatedCandidates.Clear();
            runtimeValidated = false;
            safetyLocked = false;
            entryAttempted = false;
            flattenIssued = false;
            validationPending = false;
            pendingValidationId = null;
            validationsToday = 0;
            orStarted = false;
            orComplete = false;
            orOpen = orClose = 0;
            orHigh = double.MinValue;
            orLow = double.MaxValue;
            orBarCount = 0;
            orStartAbsoluteIndex = -1;
            bias = 0;
            breakoutTime = DateTime.MinValue;
            breakoutAbsoluteIndex = -1;
            breakoutClose = 0;
            overnightHigh = double.MinValue;
            overnightLow = double.MaxValue;
            rthHigh = double.MinValue;
            rthLow = double.MaxValue;
            rthVolume = rthTypicalVolume = 0;
            plannedStopPrice = plannedTargetPrice = plannedRiskDollars = 0;
            entryFillPrice = dailyRealizedPnl = 0;
            activeTradeDirection = 0;
            entryAbsoluteIndex = -1;
            entryOrder = null;
            llmStatus = "required-ready";
            contextStatus = "prefetched bridge context required";
            nextContextEvent = "unknown";
            nextContextEarnings = "unknown";
            contextPollPending = false;
            lastContextPollUtc = DateTime.MinValue;
        }

        private void RenderStatus()
        {
            string range = orComplete ? orLow.ToString("0.00") + " - " + orHigh.ToString("0.00") : "building";
            string side = bias == 1 ? "long" : bias == -1 ? "short" : "none";
            string reentry = entryAttempted ? "trade active/attempted"
                : bias == 0 ? "armed inside OR" : "waiting for OR return";
            string gate = State == State.Historical ? HistoricalGateMode.ToString() : "RequiredLLM";
            string status = string.Format(CultureInfo.InvariantCulture,
                "MES ORB Breakout V2 (SIM ONLY)\nOR 9:30-9:45 ET: {0}\nBias: {1}\nRe-entry: {2}\nGate: {3}\nValidations: {4}/3\nLLM: {5}\nContext: {6}\nNext event: {7}\nNext weighted earnings: {8}\nPlanned risk: ${9:0.00}\nEntry attempted: {10}\nSafety lock: {11}\nPosition: {12}",
                range, side, reentry, gate, validationsToday, llmStatus, contextStatus,
                nextContextEvent, nextContextEarnings, plannedRiskDollars,
                entryAttempted, safetyLocked, Position.MarketPosition);
            Draw.TextFixed(this, "MES_ORB_V2_STATUS", status, TextPosition.TopLeft);
        }

        private void QueueContextStatusPoll()
        {
            if (State != State.Realtime || contextPollPending
                || (DateTime.UtcNow - lastContextPollUtc).TotalSeconds < 60)
                return;
            contextPollPending = true;
            lastContextPollUtc = DateTime.UtcNow;
            Task.Factory.StartNew(delegate
            {
                BridgeContextResult result = new BridgeContextResult { Status = "unavailable" };
                try
                {
                    HttpWebRequest request = (HttpWebRequest)WebRequest.Create(ContextStatusEndpoint);
                    request.Method = "GET";
                    request.Timeout = 750;
                    request.ReadWriteTimeout = 750;
                    using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                    using (StreamReader reader = new StreamReader(response.GetResponseStream()))
                    {
                        string json = reader.ReadToEnd();
                        result.Status = ExtractJsonString(json, "status") ?? "unavailable";
                        result.NextEvent = ExtractJsonString(json, "next_economic_event");
                        result.NextEarnings = ExtractJsonString(json, "next_earnings");
                    }
                }
                catch { }
                try { TriggerCustomEvent(HandleContextStatus, 0, result); }
                catch { }
            });
        }

        private void HandleContextStatus(object state)
        {
            BridgeContextResult result = state as BridgeContextResult;
            contextPollPending = false;
            if (result == null)
                return;
            contextStatus = result.Status;
            nextContextEvent = string.IsNullOrWhiteSpace(result.NextEvent) ? "none cached" : result.NextEvent;
            nextContextEarnings = string.IsNullOrWhiteSpace(result.NextEarnings) ? "none cached" : result.NextEarnings;
        }

        private void StartLlmWorker()
        {
            llmQueue = new BlockingCollection<LlmWork>(LlmQueueCapacity);
            llmCancellation = new CancellationTokenSource();
            llmWorker = Task.Factory.StartNew(ProcessLlmQueue, llmCancellation.Token,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        private void StopLlmWorker()
        {
            try
            {
                if (llmQueue != null && !llmQueue.IsAddingCompleted) llmQueue.CompleteAdding();
                if (llmCancellation != null) llmCancellation.CancelAfter(750);
                if (llmWorker != null) llmWorker.Wait(750);
            }
            catch { }
            finally
            {
                if (llmCancellation != null) llmCancellation.Dispose();
                if (llmQueue != null) llmQueue.Dispose();
            }
        }

        private void ProcessLlmQueue()
        {
            try
            {
                foreach (LlmWork work in llmQueue.GetConsumingEnumerable(llmCancellation.Token))
                {
                    LlmResult result = PostValidation(work);
                    try { TriggerCustomEvent(HandleLlmResult, 0, result); }
                    catch (Exception ex) { Print(Name + " LLM dispatch unavailable: " + ex.Message); }
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        }

        private LlmResult PostValidation(LlmWork work)
        {
            LlmResult result = new LlmResult
            {
                ValidationId = work.ValidationId,
                SnapshotHash = work.SnapshotHash,
                Plan = work.Plan,
                RequestedAtUtc = work.RequestedAtUtc,
                Decision = "reject",
                Confidence = 0,
                Reason = "llm_bridge_unavailable",
                Provider = "bridge",
                Model = "none"
            };
            try
            {
                byte[] body = Encoding.UTF8.GetBytes(work.Json);
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(ValidationEndpoint);
                request.Method = "POST";
                request.ContentType = "application/json";
                request.ContentLength = body.Length;
                request.Timeout = LlmTimeoutMilliseconds;
                request.ReadWriteTimeout = LlmTimeoutMilliseconds;
                using (Stream stream = request.GetRequestStream()) stream.Write(body, 0, body.Length);
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (StreamReader reader = new StreamReader(response.GetResponseStream()))
                {
                    string json = reader.ReadToEnd();
                    if (ExtractJsonString(json, "validation_id") != work.ValidationId
                        || ExtractJsonString(json, "snapshot_hash") != work.SnapshotHash)
                    {
                        result.Reason = "llm_response_identity_mismatch";
                        return result;
                    }
                    result.Decision = ExtractJsonString(json, "decision") == "allow" ? "allow" : "reject";
                    result.Confidence = ExtractJsonNumber(json, "confidence");
                    result.Reason = ExtractJsonStringArray(json, "reason_codes") ?? "unspecified_rejection";
                    result.Provider = ExtractJsonString(json, "provider") ?? "bridge";
                    result.Model = ExtractJsonString(json, "model") ?? "unknown";
                    result.StopLoss = ExtractJsonNumber(json, "stop_loss");
                    result.TakeProfit = ExtractJsonNumber(json, "take_profit");
                }
            }
            catch (WebException ex)
            {
                result.Reason = ex.Status == WebExceptionStatus.Timeout ? "llm_timeout" : "llm_bridge_unavailable";
            }
            catch (Exception ex)
            {
                result.Reason = "llm_response_invalid_" + ex.GetType().Name.ToLowerInvariant();
            }
            return result;
        }

        private static string ExtractJsonString(string json, string field)
        {
            Match match = Regex.Match(json ?? string.Empty,
                "\\\"" + Regex.Escape(field) + "\\\"\\s*:\\s*\\\"((?:\\\\.|[^\\\"])*)\\\"", RegexOptions.Singleline);
            return match.Success ? match.Groups[1].Value.Replace("\\\"", "\"").Replace("\\\\", "\\") : null;
        }

        private static double ExtractJsonNumber(string json, string field)
        {
            Match match = Regex.Match(json ?? string.Empty,
                "\\\"" + Regex.Escape(field) + "\\\"\\s*:\\s*(-?[0-9]+(?:\\.[0-9]+)?)", RegexOptions.Singleline);
            double value;
            return match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float,
                CultureInfo.InvariantCulture, out value) ? value : 0;
        }

        private static string ExtractJsonStringArray(string json, string field)
        {
            Match array = Regex.Match(json ?? string.Empty,
                "\\\"" + Regex.Escape(field) + "\\\"\\s*:\\s*\\[(.*?)\\]", RegexOptions.Singleline);
            if (!array.Success) return null;
            List<string> values = new List<string>();
            foreach (Match item in Regex.Matches(array.Groups[1].Value,
                "\\\"((?:\\\\.|[^\\\"])*)\\\"", RegexOptions.Singleline))
                values.Add(item.Groups[1].Value.Replace("\\\"", "\"").Replace("\\\\", "\\"));
            return string.Join(",", values.ToArray());
        }

        private void Notify(string tag, string message)
        {
            Print(Name + ": " + message);
            if (State == State.Realtime)
                Alert(tag, Priority.High, message, "Alert3.wav", 10, Brushes.Transparent, Brushes.White);
        }

        private void StartAuditWorker()
        {
            auditQueue = new BlockingCollection<string>(AuditQueueCapacity);
            auditCancellation = new CancellationTokenSource();
            auditWorker = Task.Factory.StartNew(ProcessAuditQueue, auditCancellation.Token,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        private void StopAuditWorker()
        {
            try
            {
                if (auditQueue != null && !auditQueue.IsAddingCompleted) auditQueue.CompleteAdding();
                if (auditCancellation != null) auditCancellation.CancelAfter(750);
                if (auditWorker != null) auditWorker.Wait(750);
            }
            catch { }
            finally
            {
                if (auditCancellation != null) auditCancellation.Dispose();
                if (auditQueue != null) auditQueue.Dispose();
            }
        }

        private void ProcessAuditQueue()
        {
            try
            {
                foreach (string json in auditQueue.GetConsumingEnumerable(auditCancellation.Token))
                    PostAuditEvent(json);
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        }

        private void QueueAuditEvent(string eventType, int direction, double price,
            double? entry, double? stop, double? target, double? risk, double? realized, string reason)
        {
            if (State != State.Realtime || auditQueue == null || auditQueue.IsAddingCompleted)
                return;
            string side = direction == 1 ? "long" : direction == -1 ? "short" : null;
            string executionMode = Account != null && Account.Name == "Playback101" ? "playback" : "simulation";
            string eventId = strategyInstanceId + "-" + Interlocked.Increment(ref eventSequence).ToString(CultureInfo.InvariantCulture);
            string json = string.Format(CultureInfo.InvariantCulture,
                "{{\"event_id\":\"{0}\",\"strategy_instance_id\":\"{1}\",\"event_type\":\"{2}\",\"strategy\":\"{3}\",\"instrument\":\"{4}\",\"timestamp\":\"{5}\",\"execution_mode\":\"{6}\",\"direction\":{7},\"quantity\":1,\"price\":{8},\"entry\":{9},\"stop\":{10},\"target\":{11},\"planned_risk\":{12},\"realized_pnl\":{13},\"reason_code\":{14}}}",
                EscapeJson(eventId), EscapeJson(strategyInstanceId), EscapeJson(eventType), StrategyCode,
                EscapeJson(Instrument.FullName), DateTime.UtcNow.ToString("o"), executionMode,
                JsonString(side), JsonNumber(price), JsonNumber(entry), JsonNumber(stop), JsonNumber(target),
                JsonNumber(risk), JsonNumber(realized), JsonString(reason));
            if (!auditQueue.TryAdd(json)) Print(Name + " audit queue full; event dropped: " + eventType);
        }

        private void PostAuditEvent(string json)
        {
            try
            {
                byte[] body = Encoding.UTF8.GetBytes(json);
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(AuditEndpoint);
                request.Method = "POST";
                request.ContentType = "application/json";
                request.ContentLength = body.Length;
                request.Timeout = 500;
                request.ReadWriteTimeout = 500;
                using (Stream stream = request.GetRequestStream()) stream.Write(body, 0, body.Length);
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse()) { }
            }
            catch (Exception ex) { Print(Name + " audit unavailable: " + ex.Message); }
        }

        private DateTime ToEastern(DateTime value)
        {
            DateTime unspecified = DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
            try
            {
                return TimeZoneInfo.ConvertTime(unspecified,
                    NinjaTrader.Core.Globals.GeneralOptions.TimeZoneInfo, easternTimeZone);
            }
            catch { return unspecified; }
        }

        private string ToUtcIso(DateTime value)
        {
            DateTime eastern = ToEastern(value);
            return EasternToUtcIso(eastern);
        }

        private string EasternToUtcIso(DateTime eastern)
        {
            DateTime unspecified = DateTime.SpecifyKind(eastern, DateTimeKind.Unspecified);
            return TimeZoneInfo.ConvertTimeToUtc(unspecified, easternTimeZone).ToString("o", CultureInfo.InvariantCulture);
        }

        private string EasternClockToUtc(DateTime day, int hour, int minute)
        {
            return EasternToUtcIso(new DateTime(day.Year, day.Month, day.Day, hour, minute, 0));
        }

        private static DateTime TradingDate(DateTime eastern)
        {
            return eastern.TimeOfDay >= TimeSpan.FromHours(18) ? eastern.Date.AddDays(1) : eastern.Date;
        }

        private static string Sha256(string value)
        {
            using (SHA256 hash = SHA256.Create())
            {
                byte[] bytes = hash.ComputeHash(Encoding.UTF8.GetBytes(value));
                StringBuilder result = new StringBuilder(64);
                foreach (byte item in bytes) result.Append(item.ToString("x2", CultureInfo.InvariantCulture));
                return result.ToString();
            }
        }

        private double CloseSafe()
        {
            try { return Close[0]; }
            catch { return 0; }
        }

        private static string JsonFinite(double value)
        {
            return double.IsNaN(value) || double.IsInfinity(value)
                ? "null" : value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static string JsonNumber(double value)
        {
            return value <= 0 || double.IsNaN(value) || double.IsInfinity(value)
                ? "null" : value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static string JsonNumber(double? value)
        {
            return !value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value)
                ? "null" : value.Value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static string JsonString(string value)
        {
            return value == null ? "null" : "\"" + EscapeJson(value) + "\"";
        }

        private static string EscapeJson(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"")
                .Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        }
    }
}
