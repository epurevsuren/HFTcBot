using System;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Internals;
using FxCalendarLibrary;
using Newtonsoft.Json;
using System.Net;
using System.Collections.Generic;
using System.IO;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.EasternStandardTime, AccessRights = AccessRights.FullAccess)]
    public class HFTcBot : Robot
    {
        [Parameter("Volume USD", Group = "Volume", DefaultValue = 1000, MinValue = 1000, Step = 1000)]
        public int volume { get; set; }

        [Parameter("Maximum Take Profit (pips)", Group = "Protection", DefaultValue = 13, MinValue = 2)]
        public int maximumTp { get; set; }

        [Parameter("Trailing Stop", Group = "Protection", DefaultValue = true)]
        public bool trailingStop { get; set; }

        [Parameter("Early Minute", Group = "Time", DefaultValue = 5)]
        public int earlyMinute { get; set; }

        [Parameter("Delay Minute (-30 to 0)", Group = "Time", DefaultValue = 0, MaxValue = 0, MinValue = -30)]
        public int delayMinute { get; set; }

        [Parameter("Back Test", Group = "Time", DefaultValue = false)]
        public bool backTest { get; set; }

        [Parameter("URL", Group = "Config", DefaultValue = "https://sslecal2.forexprostools.com/?calType=day")]
        public string url { get; set; }

        [Parameter("JSON URL", Group = "Config", DefaultValue = "http://mncashier.xyz/economic-calendar-api/")]
        public string jsonUrl { get; set; }

        [Parameter("JSON Config", Group = "Config", DefaultValue = "C:\\xampp\\htdocs\\economic-calendar-api\\jsonConfig\\")]
        public string jsonConfigLoc { get; set; }



        private int todayInt = 0;

        private DateTimeOffset nextKey;

        #region Config
        private static string[] skippedEvent = 
        {
            "Oil",
            "Fuel",
            "Gas",
            "EIA",
            "Survey"
        };

        private string label = "High Frequency Trading cBot";
        private static PrimaryClass _primaryClass;
        private NewsRetriever _newsRetriever;
        private static LogWriter _log;

        private static Dictionary<string, string> countries = new Dictionary<string, string>();

        private static Dictionary<string, string> currencies = new Dictionary<string, string>();

        private static Dictionary<string, int> currencyPipSize = new Dictionary<string, int>();

        private static Dictionary<string, int> eventTime = new Dictionary<string, int>();

        private static Dictionary<int, double> tradingHoursStrength = new Dictionary<int, double>();

        private static Dictionary<DateTimeOffset, List<FxCalendar>> eventHash = new Dictionary<DateTimeOffset, List<FxCalendar>>();

        private Dictionary<DateTimeOffset, bool> finishedEvents = new Dictionary<DateTimeOffset, bool>();

        private static List<FxCalendar> calendars;

        private void init()
        {
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;

            countries = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(jsonConfigLoc + "countries.json"));

            currencies = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(jsonConfigLoc + "currencies.json"));

            currencyPipSize = JsonConvert.DeserializeObject<Dictionary<string, int>>(File.ReadAllText(jsonConfigLoc + "currencyPipSize.json"));

            tradingHoursStrength = JsonConvert.DeserializeObject<Dictionary<int, double>>(File.ReadAllText(jsonConfigLoc + "tradingHoursStrength.json"));

            eventTime = JsonConvert.DeserializeObject<Dictionary<string, int>>(File.ReadAllText(jsonConfigLoc + "eventTime.json"));

            var jsonString = new WebClient().DownloadString(jsonUrl);
            calendars = JsonConvert.DeserializeObject<List<FxCalendar>>(jsonString);
        }
        #endregion

        #region FromConsole
        private void eventsOfToday()
        {
            var time = Time;
            nextKey = new DateTimeOffset(time.Year, time.Month, time.Day, 0, 0, 0, new TimeSpan(8, 0, 0));
            string todayStr = time.ToString("dddd, MMMM d, yyyy");

            _newsRetriever = new NewsRetriever(todayStr);
            _log = _newsRetriever._log;
            _log.WriteLine(todayStr);

            int today = time.Day;
            _log.WriteLine(time.ToString());

            foreach (var cs in calendars)
            {
                if (cs.data.Day == today && cs.forecast != null && cs.forecast != "" && cs.previous != null && cs.previous != "" && !skippedEvent.Any(cs.name.Contains))
                {
                    List<FxCalendar> temp;
                    if (!eventHash.TryGetValue(cs.data, out temp))
                    {
                        temp = new List<FxCalendar>();
                        eventHash.Add(cs.data, temp);
                        finishedEvents.Add(cs.data, false);
                    }
                    temp.Add(cs);
                }
            }

            Print("Today events initiated!");

        }

        #endregion

        #region UniqueModification

        private bool getEvents(List<FxCalendar> temp)
        {
            string current_url = url;

            current_url += _primaryClass.urlBuilder(temp);
            string html = _newsRetriever.htmlWork(current_url);

            List<OrderBook> ob = _primaryClass.eventManager(temp, html);

            if (ob != null && ob.Any())
            {
                makeOrder(ob);
                return true;
            }
            else
            {
                return false;
            }

        }

        private void mainFunc()
        {
            try
            {
                init();

                _primaryClass = new PrimaryClass(maximumTp, countries, currencies, currencyPipSize, eventTime, tradingHoursStrength);


            } catch (Exception ex)
            {
                Print(ex.Message);
            }

        }

        private void makeOrder(List<OrderBook> ob)
        {
            try
            {
                foreach (var e in ob)
                {
                    marketOrder(e.getSymbol(), e.getTakeProfit(), e.getStopLoss(), e.getOrderType());
                }

            } catch (Exception ex)
            {
                Print(ex.Message);
                _log.WriteLine(ex.Message);
            }
        }

        #endregion

        protected override void OnStart()
        {
            // Put your initialization logic here
            Print(label + " started...");
            Print("Loading...");
            mainFunc();
            Print("Load Finish!");
            Timer.Start(1);
        }

        protected override void OnTimer()
        {
            if (backTest)
                backTesting();
            else
                earlyAccess();
        }

        private void earlyAccess()
        {
            var time = Time;

            checkDayChange(time.AddMinutes(earlyMinute).Day);

            DateTimeOffset keyTime = new DateTimeOffset(time.Year, time.Month, time.Day, time.Hour, time.Minute, 0, new TimeSpan(8, 0, 0));
            //Print("KeyTime={0}", keyTime.ToString());

            DateTimeOffset hashKey = getNextKey(keyTime.AddMinutes(delayMinute));
            //Print("HashKeyTime={0}", hashKey.ToString());

            keyTime = keyTime.AddMinutes(earlyMinute);

            if (keyTime.CompareTo(hashKey) == 1)
                doWork(hashKey);
        }

        private void backTesting()
        {
            var time = Time.AddMinutes(earlyMinute);

            checkDayChange(time.Day);

            DateTimeOffset keyTime = new DateTimeOffset(time.Year, time.Month, time.Day, time.Hour, time.Minute, 0, new TimeSpan(8, 0, 0));
            Print("KeyTime={0}", keyTime.ToString());

            DateTimeOffset hashKey = getNextKey(keyTime);
            Print("HashKeyTime={0}", hashKey.ToString());

            if (keyTime.CompareTo(hashKey) == 0)
                doWork(hashKey);
        }

        private void checkDayChange(int td)
        {
            if (td != todayInt)
            {
                eventsOfToday();

                _primaryClass.setNewsRetriever(_newsRetriever);

                todayInt = td;
            }
        }

        private void doWork(DateTimeOffset hashKey)
        {
            List<FxCalendar> temp;
            if (eventHash.TryGetValue(hashKey, out temp))
            {
                bool isDone;
                finishedEvents.TryGetValue(hashKey, out isDone);

                if (!isDone)
                {
                    _log.WriteLine(hashKey.ToString());
                    int eventCount = temp.Count;
                    _log.WriteLine("Event count at " + hashKey.ToString("HH:mm") + " : " + eventCount);

                    if (getEvents(temp))
                    {
                        Print("Events done!");
                        finishedEvents[hashKey] = true;
                    }

                    _log.WriteLine("~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~");

                }
            }
        }

        private DateTimeOffset getNextKey(DateTimeOffset k)
        {
            if (k.CompareTo(nextKey) == 1)
            {
                foreach (var h in eventHash)
                {
                    if (h.Key >= k)
                    {
                        nextKey = h.Key;
                        break;
                    }
                }
            }
            return nextKey;
        }

        protected override void OnStop()
        {
            // Put your deinitialization logic here
        }

        private void marketOrder(string currentSymbol, int takeProfitInPips, int stopLossInPips, bool orderType)
        {
            try
            {
                TradeType entryType = TradeType.Sell;

                if (orderType)
                    entryType = TradeType.Buy;

                string currentLabel = currentSymbol + " " + takeProfitInPips + " " + stopLossInPips + " " + orderType;
                Print(currentLabel);

                bool _trailingStop = trailingStop;

                if (stopLossInPips < 3)
                    _trailingStop = false;

                ExecuteMarketOrder(entryType, currentSymbol, volume, currentLabel, stopLossInPips, takeProfitInPips, "", _trailingStop, StopTriggerMethod.Trade);
            } catch (Exception ex)
            {
                Print(ex.Message);
                _log.WriteLine(ex.Message);
            }
        }
    }
}
