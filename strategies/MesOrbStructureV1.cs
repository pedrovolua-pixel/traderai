#region Using declarations
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public enum LlmValidationMode
    {
        Off,
        Shadow,
        Required
    }

    /// <summary>
    /// Simulation-only MES opening-range breakout strategy with confirmed
    /// five-minute market-structure filtering and native protective orders.
    /// </summary>
    public class MesOrbStructureV1 : Strategy
    {
        private const string LongEntrySignal = "MES_ORB_LONG";
        private const string ShortEntrySignal = "MES_ORB_SHORT";
        private const string StrategyCode = "mes_orb_structure_v1";
        private const int AuditQueueCapacity = 128;
        private const int LlmQueueCapacity = 4;
        private const int LlmSnapshotBars = 12;

        private sealed class TradePlan
        {
            public double EntryReference;
            public double Stop;
            public double Target;
            public double RiskDollars;
        }

        private sealed class LlmValidationWork
        {
            public string ValidationId;
            public string Json;
            public int Direction;
            public LlmValidationMode Mode;
            public DateTime RequestedAtUtc;
            public int TimeoutMilliseconds;
        }

        private sealed class LlmValidationResult
        {
            public string ValidationId;
            public int Direction;
            public LlmValidationMode Mode;
            public DateTime RequestedAtUtc;
            public string Decision;
            public double Confidence;
            public string Reason;
            public string Provider;
            public string Model;
        }

        private readonly List<double> confirmedSwingHighs = new List<double>();
        private readonly List<double> confirmedSwingLows = new List<double>();

        private BlockingCollection<string> auditQueue;
        private CancellationTokenSource auditCancellation;
        private Task auditWorker;
        private BlockingCollection<LlmValidationWork> llmQueue;
        private CancellationTokenSource llmCancellation;
        private Task llmWorker;

        private DateTime activeDate = DateTime.MinValue;
        private string strategyInstanceId;
        private long eventSequence;
        private double openingRangeHigh;
        private double openingRangeLow;
        private bool openingRangeStarted;
        private bool openingRangeComplete;
        private int structureDirection;
        private int armedDirection;
        private bool entryAttempted;
        private bool riskLocked;
        private bool runtimeValidated;
        private bool flattenIssued;
        private double plannedStopPrice;
        private double plannedTargetPrice;
        private double plannedRiskDollars;
        private double entryFillPrice;
        private int activeTradeDirection;
        private double dailyRealizedPnl;
        private Order entryOrder;
        private bool llmValidationPending;
        private string pendingValidationId;
        private string llmLastStatus;
        private long validationSequence;

        [NinjaScriptProperty]
        [Range(93000, 155500)]
        [Display(Name = "Opening range start (HHmmss)", Order = 1, GroupName = "Schedule")]
        public int OpeningRangeStart { get; set; }

        [NinjaScriptProperty]
        [Range(93000, 155500)]
        [Display(Name = "Opening range end (HHmmss)", Order = 2, GroupName = "Schedule")]
        public int OpeningRangeEnd { get; set; }

        [NinjaScriptProperty]
        [Range(93000, 155959)]
        [Display(Name = "Flatten time (HHmmss)", Order = 3, GroupName = "Schedule")]
        public int FlattenTime { get; set; }

        [NinjaScriptProperty]
        [Range(80000, 93000)]
        [Display(Name = "Structure lookback start (HHmmss)", Order = 4, GroupName = "Schedule")]
        public int StructureLookbackStart { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "Pivot strength", Order = 1, GroupName = "Signal")]
        public int PivotStrength { get; set; }

        [NinjaScriptProperty]
        [Range(1, 1)]
        [Display(Name = "Quantity", Description = "Version 1 is fixed to one MES contract.", Order = 1, GroupName = "Risk")]
        public int TradeQuantity { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, 50.0)]
        [Display(Name = "Maximum planned risk ($)", Order = 2, GroupName = "Risk")]
        public double MaximumPlannedRisk { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, 10.0)]
        [Display(Name = "Reward multiple", Order = 3, GroupName = "Risk")]
        public double RewardMultiple { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, 100.0)]
        [Display(Name = "Daily loss lock ($)", Order = 4, GroupName = "Risk")]
        public double DailyLossLimit { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Allowed accounts", Description = "May only narrow the built-in Sim101/Playback101 allowlist.", Order = 1, GroupName = "Safety")]
        public string AllowedAccountNames { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Audit endpoint", Order = 1, GroupName = "Audit")]
        public string AuditEndpoint { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Validation mode", Description = "Off, non-blocking Shadow, or fail-closed Required.", Order = 1, GroupName = "LLM validation")]
        public LlmValidationMode LlmGateMode { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Validation endpoint", Order = 2, GroupName = "LLM validation")]
        public string LlmValidationEndpoint { get; set; }

        [NinjaScriptProperty]
        [Range(500, 10000)]
        [Display(Name = "Decision timeout (ms)", Order = 3, GroupName = "LLM validation")]
        public int LlmDecisionTimeoutMilliseconds { get; set; }

        [NinjaScriptProperty]
        [Range(1, 30)]
        [Display(Name = "Maximum decision age (seconds)", Order = 4, GroupName = "LLM validation")]
        public int LlmMaximumDecisionAgeSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 1.0)]
        [Display(Name = "Minimum allow confidence", Order = 5, GroupName = "LLM validation")]
        public double LlmMinimumAllowConfidence { get; set; }

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Simulation-only MES 15-minute ORB with confirmed five-minute swing structure.";
                Name = "MesOrbStructureV1";
                Calculate = Calculate.OnBarClose;
                IsOverlay = true;
                BarsRequiredToTrade = 10;
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
                StructureLookbackStart = 83000;
                PivotStrength = 2;
                TradeQuantity = 1;
                MaximumPlannedRisk = 50.0;
                RewardMultiple = 2.0;
                DailyLossLimit = 100.0;
                AllowedAccountNames = "Sim101,Playback101";
                AuditEndpoint = "http://127.0.0.1:8000/events";
                LlmGateMode = LlmValidationMode.Off;
                LlmValidationEndpoint = "http://127.0.0.1:8000/validate";
                LlmDecisionTimeoutMilliseconds = 3500;
                LlmMaximumDecisionAgeSeconds = 6;
                LlmMinimumAllowConfidence = 0.70;
            }
            else if (State == State.Configure)
            {
                AddDataSeries(BarsPeriodType.Minute, 5);
            }
            else if (State == State.DataLoaded)
            {
                strategyInstanceId = Name + "-" + Guid.NewGuid().ToString("N");
                StartAuditWorker();
                StartLlmWorker();
                ResetTradingDay(DateTime.MinValue);
            }
            else if (State == State.Realtime)
            {
                // A chart processes historical preload bars before reaching
                // real time. Preserve the calculated OR/pivots, but start the
                // real-time execution allowance and safety state cleanly.
                entryAttempted = false;
                riskLocked = false;
                runtimeValidated = false;
                flattenIssued = false;
                plannedStopPrice = 0;
                plannedTargetPrice = 0;
                plannedRiskDollars = 0;
                entryFillPrice = 0;
                activeTradeDirection = 0;
                entryOrder = null;
                llmValidationPending = false;
                pendingValidationId = null;
                llmLastStatus = LlmGateMode == LlmValidationMode.Off ? "off" : "ready";
            }
            else if (State == State.Terminated)
            {
                StopLlmWorker();
                StopAuditWorker();
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBars[0] < 1 || CurrentBars[1] < PivotStrength * 2)
                return;

            DateTime barTime = Times[BarsInProgress][0];
            EnsureTradingDay(barTime.Date);

            if (BarsInProgress == 1)
            {
                ProcessFiveMinuteBar();
                return;
            }

            if (BarsInProgress != 0)
                return;

            if (!ValidateStaticConfiguration())
                return;

            if (State == State.Realtime && !ValidateRealtimeEnvironment())
                return;

            ProcessPrimaryBar();
            RenderStatus();
        }

        private void ProcessFiveMinuteBar()
        {
            int candidateBarsAgo = PivotStrength;
            DateTime candidateTime = Times[1][candidateBarsAgo];
            if (candidateTime.Date != activeDate || ToTime(candidateTime) < StructureLookbackStart)
                return;

            double candidateHigh = Highs[1][candidateBarsAgo];
            double candidateLow = Lows[1][candidateBarsAgo];
            bool isSwingHigh = true;
            bool isSwingLow = true;

            for (int offset = 1; offset <= PivotStrength; offset++)
            {
                if (candidateHigh <= Highs[1][candidateBarsAgo - offset]
                    || candidateHigh <= Highs[1][candidateBarsAgo + offset])
                    isSwingHigh = false;
                if (candidateLow >= Lows[1][candidateBarsAgo - offset]
                    || candidateLow >= Lows[1][candidateBarsAgo + offset])
                    isSwingLow = false;
            }

            if (isSwingHigh)
                AddConfirmedPivot(confirmedSwingHighs, candidateHigh);
            if (isSwingLow)
                AddConfirmedPivot(confirmedSwingLows, candidateLow);

            UpdateStructureDirection();
        }

        private void ProcessPrimaryBar()
        {
            int time = ToTime(Time[0]);

            if (time >= FlattenTime)
            {
                FlattenForSessionEnd();
                return;
            }

            if (time > OpeningRangeStart && time <= OpeningRangeEnd)
            {
                openingRangeHigh = openingRangeStarted ? Math.Max(openingRangeHigh, High[0]) : High[0];
                openingRangeLow = openingRangeStarted ? Math.Min(openingRangeLow, Low[0]) : Low[0];
                openingRangeStarted = true;
            }

            if (!openingRangeComplete && openingRangeStarted && time >= OpeningRangeEnd)
            {
                openingRangeComplete = true;
                Draw.HorizontalLine(this, "MES_ORB_HIGH_" + activeDate.ToString("yyyyMMdd"), openingRangeHigh, Brushes.DodgerBlue);
                Draw.HorizontalLine(this, "MES_ORB_LOW_" + activeDate.ToString("yyyyMMdd"), openingRangeLow, Brushes.DodgerBlue);
            }

            if (!openingRangeComplete || time < OpeningRangeEnd
                || entryAttempted || riskLocked || llmValidationPending
                || Position.MarketPosition != MarketPosition.Flat)
                return;

            if (State == State.Realtime && PositionAccount.MarketPosition != MarketPosition.Flat)
            {
                TriggerSafetyLock("account_position_not_flat", true);
                return;
            }

            bool insideRange = Close[0] >= openingRangeLow && Close[0] <= openingRangeHigh;
            if (insideRange)
            {
                if (structureDirection == 1)
                    ArmSetup(1);
                else if (structureDirection == -1)
                    ArmSetup(-1);
                else
                    armedDirection = 0;
                return;
            }

            if (armedDirection == 1 && structureDirection == 1 && Close[0] > openingRangeHigh)
            {
                HandleBreakoutCandidate(1);
                return;
            }

            if (armedDirection == -1 && structureDirection == -1 && Close[0] < openingRangeLow)
            {
                HandleBreakoutCandidate(-1);
                return;
            }

            // An outside close without a matching armed breakout must return
            // inside the range before a later entry can qualify.
            armedDirection = 0;
        }

        private void ArmSetup(int direction)
        {
            if (armedDirection == direction)
                return;

            armedDirection = direction;
            string side = direction == 1 ? "long" : "short";
            Notify("armed-" + activeDate.ToString("yyyyMMdd") + "-" + side,
                "MES ORB " + side.ToUpperInvariant() + " setup armed");
            QueueAuditEvent("setup_armed", direction, Close[0], null, null, null, null, null, null);
        }

        private void HandleBreakoutCandidate(int direction)
        {
            // Historical orders are desired in Strategy Analyzer, but not
            // during the preload phase of a strategy attached to a chart.
            if (State == State.Historical && !IsInStrategyAnalyzer)
            {
                armedDirection = 0;
                return;
            }

            if (State == State.Historical && IsInStrategyAnalyzer
                && LlmGateMode != LlmValidationMode.Off)
            {
                SkipSetup(direction, "llm_mode_not_supported_in_historical_backtest");
                return;
            }

            TradePlan plan;
            string reason;
            if (!TryBuildTradePlan(direction, Close[0], out plan, out reason))
            {
                SkipSetup(direction, reason);
                return;
            }

            if (LlmGateMode == LlmValidationMode.Off || State != State.Realtime)
            {
                SubmitEntry(direction, plan);
                return;
            }

            if (LlmGateMode == LlmValidationMode.Shadow)
            {
                QueueLlmValidation(direction, plan, LlmValidationMode.Shadow);
                SubmitEntry(direction, plan);
                return;
            }

            if (!QueueLlmValidation(direction, plan, LlmValidationMode.Required))
            {
                llmLastStatus = "rejected:queue_unavailable";
                SkipSetup(direction, "llm_queue_unavailable");
            }
        }

        private bool TryBuildTradePlan(int direction, double entryReference,
            out TradePlan plan, out string reason)
        {
            plan = null;
            reason = null;
            if (confirmedSwingHighs.Count < 2 || confirmedSwingLows.Count < 2)
            {
                reason = "missing_confirmed_pivots";
                return false;
            }

            double stop = direction == 1
                ? confirmedSwingLows[confirmedSwingLows.Count - 1] - TickSize
                : confirmedSwingHighs[confirmedSwingHighs.Count - 1] + TickSize;
            stop = Instrument.MasterInstrument.RoundToTickSize(stop);

            double riskPoints = direction == 1 ? entryReference - stop : stop - entryReference;
            double riskDollars = riskPoints * Instrument.MasterInstrument.PointValue * TradeQuantity;
            if (riskPoints <= TickSize || riskDollars <= 0)
            {
                reason = "invalid_stop_geometry";
                return false;
            }
            if (riskDollars > MaximumPlannedRisk)
            {
                reason = "planned_risk_exceeds_limit";
                return false;
            }

            double target = direction == 1
                ? entryReference + RewardMultiple * riskPoints
                : entryReference - RewardMultiple * riskPoints;
            target = Instrument.MasterInstrument.RoundToTickSize(target);
            plan = new TradePlan
            {
                EntryReference = entryReference,
                Stop = stop,
                Target = target,
                RiskDollars = riskDollars
            };
            return true;
        }

        private void SubmitEntry(int direction, TradePlan plan)
        {
            plannedStopPrice = plan.Stop;
            plannedRiskDollars = plan.RiskDollars;
            plannedTargetPrice = plan.Target;
            activeTradeDirection = direction;
            entryAttempted = true;
            armedDirection = 0;

            string signalName = direction == 1 ? LongEntrySignal : ShortEntrySignal;
            SetStopLoss(signalName, CalculationMode.Price, plannedStopPrice, false);
            SetProfitTarget(signalName, CalculationMode.Price, plannedTargetPrice);

            QueueAuditEvent("entry_submitted", direction, plan.EntryReference, plan.EntryReference, plannedStopPrice,
                plannedTargetPrice, plannedRiskDollars, null, null);
            if (direction == 1)
                EnterLong(TradeQuantity, signalName);
            else
                EnterShort(TradeQuantity, signalName);
        }

        private void SkipSetup(int direction, string reason)
        {
            QueueAuditEvent("setup_skipped", direction, Close[0], Close[0], plannedStopPrice,
                plannedTargetPrice, plannedRiskDollars, null, reason);
            Print(Name + " setup skipped: " + reason);
            armedDirection = 0;
        }

        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice,
            int quantity, int filled, double averageFillPrice, OrderState orderState,
            DateTime time, ErrorCode error, string comment)
        {
            if (order == null)
                return;

            if (order.Name == LongEntrySignal || order.Name == ShortEntrySignal)
                entryOrder = order;

            if (orderState == OrderState.Rejected)
            {
                int direction = order.Name == ShortEntrySignal ? -1 : activeTradeDirection;
                QueueAuditEvent("order_rejected", direction, averageFillPrice > 0 ? averageFillPrice : CloseSafe(),
                    entryFillPrice, plannedStopPrice, plannedTargetPrice, plannedRiskDollars, null,
                    error + ":" + comment);
                Notify("rejected-" + eventSequence, "MES ORB order rejected; strategy locked");
                TriggerSafetyLock("order_rejected", true);
            }
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId,
            double price, int quantity, MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (execution == null || execution.Order == null || execution.Order.OrderState != OrderState.Filled)
                return;

            string orderName = execution.Order.Name;
            bool isEntry = orderName == LongEntrySignal || orderName == ShortEntrySignal;
            if (isEntry)
            {
                activeTradeDirection = orderName == LongEntrySignal ? 1 : -1;
                entryFillPrice = execution.Order.AverageFillPrice;
                double actualRiskPoints = activeTradeDirection == 1
                    ? entryFillPrice - plannedStopPrice
                    : plannedStopPrice - entryFillPrice;

                if (actualRiskPoints <= 0)
                {
                    TriggerSafetyLock("fill_invalidates_stop_geometry", true);
                    return;
                }

                plannedTargetPrice = activeTradeDirection == 1
                    ? entryFillPrice + RewardMultiple * actualRiskPoints
                    : entryFillPrice - RewardMultiple * actualRiskPoints;
                plannedTargetPrice = Instrument.MasterInstrument.RoundToTickSize(plannedTargetPrice);
                string signalName = activeTradeDirection == 1 ? LongEntrySignal : ShortEntrySignal;
                SetProfitTarget(signalName, CalculationMode.Price, plannedTargetPrice);

                QueueAuditEvent("entry_filled", activeTradeDirection, entryFillPrice, entryFillPrice,
                    plannedStopPrice, plannedTargetPrice, plannedRiskDollars, null, null);
                QueueAuditEvent("protection_active", activeTradeDirection, entryFillPrice, entryFillPrice,
                    plannedStopPrice, plannedTargetPrice, plannedRiskDollars, null, null);
                Notify("entry-" + activeDate.ToString("yyyyMMdd"),
                    "MES ORB entry filled; stop and target active");
                return;
            }

            if (entryFillPrice > 0 && activeTradeDirection != 0 && marketPosition == MarketPosition.Flat)
            {
                double realized = activeTradeDirection == 1
                    ? (price - entryFillPrice) * Instrument.MasterInstrument.PointValue * quantity
                    : (entryFillPrice - price) * Instrument.MasterInstrument.PointValue * quantity;
                dailyRealizedPnl += realized;
                QueueAuditEvent("exit_filled", activeTradeDirection, price, entryFillPrice,
                    plannedStopPrice, plannedTargetPrice, plannedRiskDollars, realized, orderName);
                Notify("exit-" + eventSequence,
                    "MES ORB exit filled; gross PnL " + realized.ToString("C2", CultureInfo.CurrentCulture));

                entryFillPrice = 0;
                activeTradeDirection = 0;
                if (dailyRealizedPnl <= -DailyLossLimit)
                    TriggerSafetyLock("daily_loss_limit", false);
            }
        }

        protected override void OnConnectionStatusUpdate(ConnectionStatusEventArgs connectionStatusUpdate)
        {
            if (State != State.Realtime || connectionStatusUpdate == null)
                return;

            if (connectionStatusUpdate.PriceStatus == ConnectionStatus.Disconnected)
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
            if (BarsPeriod.BarsPeriodType != BarsPeriodType.Minute || BarsPeriod.Value != 1)
            {
                TriggerSafetyLock("primary_series_must_be_one_minute", State == State.Realtime);
                return false;
            }
            if (!(OpeningRangeStart < OpeningRangeEnd && OpeningRangeEnd < FlattenTime))
            {
                TriggerSafetyLock("invalid_schedule", State == State.Realtime);
                return false;
            }
            if (LlmGateMode != LlmValidationMode.Off && !IsLoopbackEndpoint(LlmValidationEndpoint))
            {
                TriggerSafetyLock("llm_endpoint_must_be_loopback", State == State.Realtime);
                return false;
            }
            return true;
        }

        private static bool IsLoopbackEndpoint(string endpoint)
        {
            Uri uri;
            return Uri.TryCreate(endpoint, UriKind.Absolute, out uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                && uri.IsLoopback;
        }

        private bool ValidateRealtimeEnvironment()
        {
            if (runtimeValidated)
                return !riskLocked;

            runtimeValidated = true;
            if (Account == null || !IsAllowedSimulationAccount(Account.Name))
            {
                TriggerSafetyLock("account_not_allowed", true);
                return false;
            }
            if (TradeQuantity != 1)
            {
                TriggerSafetyLock("quantity_must_equal_one", true);
                return false;
            }
            return true;
        }

        private bool IsAllowedSimulationAccount(string accountName)
        {
            if (accountName != "Sim101" && accountName != "Playback101")
                return false;

            string[] configured = (AllowedAccountNames ?? string.Empty).Split(',');
            foreach (string value in configured)
                if (string.Equals(value.Trim(), accountName, StringComparison.Ordinal))
                    return true;
            return false;
        }

        private void FlattenForSessionEnd()
        {
            if (flattenIssued)
                return;
            flattenIssued = true;
            armedDirection = 0;
            llmValidationPending = false;
            pendingValidationId = null;

            if (entryOrder != null && (entryOrder.OrderState == OrderState.Accepted
                || entryOrder.OrderState == OrderState.Submitted
                || entryOrder.OrderState == OrderState.Working))
                CancelOrder(entryOrder);

            if (Position.MarketPosition == MarketPosition.Long)
                ExitLong("MES_ORB_TIME_EXIT", LongEntrySignal);
            else if (Position.MarketPosition == MarketPosition.Short)
                ExitShort("MES_ORB_TIME_EXIT", ShortEntrySignal);
        }

        private void TriggerSafetyLock(string reason, bool closeStrategy)
        {
            if (!riskLocked)
            {
                riskLocked = true;
                armedDirection = 0;
                llmValidationPending = false;
                pendingValidationId = null;
                Print(Name + " safety lock: " + reason);
                QueueAuditEvent("risk_lockout", activeTradeDirection, CloseSafe(), entryFillPrice,
                    plannedStopPrice, plannedTargetPrice, plannedRiskDollars, dailyRealizedPnl, reason);
                Notify("lock-" + eventSequence, "MES ORB safety lock: " + reason);
            }

            if (closeStrategy && State == State.Realtime)
                CloseStrategy(reason);
        }

        private bool QueueLlmValidation(int direction, TradePlan plan, LlmValidationMode mode)
        {
            if (llmQueue == null || llmQueue.IsAddingCompleted)
                return false;

            string validationId = strategyInstanceId + "-llm-"
                + Interlocked.Increment(ref validationSequence).ToString(CultureInfo.InvariantCulture);
            string json = BuildLlmValidationJson(validationId, direction, plan);
            LlmValidationWork work = new LlmValidationWork
            {
                ValidationId = validationId,
                Json = json,
                Direction = direction,
                Mode = mode,
                RequestedAtUtc = DateTime.UtcNow,
                TimeoutMilliseconds = LlmDecisionTimeoutMilliseconds
            };

            if (!llmQueue.TryAdd(work))
                return false;

            plannedStopPrice = plan.Stop;
            plannedTargetPrice = plan.Target;
            plannedRiskDollars = plan.RiskDollars;
            if (mode == LlmValidationMode.Required)
            {
                llmValidationPending = true;
                pendingValidationId = validationId;
                armedDirection = 0;
                llmLastStatus = "pending";
                Notify("llm-pending-" + validationId, "MES ORB waiting for LLM validation");
            }
            else
            {
                llmLastStatus = "shadow_pending";
            }
            return true;
        }

        private string BuildLlmValidationJson(string validationId, int direction, TradePlan plan)
        {
            string executionMode = Account != null && Account.Name == "Playback101" ? "playback" : "simulation";
            return string.Format(CultureInfo.InvariantCulture,
                "{{\"validation_id\":\"{0}\",\"strategy_instance_id\":\"{1}\",\"strategy\":\"{2}\",\"instrument\":\"{3}\",\"timestamp\":\"{4}\",\"execution_mode\":\"{5}\",\"direction\":\"{6}\",\"quantity\":1,\"opening_range_high\":{7},\"opening_range_low\":{8},\"breakout_close\":{9},\"entry_reference\":{10},\"stop\":{11},\"target\":{12},\"planned_risk\":{13},\"reward_multiple\":{14},\"structure_direction\":\"{15}\",\"confirmed_swing_highs\":{16},\"confirmed_swing_lows\":{17},\"primary_bars\":{18},\"structure_bars\":{19}}}",
                EscapeJson(validationId), EscapeJson(strategyInstanceId), StrategyCode,
                EscapeJson(Instrument.FullName), DateTime.UtcNow.ToString("o"), executionMode,
                direction == 1 ? "long" : "short", JsonFinite(openingRangeHigh),
                JsonFinite(openingRangeLow), JsonFinite(Close[0]), JsonFinite(plan.EntryReference),
                JsonFinite(plan.Stop), JsonFinite(plan.Target), JsonFinite(plan.RiskDollars),
                JsonFinite(RewardMultiple), direction == 1 ? "bullish" : "bearish",
                BuildNumberArray(confirmedSwingHighs), BuildNumberArray(confirmedSwingLows),
                BuildBarsJson(0, LlmSnapshotBars), BuildBarsJson(1, LlmSnapshotBars));
        }

        private string BuildBarsJson(int seriesIndex, int maximumBars)
        {
            int count = Math.Min(CurrentBars[seriesIndex] + 1, maximumBars);
            StringBuilder builder = new StringBuilder("[");
            bool first = true;
            for (int barsAgo = count - 1; barsAgo >= 0; barsAgo--)
            {
                if (!first)
                    builder.Append(',');
                first = false;
                builder.AppendFormat(CultureInfo.InvariantCulture,
                    "{{\"timestamp\":\"{0}\",\"open\":{1},\"high\":{2},\"low\":{3},\"close\":{4},\"volume\":{5}}}",
                    ToUtcIso(Times[seriesIndex][barsAgo]), JsonFinite(Opens[seriesIndex][barsAgo]),
                    JsonFinite(Highs[seriesIndex][barsAgo]), JsonFinite(Lows[seriesIndex][barsAgo]),
                    JsonFinite(Closes[seriesIndex][barsAgo]), JsonFinite(Volumes[seriesIndex][barsAgo]));
            }
            builder.Append(']');
            return builder.ToString();
        }

        private static string BuildNumberArray(List<double> values)
        {
            StringBuilder builder = new StringBuilder("[");
            for (int index = 0; index < values.Count; index++)
            {
                if (index > 0)
                    builder.Append(',');
                builder.Append(JsonFinite(values[index]));
            }
            builder.Append(']');
            return builder.ToString();
        }

        private static string ToUtcIso(DateTime value)
        {
            if (value.Kind == DateTimeKind.Unspecified)
                value = DateTime.SpecifyKind(value, DateTimeKind.Local);
            return value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
        }

        private void StartLlmWorker()
        {
            llmQueue = new BlockingCollection<LlmValidationWork>(LlmQueueCapacity);
            llmCancellation = new CancellationTokenSource();
            llmWorker = Task.Factory.StartNew(ProcessLlmQueue, llmCancellation.Token,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        private void StopLlmWorker()
        {
            try
            {
                if (llmQueue != null && !llmQueue.IsAddingCompleted)
                    llmQueue.CompleteAdding();
                if (llmCancellation != null)
                    llmCancellation.CancelAfter(750);
                if (llmWorker != null)
                    llmWorker.Wait(750);
            }
            catch
            {
            }
            finally
            {
                if (llmCancellation != null)
                    llmCancellation.Dispose();
                if (llmQueue != null)
                    llmQueue.Dispose();
            }
        }

        private void ProcessLlmQueue()
        {
            try
            {
                foreach (LlmValidationWork work in llmQueue.GetConsumingEnumerable(llmCancellation.Token))
                {
                    LlmValidationResult result = PostLlmValidation(work);
                    try
                    {
                        TriggerCustomEvent(HandleLlmValidationResult, 0, result);
                    }
                    catch (Exception ex)
                    {
                        Print(Name + " LLM result dispatch unavailable: " + ex.Message);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private LlmValidationResult PostLlmValidation(LlmValidationWork work)
        {
            LlmValidationResult result = new LlmValidationResult
            {
                ValidationId = work.ValidationId,
                Direction = work.Direction,
                Mode = work.Mode,
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
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(LlmValidationEndpoint);
                request.Method = "POST";
                request.ContentType = "application/json";
                request.ContentLength = body.Length;
                request.Timeout = work.TimeoutMilliseconds;
                request.ReadWriteTimeout = work.TimeoutMilliseconds;
                using (Stream stream = request.GetRequestStream())
                    stream.Write(body, 0, body.Length);
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (StreamReader reader = new StreamReader(response.GetResponseStream()))
                {
                    string json = reader.ReadToEnd();
                    string returnedId = ExtractJsonString(json, "validation_id");
                    if (!string.Equals(returnedId, work.ValidationId, StringComparison.Ordinal))
                    {
                        result.Reason = "llm_validation_id_mismatch";
                        return result;
                    }

                    string decision = ExtractJsonString(json, "decision");
                    result.Decision = decision == "allow" ? "allow" : "reject";
                    result.Confidence = ExtractJsonNumber(json, "confidence");
                    result.Reason = ExtractJsonStringArray(json, "reason_codes");
                    result.Provider = ExtractJsonString(json, "provider") ?? "bridge";
                    result.Model = ExtractJsonString(json, "model") ?? "unknown";
                    if (string.IsNullOrWhiteSpace(result.Reason))
                        result.Reason = "llm_unspecified_decision";
                }
            }
            catch (WebException ex)
            {
                result.Reason = ex.Status == WebExceptionStatus.Timeout
                    ? "llm_timeout" : "llm_bridge_unavailable";
            }
            catch (Exception ex)
            {
                result.Reason = "llm_response_invalid_" + ex.GetType().Name.ToLowerInvariant();
            }
            return result;
        }

        private void HandleLlmValidationResult(object state)
        {
            LlmValidationResult result = state as LlmValidationResult;
            if (result == null || State != State.Realtime)
                return;

            if (result.Mode == LlmValidationMode.Shadow)
            {
                llmLastStatus = "shadow_" + result.Decision + ":" + result.Reason;
                Print(Name + " LLM shadow " + result.Decision + " confidence="
                    + result.Confidence.ToString("0.00", CultureInfo.InvariantCulture)
                    + " reasons=" + result.Reason);
                return;
            }

            if (!llmValidationPending
                || !string.Equals(pendingValidationId, result.ValidationId, StringComparison.Ordinal))
                return;

            llmValidationPending = false;
            pendingValidationId = null;

            double ageSeconds = (DateTime.UtcNow - result.RequestedAtUtc).TotalSeconds;
            if (ageSeconds < 0 || ageSeconds > LlmMaximumDecisionAgeSeconds)
            {
                RejectRequiredLlmValidation(result.Direction, "llm_response_stale");
                return;
            }
            if (result.Decision != "allow")
            {
                RejectRequiredLlmValidation(result.Direction, result.Reason);
                return;
            }
            if (result.Confidence < LlmMinimumAllowConfidence)
            {
                RejectRequiredLlmValidation(result.Direction, "llm_confidence_below_threshold");
                return;
            }

            int time = ToTime(Time[0]);
            bool breakoutStillValid = result.Direction == 1
                ? structureDirection == 1 && Close[0] > openingRangeHigh
                : structureDirection == -1 && Close[0] < openingRangeLow;
            if (!openingRangeComplete || time >= FlattenTime
                || entryAttempted || riskLocked || Position.MarketPosition != MarketPosition.Flat
                || !breakoutStillValid)
            {
                RejectRequiredLlmValidation(result.Direction, "setup_changed_while_waiting");
                return;
            }
            if (PositionAccount.MarketPosition != MarketPosition.Flat)
            {
                TriggerSafetyLock("account_position_not_flat", true);
                return;
            }

            double entryReference = Close[0];
            try
            {
                double currentQuote = result.Direction == 1 ? GetCurrentAsk() : GetCurrentBid();
                if (currentQuote > 0)
                    entryReference = currentQuote;
            }
            catch
            {
            }
            bool quoteStillBeyondRange = result.Direction == 1
                ? entryReference > openingRangeHigh : entryReference < openingRangeLow;
            if (!quoteStillBeyondRange)
            {
                RejectRequiredLlmValidation(result.Direction, "market_returned_inside_range");
                return;
            }

            TradePlan refreshedPlan;
            string reason;
            if (!TryBuildTradePlan(result.Direction, entryReference, out refreshedPlan, out reason))
            {
                RejectRequiredLlmValidation(result.Direction, "post_llm_" + reason);
                return;
            }

            llmLastStatus = "allowed:" + result.Reason;
            Notify("llm-allowed-" + result.ValidationId,
                "MES ORB LLM validation allowed; submitting simulated entry");
            SubmitEntry(result.Direction, refreshedPlan);
        }

        private void RejectRequiredLlmValidation(int direction, string reason)
        {
            llmLastStatus = "rejected:" + reason;
            Notify("llm-rejected-" + validationSequence,
                "MES ORB LLM validation rejected; no entry: " + reason);
            SkipSetup(direction, reason);
        }

        private static string ExtractJsonString(string json, string fieldName)
        {
            string pattern = "\\\"" + Regex.Escape(fieldName)
                + "\\\"\\s*:\\s*\\\"((?:\\\\.|[^\\\"])*)\\\"";
            Match match = Regex.Match(json ?? string.Empty, pattern, RegexOptions.Singleline);
            if (!match.Success)
                return null;
            return match.Groups[1].Value.Replace("\\\"", "\"").Replace("\\\\", "\\");
        }

        private static double ExtractJsonNumber(string json, string fieldName)
        {
            string pattern = "\\\"" + Regex.Escape(fieldName)
                + "\\\"\\s*:\\s*(-?[0-9]+(?:\\.[0-9]+)?)";
            Match match = Regex.Match(json ?? string.Empty, pattern, RegexOptions.Singleline);
            double value;
            return match.Success && double.TryParse(match.Groups[1].Value,
                NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : 0;
        }

        private static string ExtractJsonStringArray(string json, string fieldName)
        {
            string pattern = "\\\"" + Regex.Escape(fieldName) + "\\\"\\s*:\\s*\\[(.*?)\\]";
            Match array = Regex.Match(json ?? string.Empty, pattern, RegexOptions.Singleline);
            if (!array.Success)
                return null;
            List<string> values = new List<string>();
            foreach (Match item in Regex.Matches(array.Groups[1].Value,
                "\\\"((?:\\\\.|[^\\\"])*)\\\"", RegexOptions.Singleline))
                values.Add(item.Groups[1].Value.Replace("\\\"", "\"").Replace("\\\\", "\\"));
            return string.Join(",", values.ToArray());
        }

        private void UpdateStructureDirection()
        {
            if (confirmedSwingHighs.Count < 2 || confirmedSwingLows.Count < 2)
            {
                structureDirection = 0;
                return;
            }

            double previousHigh = confirmedSwingHighs[confirmedSwingHighs.Count - 2];
            double latestHigh = confirmedSwingHighs[confirmedSwingHighs.Count - 1];
            double previousLow = confirmedSwingLows[confirmedSwingLows.Count - 2];
            double latestLow = confirmedSwingLows[confirmedSwingLows.Count - 1];

            if (latestHigh > previousHigh && latestLow > previousLow)
                structureDirection = 1;
            else if (latestHigh < previousHigh && latestLow < previousLow)
                structureDirection = -1;
            else
                structureDirection = 0;
        }

        private static void AddConfirmedPivot(List<double> pivots, double value)
        {
            pivots.Add(value);
            if (pivots.Count > 8)
                pivots.RemoveAt(0);
        }

        private void EnsureTradingDay(DateTime date)
        {
            if (activeDate != date)
                ResetTradingDay(date);
        }

        private void ResetTradingDay(DateTime date)
        {
            activeDate = date;
            confirmedSwingHighs.Clear();
            confirmedSwingLows.Clear();
            openingRangeHigh = double.MinValue;
            openingRangeLow = double.MaxValue;
            openingRangeStarted = false;
            openingRangeComplete = false;
            structureDirection = 0;
            armedDirection = 0;
            entryAttempted = false;
            riskLocked = false;
            runtimeValidated = false;
            flattenIssued = false;
            plannedStopPrice = 0;
            plannedTargetPrice = 0;
            plannedRiskDollars = 0;
            entryFillPrice = 0;
            activeTradeDirection = 0;
            dailyRealizedPnl = 0;
            entryOrder = null;
            llmValidationPending = false;
            pendingValidationId = null;
            llmLastStatus = LlmGateMode == LlmValidationMode.Off ? "off" : "ready";
        }

        private void RenderStatus()
        {
            string range = openingRangeComplete
                ? openingRangeLow.ToString("0.00") + " - " + openingRangeHigh.ToString("0.00")
                : "building";
            string structure = structureDirection == 1 ? "bullish" : structureDirection == -1 ? "bearish" : "neutral";
            string armed = armedDirection == 1 ? "long" : armedDirection == -1 ? "short" : "none";
            string status = string.Format(CultureInfo.InvariantCulture,
                "MES ORB v1 (SIM ONLY)\nOR: {0}\nStructure: {1}\nArmed: {2}\nLLM: {3} ({4})\nEntry attempted: {5}\nSafety lock: {6}\nPlanned risk: ${7:0.00}\nPosition: {8}",
                range, structure, armed, LlmGateMode, llmLastStatus ?? "ready",
                entryAttempted, riskLocked, plannedRiskDollars, Position.MarketPosition);
            Draw.TextFixed(this, "MES_ORB_STATUS", status, TextPosition.TopLeft);
        }

        private void Notify(string tag, string message)
        {
            Print(Name + ": " + message);
            if (State == State.Realtime)
                Alert(tag, Priority.High, message, "Alert3.wav", 10, Brushes.Transparent, Brushes.White);
        }

        private double CloseSafe()
        {
            try
            {
                return Close[0];
            }
            catch
            {
                return 0;
            }
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
                if (auditQueue != null && !auditQueue.IsAddingCompleted)
                    auditQueue.CompleteAdding();
                if (auditCancellation != null)
                    auditCancellation.CancelAfter(750);
                if (auditWorker != null)
                    auditWorker.Wait(750);
            }
            catch
            {
                // Audit shutdown must never block strategy termination.
            }
            finally
            {
                if (auditCancellation != null)
                    auditCancellation.Dispose();
                if (auditQueue != null)
                    auditQueue.Dispose();
            }
        }

        private void ProcessAuditQueue()
        {
            try
            {
                foreach (string json in auditQueue.GetConsumingEnumerable(auditCancellation.Token))
                    PostAuditEvent(json);
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void QueueAuditEvent(string eventType, int direction, double price,
            double? entry, double? stop, double? target, double? risk,
            double? realizedPnl, string reason)
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
                JsonNumber(risk), JsonNumber(realizedPnl), JsonString(reason));

            if (!auditQueue.TryAdd(json))
                Print(Name + " audit queue full; event dropped: " + eventType);
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
                using (Stream stream = request.GetRequestStream())
                    stream.Write(body, 0, body.Length);
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300)
                        Print(Name + " audit HTTP " + (int)response.StatusCode);
                }
            }
            catch (Exception ex)
            {
                Print(Name + " audit unavailable: " + ex.Message);
            }
        }

        private static string JsonNumber(double value)
        {
            if (value <= 0 || double.IsNaN(value) || double.IsInfinity(value))
                return "null";
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static string JsonFinite(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return "null";
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static string JsonNumber(double? value)
        {
            if (!value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
                return "null";
            return value.Value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static string JsonString(string value)
        {
            return value == null ? "null" : "\"" + EscapeJson(value) + "\"";
        }

        private static string EscapeJson(string value)
        {
            return (value ?? string.Empty)
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n")
                .Replace("\t", "\\t");
        }
    }
}
