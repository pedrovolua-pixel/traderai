#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class TrendPullbackV1 : Strategy
    {
        private EMA ema20;
        private EMA ema50;
        private ATR atr14;
        private MIN recentLow;
        private bool pullbackArmed;
        private bool waitingForReset;
        private bool developerSignalSent;
        private int pullbackBar;
        private double pullbackSwingLow;

        [NinjaScriptProperty]
        [Display(Name = "Developer test mode", Description = "Emit one synthetic valid setup for connectivity testing only.", Order = 1, GroupName = "Developer")]
        public bool DeveloperTestMode { get; set; }

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Deterministic long trend-pullback setup detector. Never places orders.";
                Name = "TrendPullbackV1";
                Calculate = Calculate.OnBarClose;
                IsOverlay = true;
                BarsRequiredToTrade = 60;
                PrintTo = PrintTo.OutputTab1;
                DeveloperTestMode = false;
            }
            else if (State == State.DataLoaded)
            {
                ema20 = EMA(20);
                ema50 = EMA(50);
                atr14 = ATR(14);
                recentLow = MIN(Low, 10);
                pullbackArmed = false;
                waitingForReset = false;
                developerSignalSent = false;
                pullbackBar = -1;
            }
        }

        protected override void OnBarUpdate()
        {
            // NinjaTrader calls OnBarUpdate for historical chart data when a
            // strategy is enabled. Historical setups must never be POSTed to
            // the supervised bridge; only a newly closed realtime bar may
            // produce an external signal.
            if (State != State.Realtime)
                return;

            if (CurrentBar < BarsRequiredToTrade)
                return;

            double atr = atr14[0];
            if (!IsFinitePositive(atr))
                return;

            if (DeveloperTestMode && !developerSignalSent)
            {
                developerSignalSent = true;
                EmitDeveloperTestSignal(atr);
                return;
            }

            bool bullishTrend = ema20[0] > ema50[0];
            bool priceAboveRegimeFloor = Close[0] >= ema50[0] - (0.25 * atr);

            if (waitingForReset)
            {
                bool trendInvalidated = !bullishTrend || !priceAboveRegimeFloor;
                bool priceSeparated = Low[0] > ema20[0] + (0.50 * atr);
                if (trendInvalidated || priceSeparated)
                    waitingForReset = false;
                return;
            }

            if (!bullishTrend || !priceAboveRegimeFloor)
            {
                ResetPullback();
                return;
            }

            if (!pullbackArmed)
            {
                bool touchedTriggerArea = Low[0] <= ema20[0] + (0.25 * atr);
                if (touchedTriggerArea)
                {
                    pullbackArmed = true;
                    pullbackBar = CurrentBar;
                    pullbackSwingLow = recentLow[0];
                }
                return;
            }

            bool expired = CurrentBar - pullbackBar > 8;
            bool structureBroken = Close[0] <= pullbackSwingLow;
            if (expired || structureBroken)
            {
                ResetPullback();
                return;
            }

            bool bullishBar = Close[0] > Open[0];
            bool reclaimedEma20 = Close[0] > ema20[0];
            bool brokePriorHigh = Close[0] > High[1];
            if (CurrentBar > pullbackBar && bullishBar && reclaimedEma20 && brokePriorHigh)
                EmitQualifiedSignal(atr);
        }

        private void EmitQualifiedSignal(double atr)
        {
            double entry = Close[0];
            double stop = pullbackSwingLow - (0.10 * atr);
            if (!IsFinitePositive(entry) || !IsFinitePositive(stop) || stop >= entry)
            {
                Print("TrendPullbackV1 candidate rejected locally: invalid entry/stop geometry");
                ResetPullback();
                return;
            }

            double risk = entry - stop;
            double target1 = entry + risk;
            double target2 = entry + (2.0 * risk);
            SendSignal(entry, stop, target1, target2, ema20[0], ema50[0], atr,
                pullbackSwingLow, "qualified");
            pullbackArmed = false;
            waitingForReset = true;
        }

        private void EmitDeveloperTestSignal(double atr)
        {
            double entry = Close[0];
            double testAtr = Math.Max(atr, TickSize * 4.0);
            double stop = entry - testAtr;
            double target1 = entry + testAtr;
            double target2 = entry + (2.0 * testAtr);
            double testEma50 = entry - (2.0 * testAtr);
            double testEma20 = entry - testAtr;
            double testSwingLow = entry - (0.90 * testAtr);
            SendSignal(entry, stop, target1, target2, testEma20, testEma50,
                testAtr, testSwingLow, "qualified_test");
        }

        private void SendSignal(double entry, double stop, double target1,
            double target2, double ema20Value, double ema50Value, double atr,
            double swingLow, string setupState)
        {
            double risk = entry - stop;
            double rawRr = (target2 - entry) / risk;
            double[] values = { entry, stop, target1, target2, ema20Value,
                ema50Value, atr, swingLow, rawRr };
            foreach (double value in values)
            {
                if (double.IsNaN(value) || double.IsInfinity(value))
                {
                    Print("TrendPullbackV1 candidate rejected locally: non-finite value");
                    return;
                }
            }

            string json = string.Format(CultureInfo.InvariantCulture,
                "{{\"instrument\":\"{0}\",\"strategy\":\"trend_pullback_v1\",\"direction\":\"long\",\"timeframe\":\"{1}\",\"timestamp\":\"{2}\",\"price\":{3},\"entry\":{3},\"stop\":{4},\"target1\":{5},\"target2\":{6},\"ema20\":{7},\"ema50\":{8},\"vwap\":null,\"atr\":{9},\"recent_swing_low\":{10},\"regime\":\"bullish\",\"setup_state\":\"{11}\",\"raw_rr\":{12}}}",
                EscapeJson(Instrument.FullName),
                EscapeJson(BarsPeriod.Value + BarsPeriod.BarsPeriodType.ToString()),
                DateTime.UtcNow.ToString("o"), entry, stop, target1, target2,
                ema20Value, ema50Value, atr, swingLow, setupState, rawRr);

            PostJson(json);
        }

        private void PostJson(string json)
        {
            const string endpoint = "http://127.0.0.1:8000/signal";
            try
            {
                byte[] body = Encoding.UTF8.GetBytes(json);
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(endpoint);
                request.Method = "POST";
                request.ContentType = "application/json";
                request.ContentLength = body.Length;
                request.Timeout = 5000;
                using (Stream requestStream = request.GetRequestStream())
                    requestStream.Write(body, 0, body.Length);
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (StreamReader reader = new StreamReader(response.GetResponseStream()))
                    Print(string.Format("TrendPullbackV1 HTTP {0}: {1}",
                        (int)response.StatusCode, reader.ReadToEnd()));
            }
            catch (WebException ex)
            {
                string responseBody = "";
                if (ex.Response != null)
                    using (StreamReader reader = new StreamReader(ex.Response.GetResponseStream()))
                        responseBody = reader.ReadToEnd();
                Print("TrendPullbackV1 connection error: " + ex.Message + " " + responseBody);
            }
            catch (Exception ex)
            {
                Print("TrendPullbackV1 unexpected error: " + ex.Message);
            }
        }

        private void ResetPullback()
        {
            pullbackArmed = false;
            pullbackBar = -1;
            pullbackSwingLow = 0;
        }

        private static bool IsFinitePositive(double value)
        {
            return value > 0 && !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static string EscapeJson(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
