using Newtonsoft.Json;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Net;
using System.Reflection;
using System.Text;

namespace IRIS_Marketing.Services
{
    public class NotificationService
    {
        public async static void SendNotification(string apiBusiness, string mainArea, string subArea, string message, string errorMsg, NotificationType notificationType, NotificationProvider providers = NotificationProvider.Slack, bool enableInformationNotifications = false)
        {
            if (notificationType != NotificationType.Information || enableInformationNotifications)
            {
                using (var rateGate = new RateGate(1, TimeSpan.FromSeconds(1)))
                {
                    rateGate.WaitToProceed();
                    DoNotification(apiBusiness, mainArea, subArea, message, errorMsg, notificationType);
                }
            }
        }

        public async static Task<bool> SendNotificationAsync(string apiBusiness, string mainArea, string subArea, string message, string errorMsg, NotificationType notificationType, NotificationProvider providers = NotificationProvider.Slack, bool enableInformationNotifications = false)
        {
            if (notificationType != NotificationType.Information || enableInformationNotifications)
            {
                return await DoNotification(apiBusiness, mainArea, subArea, message, errorMsg, notificationType);
            }

            return true;
        }

        private async static Task<bool> DoNotification(string apiBusiness, string mainArea, string subArea, string message, string errorMsg, NotificationType notificationType, NotificationProvider providers = NotificationProvider.Slack)
        {
            bool result = false;

            var providerflags = EnumHelpers.GetFlags(providers);

            foreach (var flag in providerflags)
            {
                var _notificationprovider = new NotificationConfig().NotificationProviders[(NotificationProvider)flag];

                try
                {
                    result = await _notificationprovider.SendNotification(apiBusiness, mainArea, subArea, message, errorMsg, notificationType);
                }
                catch (Exception ex)
                {
                    result = false;
                }
            }

            return result;
        }
    }

    [Flags]
    public enum NotificationType
    {
        Error,
        Exception,
        Warning,
        Information
    }

    /// <summary>
    /// Used to control the rate of some occurrence per unit of time.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///     To control the rate of an action using a <see cref="RateGate"/>, 
    ///     code should simply call <see cref="WaitToProceed()"/> prior to 
    ///     performing the action. <see cref="WaitToProceed()"/> will block
    ///     the current thread until the action is allowed based on the rate 
    ///     limit.
    ///     </para>
    ///     <para>
    ///     This class is thread safe. A single <see cref="RateGate"/> instance 
    ///     may be used to control the rate of an occurrence across multiple 
    ///     threads.
    ///     </para>
    /// </remarks>
    /// URL: http://www.jackleitch.net/2010/10/better-rate-limiting-with-dot-net/
    public class RateGate : IDisposable
    {
        // Semaphore used to count and limit the number of occurrences per
        // unit time.
        private readonly SemaphoreSlim _semaphore;

        // Times (in millisecond ticks) at which the semaphore should be exited.
        private readonly ConcurrentQueue<int> _exitTimes;

        // Timer used to trigger exiting the semaphore.
        private readonly Timer _exitTimer;

        // Whether this instance is disposed.
        private bool _isDisposed;

        /// <summary>
        /// Number of occurrences allowed per unit of time.
        /// </summary>
        public int Occurrences { get; private set; }

        /// <summary>
        /// The length of the time unit, in milliseconds.
        /// </summary>
        public int TimeUnitMilliseconds { get; private set; }

        /// <summary>
        /// Initializes a <see cref="RateGate"/> with a rate of <paramref name="occurrences"/> 
        /// per <paramref name="timeUnit"/>.
        /// </summary>
        /// <param name="occurrences">Number of occurrences allowed per unit of time.</param>
        /// <param name="timeUnit">Length of the time unit.</param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// If <paramref name="occurrences"/> or <paramref name="timeUnit"/> is negative.
        /// </exception>
        public RateGate(int occurrences, TimeSpan timeUnit)
        {
            // Check the arguments.
            if (occurrences <= 0)
                throw new ArgumentOutOfRangeException("occurrences", "Number of occurrences must be a positive integer");
            if (timeUnit != timeUnit.Duration())
                throw new ArgumentOutOfRangeException("timeUnit", "Time unit must be a positive span of time");
            if (timeUnit >= TimeSpan.FromMilliseconds(UInt32.MaxValue))
                throw new ArgumentOutOfRangeException("timeUnit", "Time unit must be less than 2^32 milliseconds");

            Occurrences = occurrences;
            TimeUnitMilliseconds = (int)timeUnit.TotalMilliseconds;

            // Create the semaphore, with the number of occurrences as the maximum count.
            _semaphore = new SemaphoreSlim(Occurrences, Occurrences);

            // Create a queue to hold the semaphore exit times.
            _exitTimes = new ConcurrentQueue<int>();

            // Create a timer to exit the semaphore. Use the time unit as the original
            // interval length because that's the earliest we will need to exit the semaphore.
            _exitTimer = new Timer(ExitTimerCallback, null, TimeUnitMilliseconds, -1);
        }

        // Callback for the exit timer that exits the semaphore based on exit times 
        // in the queue and then sets the timer for the nextexit time.
        private void ExitTimerCallback(object state)
        {
            // While there are exit times that are passed due still in the queue,
            // exit the semaphore and dequeue the exit time.
            int exitTime;
            while (_exitTimes.TryPeek(out exitTime)
                    && unchecked(exitTime - Environment.TickCount) <= 0)
            {
                _semaphore.Release();
                _exitTimes.TryDequeue(out exitTime);
            }

            // Try to get the next exit time from the queue and compute
            // the time until the next check should take place. If the 
            // queue is empty, then no exit times will occur until at least
            // one time unit has passed.
            int timeUntilNextCheck;
            if (_exitTimes.TryPeek(out exitTime))
                timeUntilNextCheck = unchecked(exitTime - Environment.TickCount);
            else
                timeUntilNextCheck = TimeUnitMilliseconds;

            // Set the timer.
            _exitTimer.Change(timeUntilNextCheck, -1);
        }

        /// <summary>
        /// Blocks the current thread until allowed to proceed or until the
        /// specified timeout elapses.
        /// </summary>
        /// <param name="millisecondsTimeout">Number of milliseconds to wait, or -1 to wait indefinitely.</param>
        /// <returns>true if the thread is allowed to proceed, or false if timed out</returns>
        public bool WaitToProceed(int millisecondsTimeout)
        {
            // Check the arguments.
            if (millisecondsTimeout < -1)
                throw new ArgumentOutOfRangeException("millisecondsTimeout");

            CheckDisposed();

            // Block until we can enter the semaphore or until the timeout expires.
            var entered = _semaphore.Wait(millisecondsTimeout);

            // If we entered the semaphore, compute the corresponding exit time 
            // and add it to the queue.
            if (entered)
            {
                var timeToExit = unchecked(Environment.TickCount + TimeUnitMilliseconds);
                _exitTimes.Enqueue(timeToExit);
            }

            return entered;
        }

        /// <summary>
        /// Blocks the current thread until allowed to proceed or until the
        /// specified timeout elapses.
        /// </summary>
        /// <param name="timeout"></param>
        /// <returns>true if the thread is allowed to proceed, or false if timed out</returns>
        public bool WaitToProceed(TimeSpan timeout)
        {
            return WaitToProceed((int)timeout.TotalMilliseconds);
        }

        /// <summary>
        /// Blocks the current thread indefinitely until allowed to proceed.
        /// </summary>
        public void WaitToProceed()
        {
            WaitToProceed(Timeout.Infinite);
        }

        // Throws an ObjectDisposedException if this object is disposed.
        private void CheckDisposed()
        {
            if (_isDisposed)
                throw new ObjectDisposedException("RateGate is already disposed");
        }

        /// <summary>
        /// Releases unmanaged resources held by an instance of this class.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases unmanaged resources held by an instance of this class.
        /// </summary>
        /// <param name="isDisposing">Whether this object is being disposed.</param>
        protected virtual void Dispose(bool isDisposing)
        {
            if (!_isDisposed)
            {
                if (isDisposing)
                {
                    // The semaphore and timer both implement IDisposable and 
                    // therefore must be disposed.
                    _semaphore.Dispose();
                    _exitTimer.Dispose();

                    _isDisposed = true;
                }
            }
        }
    }

    [Flags]
    public enum NotificationProvider
    {
        Slack = 0,
        Email = 1,
    }

    public class NotificationConfig
    {
        public Dictionary<NotificationProvider, INotification> NotificationProviders = new Dictionary<NotificationProvider, INotification>();

        public NotificationConfig()
        {
            NotificationProviders.Add(NotificationProvider.Slack, new Slack());
        }
    }

    public interface INotification
    {
        Task<bool> SendNotification(string apiBusiness, string mainArea, string subArea, string message, string errorMsg, NotificationType notificationType);
    }

    public static class EnumHelpers
    {
        public static IEnumerable<Enum> GetFlags(Enum input)
        {
            foreach (Enum value in Enum.GetValues(input.GetType()))
                if (input.HasFlag(value))
                    yield return value;
        }

        public static string GetDescription(this Enum value)
        {
            FieldInfo field = value.GetType().GetField(value.ToString());

            DescriptionAttribute attribute
                = Attribute.GetCustomAttribute(field, typeof(DescriptionAttribute))
                    as DescriptionAttribute;

            return attribute == null ? value.ToString() : attribute.Description;
        }
    }

    public class Slack : INotification
    {
        /// <summary>
        /// Slack login details
        /// </summary>
        private const string SLACK_ACCOUNT = "imaginedigitial";
        private const string BOT_USERNAME = "neptune_api_bot";
        private const string BOT_CHANNEL = "neptune_api";
        private const string BOT_API_TOKEN = "xoxb-328177193699-oygB7xvq7RkJlk5zdP0EeTAW";

        public Slack() { }

        public async Task<bool> SendNotification(string apiBusiness, string mainArea, string subArea, string message, string errorMsg, NotificationType notificationType)
        {
            if (!string.IsNullOrEmpty(mainArea))
                mainArea = " - " + mainArea;

            if (!string.IsNullOrEmpty(subArea))
                subArea = " - " + subArea;

            if (!string.IsNullOrEmpty(message))
                message = " - MSG: " + message;

            if (!string.IsNullOrEmpty(errorMsg))
            {
                string title = "ERROR MSG";
                if (notificationType.HasFlag(NotificationType.Exception))
                    title = "EXCEPTION";

                errorMsg = $" - {title}: {errorMsg}";
            }
            string msg = $"{DateTime.Now.ToString()} - {apiBusiness}{mainArea}{subArea}{message}{errorMsg}";

            return await SendNotification(msg);
        }


        private async Task<bool> SendNotification(string message)
        {
            SlackClientAPI client = new SlackClientAPI();

            Arguments p = new Arguments();
            p.Channel = BOT_CHANNEL;
            p.Username = BOT_USERNAME;
            p.Text = message;
            p.Token = BOT_API_TOKEN;

            var response = await client.PostMessageAsync(p);

            if (response.Ok)
            {
                return true;
            }

            return false;
        }
    }

    public class SlackClientAPI
    {
        private readonly Uri _uri;
        private readonly Encoding _encoding = new UTF8Encoding();

        public SlackClientAPI()
        {
            _uri = new Uri("https://slack.com/api/chat.postMessage");
        }

        //Post a message using simple strings
        public void PostMessage(string token, string text, string username = null, string channel = null)
        {
            Arguments args = new Arguments()
            {
                Token = token,
                Channel = channel,
                Username = username,
                Text = text
            };

            PostMessage(args);
        }

        public async Task PostMessageAsync(string token, string text, string username = null, string channel = null)
        {
            Arguments args = new Arguments()
            {
                Token = token,
                Channel = channel,
                Username = username,
                Text = text
            };

            await PostMessageAsync(args);
        }

        private string ToQueryString(Object p)
        {
            List<string> properties = new List<string>();

            foreach (System.Reflection.PropertyInfo propertyInfo in p.GetType().GetProperties())
            {
                if (propertyInfo.CanRead)
                {
                    string JsonProperty = propertyInfo.GetCustomAttributes(true).Where(x => x.GetType() == typeof(JsonPropertyAttribute)).Select(x => ((JsonPropertyAttribute)x).PropertyName).FirstOrDefault();
                    if (propertyInfo.PropertyType == typeof(ObservableCollection<Attachment>))
                    {
                        if (propertyInfo.GetValue(p, null) != null)
                        {
                            properties.Add(string.Format("{0}={1}", JsonProperty != null ? JsonProperty : propertyInfo.Name, Uri.EscapeUriString(JsonConvert.SerializeObject(propertyInfo.GetValue(p, null)))));
                        }
                    }
                    else
                    {
                        if (propertyInfo.GetValue(p, null) != null)
                        {
                            properties.Add(string.Format("{0}={1}", JsonProperty != null ? JsonProperty : propertyInfo.Name, Uri.EscapeUriString(propertyInfo.GetValue(p, null).ToString())));
                        }
                    }

                }
            }

            return string.Join("&", properties.ToArray());
        }

        public NameValueCollection ToQueryNVC(Object p)
        {

            NameValueCollection nvc = new NameValueCollection();

            foreach (System.Reflection.PropertyInfo propertyInfo in p.GetType().GetProperties())
            {
                if (propertyInfo.CanRead)
                {
                    string JsonProperty = propertyInfo.GetCustomAttributes(true).Where(x => x.GetType() == typeof(JsonPropertyAttribute)).Select(x => ((JsonPropertyAttribute)x).PropertyName).FirstOrDefault();
                    if (propertyInfo.PropertyType == typeof(ObservableCollection<Attachment>))
                    {
                        if (propertyInfo.GetValue(p, null) != null)
                        {
                            nvc[JsonProperty != null ? JsonProperty : propertyInfo.Name] = JsonConvert.SerializeObject(propertyInfo.GetValue(p, null));
                        }
                    }
                    else
                    {
                        if (propertyInfo.GetValue(p, null) != null)
                        {
                            nvc[JsonProperty != null ? JsonProperty : propertyInfo.Name] = propertyInfo.GetValue(p, null).ToString();
                        }
                    }

                }
            }

            return nvc;

        }

        //Post a message using args object
        public Response PostMessage(Arguments args)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Ssl3 | SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;

            //string payloadJson = JsonConvert.SerializeObject(payload);
            using (WebClient client = new WebClient())
            {
                NameValueCollection data = ToQueryNVC(args);
                var response = client.UploadValues(_uri, "POST", data);

                string responseText = _encoding.GetString(response);

                return JsonConvert.DeserializeObject<Response>(responseText);
            }
        }

        //Post a message using args object
        public async Task<Response> PostMessageAsync(Arguments args)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            //SecurityProtocolType.Ssl3 | SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;

            //string payloadJson = JsonConvert.SerializeObject(payload);
            using (WebClient client = new WebClient())
            {
                NameValueCollection data = ToQueryNVC(args);
                var response = await client.UploadValuesTaskAsync(_uri, "POST", data);

                string responseText = _encoding.GetString(response);

                return JsonConvert.DeserializeObject<Response>(responseText);
            }
        }

    }

    public class Arguments
    {
        public Arguments()
        {
            Attachments = new ObservableCollection<Attachment>();
            Parse = "full";
        }
        [JsonProperty("channel")]
        public string Channel { get; set; }
        [JsonProperty("username")]
        public string Username { get; set; }
        [JsonProperty("text")]
        public string Text { get; set; }
        [JsonProperty("token")]
        public string Token { get; set; }
        [JsonProperty("parse")]
        public string Parse { get; set; }
        [JsonProperty("link_names")]
        public string LinkNames { get; set; }
        [JsonProperty("unfurl_links")]
        public string UnfurlLinks { get; set; }
        [JsonProperty("unfurl_media")]
        public string UnfurlMedia { get; set; }
        [JsonProperty("icon_url")]
        public string IconUrl { get; set; }
        [JsonProperty("icon_emoji")]
        public string IconEmoji { get; set; }
        [JsonProperty("attachments")]
        public ObservableCollection<Attachment> Attachments { get; set; }
    }

    public class Attachment
    {
        public Attachment()
        {
            Fields = new ObservableCollection<AttachmentFields>();
        }
        [JsonProperty("fallback")]
        public string Fallback { get; set; }
        [JsonProperty("text")]
        public string Text { get; set; }
        [JsonProperty("pretext")]
        public string Pretext { get; set; }
        [JsonProperty("color")]
        public string Color { get; set; }
        [JsonProperty("fields")]
        public ObservableCollection<AttachmentFields> Fields { get; set; }
    }

    public class AttachmentFields
    {
        [JsonProperty("title")]
        public string Title { get; set; }
        [JsonProperty("value")]
        public string Value { get; set; }
        [JsonProperty("short")]
        public bool Short { get; set; }
    }

    public class Response
    {
        [JsonProperty("ok")]
        public bool Ok { get; set; }
        [JsonProperty("channel")]
        public string Channel { get; set; }
        [JsonProperty("ts")]
        public string TimeStamp { get; set; }
        [JsonProperty("error")]
        public string Error { get; set; }
    }

}
