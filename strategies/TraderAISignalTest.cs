#region Using declarations
using System;
using System.IO;
using System.Net;
using System.Text;
using NinjaTrader.NinjaScript;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class TraderAISignalTest : Strategy
    {
        private bool signalSent;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Sends one connectivity-test signal to the local TraderAI bridge. Never places orders.";
                Name = "TraderAISignalTest";
                Calculate = Calculate.OnBarClose;
                IsOverlay = false;
                PrintTo = PrintTo.OutputTab1;
            }
            else if (State == State.DataLoaded)
            {
                signalSent = false;
            }
        }

        protected override void OnBarUpdate()
        {
            if (signalSent || CurrentBar < 0)
                return;

            signalSent = true;
            SendTestSignal();
        }

        private void SendTestSignal()
        {
            const string endpoint = "http://127.0.0.1:8000/signal";
            string instrument = Instrument.FullName;
            string timeframe = BarsPeriod.Value + BarsPeriod.BarsPeriodType.ToString();
            string timestamp = DateTime.UtcNow.ToString("o");
            string json = string.Format(
                "{{\"instrument\":\"{0}\",\"strategy\":\"connection_test\",\"direction\":\"long\",\"timeframe\":\"{1}\",\"price\":{2},\"timestamp\":\"{3}\"}}",
                EscapeJson(instrument),
                EscapeJson(timeframe),
                Close[0].ToString(System.Globalization.CultureInfo.InvariantCulture),
                timestamp);

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
                {
                    string responseBody = reader.ReadToEnd();
                    Print(string.Format("TraderAI HTTP {0}: {1}", (int)response.StatusCode, responseBody));
                }
            }
            catch (WebException ex)
            {
                string responseBody = "";
                if (ex.Response != null)
                {
                    using (StreamReader reader = new StreamReader(ex.Response.GetResponseStream()))
                        responseBody = reader.ReadToEnd();
                }
                Print("TraderAI connection error: " + ex.Message + " " + responseBody);
            }
            catch (Exception ex)
            {
                Print("TraderAI unexpected error: " + ex.Message);
            }
        }

        private static string EscapeJson(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
