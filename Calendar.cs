using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Discord;
using Discord.Commands;
using Discord.WebSocket;

using Google.Apis.Auth.OAuth2;
using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;
using Google.Apis.Services;
using Google.Apis.Util.Store;

using Microsoft.Extensions.DependencyInjection;

//using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// https://docs.microsoft.com/en-us/dotnet/standard/base-types/custom-date-and-time-format-strings //

namespace Pandorum
{
    public class Calendar
    {
        public List<Event> IncomingEvents { get; private set; } = new List<Event>();
        public bool IncomingEventsCached { get; private set; } = false;

        private const string AppName = "Pandorum";

        private readonly CalendarService  GoogleCalendar;

        private CancellationTokenSource WorkerToken;

        public Calendar(Cache cache, DiscordSocketClient discord)
        {
            string directory = $"{Directory.GetCurrentDirectory()}/Config/Google";

            if(!File.Exists($"{directory}/Credentials.json"))
            {
                Pandorum.Log(LogSeverity.Error, nameof(Calendar), "Service not started -- Config/Google/Credentials.json not found");
                return;
            }

            using(var stream = new FileStream($"{directory}/Credentials.json", FileMode.Open, FileAccess.Read))
            {
                // If not set to absolute path, %AppData%/Roaming/ is used
                GoogleWebAuthorizationBroker.Folder = directory;

                var credentials = GoogleWebAuthorizationBroker.AuthorizeAsync(
                    GoogleClientSecrets.Load(stream).Secrets,
                    new[]{ CalendarService.Scope.Calendar },
                    "user",
                    CancellationToken.None
                ).Result;

                GoogleCalendar = new CalendarService(new BaseClientService.Initializer()
                {
                    HttpClientInitializer = credentials,
                    ApplicationName = AppName,
                });
            }

            discord.Connected += OnDiscordConnected;
            discord.Disconnected += OnDiscordDisconnected;

            if(cache["Calendar"] == null)
                cache["Calendar"] = new JObject();
        }

        private async Task Worker(CancellationToken token)
        {
            await Task.Delay(1000 - DateTime.Now.Millisecond);

            while(!token.IsCancellationRequested)
            {
                if(DateTime.Now.Second == 0)
                {
                    Pandorum.Log(LogSeverity.Debug, nameof(Calendar), "Refresh events cache...");
                    RefreshCalendars();
                    Pandorum.Log(LogSeverity.Debug, nameof(Calendar), $"Refresh events cache... {IncomingEvents.Count} event{(IncomingEvents.Count != 1 ? "s" : "")}");

                    var debugChannelId = Pandorum.Configuration.Calendar.DebugChannel;
                    var debugChannel = Pandorum.Discord.GetChannel(debugChannelId) as ISocketMessageChannel;

                    foreach(Event e in IncomingEvents)
                    {
                        // UTC > YourSillyTimeZone
                        DateTime utcNow = DateTime.UtcNow;//DateTime.SpecifyKind(DateTime.Now, DateTimeKind.Utc);
                        DateTime utcStart = ((DateTime)e.Start.DateTime).ToUniversalTime();//DateTime.SpecifyKind((DateTime)e.Start.DateTime, DateTimeKind.Utc);

                        int minutesLeft = (int)Math.Ceiling((utcStart - utcNow).TotalMinutes);
                        string soon;

                        // Shotgun Debug (TM)
                        // Pandorum.Log(LogSeverity.Debug, nameof(Calendar), $"[{e.Id}] T-{minutesLeft}m : {e.Summary}");
                        // Pandorum.Log(LogSeverity.Debug, nameof(Calendar), $"[{e.Id}] T-{minutesLeft / 60}h : {e.Summary}");
                        // Pandorum.Log(LogSeverity.Debug, nameof(Calendar), $"[{e.Id}] {utcStart.ToString("dd.MM HH:mm")}");

                        if(minutesLeft <= 60) // It's happening!
                        {
                            if(minutesLeft == 60 || minutesLeft == 30 || minutesLeft == 15 || minutesLeft == 5)
                            {
                                soon = $"T-{minutesLeft}minutes";
                                Console.WriteLine($"{soon} : {e.Summary}");
                                await debugChannel?.SendMessageAsync(soon, embed: GetEventAsEmbed(e));
                            }
                            else if(minutesLeft == 0)
                            {
                                soon = $"**ON YOUR FEET MAGGOTS**";
                                Pandorum.Log(LogSeverity.Verbose, nameof(Calendar), $"{soon} : {e.Summary}");
                                await debugChannel?.SendMessageAsync(soon, embed: GetEventAsEmbed(e));
                            }
                        }
                        else
                        {
                            if(minutesLeft % 60 != 0) // Synch to full hour
                                continue;

                            int hoursLeft = minutesLeft / 60;

                            if(hoursLeft % 24 == 0) // Synch to full day
                            {
                                int daysLeft = hoursLeft / 24;

                                soon = $"T-{daysLeft}day{(daysLeft != 1 ? "s" : "")}";
                                Pandorum.Log(LogSeverity.Verbose, nameof(Calendar), $"{soon} : {e.Summary}");

                                if(daysLeft <= 7)
                                    await debugChannel?.SendMessageAsync(soon, embed: GetEventAsEmbed(e));
                            }
                            else if(hoursLeft <= 12 && hoursLeft % 3 == 0)
                            {
                                soon = $"T-{hoursLeft}hours";
                                Pandorum.Log(LogSeverity.Verbose, nameof(Calendar), $"{soon} : {e.Summary}");
                                await debugChannel?.SendMessageAsync(soon, embed: GetEventAsEmbed(e));
                            }
                        }
                    }
                }

                await Task.Delay(1000 - DateTime.Now.Millisecond);
            }
        }

        public void StartWorker()
        {
            if(WorkerToken == null)
            {
                Pandorum.Log(LogSeverity.Info, nameof(Calendar), "Starting worker");
                WorkerToken = new CancellationTokenSource();
                _ = Worker(WorkerToken.Token);
            }
        }

        public void StopWorker()
        {
            if(WorkerToken != null)
            {
                Pandorum.Log(LogSeverity.Info, nameof(Calendar), "Stopping worker");
                WorkerToken.Cancel();
                WorkerToken.Dispose();
                WorkerToken = null;
            }
        }

        public void RefreshCalendars()
        {
            CalendarListResource.ListRequest request = GoogleCalendar.CalendarList.List();
            CalendarList calendars = request.Execute();

            List<Event> events = new List<Event>();
            if(calendars.Items != null && calendars.Items.Count > 0)
            {
                foreach(var calendar in calendars.Items)
                {
                    if(!calendar.Summary.ToLower().StartsWith("pandorum"))
                        continue;

                    RefreshCalendarEvents(calendar.Id, ref events);
                }
            }

            IncomingEvents.Clear();
            IncomingEvents = events.OrderBy(e => e.Start.DateTime).ToList();
            IncomingEventsCached = true;
        }

        public void RefreshCalendarEvents(string calendarId, ref List<Event> events)
        {
            EventsResource.ListRequest request = GoogleCalendar.Events.List(calendarId);
            request.TimeMin = DateTime.Now.ToUniversalTime();
            request.TimeMax = DateTime.MaxValue.ToUniversalTime();
            request.ShowDeleted = false;
            request.SingleEvents = false;
            request.MaxResults = 10;
            //request.OrderBy = EventsResource.ListRequest.OrderByEnum.StartTime;

            Events eventsList = request.Execute();
            if (eventsList.Items != null && eventsList.Items.Count > 0)
            {
                foreach(var eventItem in eventsList.Items)
                {
                    events.Add(eventItem);
                }
            }
        }

        public Event InsertEvent(string calendarId, string summary, string description, DateTime dateTime, DateTime endDateTime)
        {
            var calendarEvent = new Event();

            EventDateTime eventStart = new EventDateTime();
            eventStart.DateTime = dateTime;
            eventStart.TimeZone = "UTC";

            EventDateTime eventEnd = new EventDateTime();
            eventEnd.DateTime = endDateTime;
            eventEnd.TimeZone = "UTC";

            calendarEvent.Start = eventStart;
            calendarEvent.End = eventEnd;
            calendarEvent.Summary = summary;

            if(!string.IsNullOrEmpty(description))
                calendarEvent.Description = description;

            Event result = GoogleCalendar.Events.Insert(calendarEvent, calendarId).Execute();
            IncomingEventsCached = false;

            return result;
        }

        public void DeleteEvent(string calendarId, string eventId)
        {
            GoogleCalendar.Events.Delete(calendarId, eventId).Execute();
            IncomingEventsCached = false;
        }

        public Embed GetEventAsEmbed(Event e, bool details = false)
        {
            const string format = "dd.MM.yyyy HH:mm";
            //TimeZoneInfo tzEST = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

            EmbedBuilder embed = new EmbedBuilder();;

            embed.WithTitle(e.Summary);
            if(!string.IsNullOrEmpty(e.Description))
                embed.WithDescription(e.Description);

            EmbedFooterBuilder footer = new EmbedFooterBuilder();;

            DateTime utc = ((DateTime)e.Start.DateTime).ToUniversalTime();
            //DateTime est = TimeZoneInfo.ConvertTimeFromUtc(utc, tzEST);

            if(!string.IsNullOrEmpty(e.Start.TimeZone))
                footer.WithText($"{utc.ToString(format)} UTC");// / {est.ToString(format)} EST");
            else
                footer.WithText($"{utc.ToString(format)} (UTC?)");

            if(details)
            {
                footer.Text += $"\nID {e.Id}";
            }

            embed.WithFooter(footer);

            return embed.Build();
        }

        //

        public Task OnDiscordConnected()
        {
            StartWorker();

            return Task.CompletedTask;
        }

        public Task OnDiscordDisconnected(Exception e)
        {

            StopWorker();

            return Task.CompletedTask;
        }
    }

    [Group("calendar")]
    public class CommandsCalendar : ModuleBase<SocketCommandContext>
    {
        [RequireMaintainer]
        [Command("add")]
        public async Task CmdCalendarAdd(string date, string time, string timezone, string summary, string description = "")
        {
            string[] dateFormat = { "d.M.yyyy", "d.MM.yyyy", "dd.M.yyyy", "dd.MM.yyyy" };
            string[] timeFormat = { "H:mm", "HH:mm" };

            // Extract values during date/time validation

            int day, month, year, hour, minute;

            if(DateTime.TryParseExact(date, dateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var tempDate))
            {
                day = tempDate.Day;
                month = tempDate.Month;
                year = tempDate.Year;
            }
            else
            {
                await ReplyAsync("Invalid date");
                return;
            }

            if(DateTime.TryParseExact(time, timeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var tempTime))
            {
                hour = tempTime.Hour;
                minute = tempTime.Minute;
            }
            else
            {
                await ReplyAsync("Invalid time");
                return;
            }

            if(timezone.ToLower() != "utc")
            {
                // This argument exists only to remind humans that time has to be UTC

                await ReplyAsync("Invalid timezone");
                return;
            }

            DateTime start = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Utc);

            // Make sure event doesn't start in past

            if(DateTime.UtcNow > start)
            {
                await ReplyAsync($"Invalid date/time -- expired {(start - DateTime.UtcNow).TotalMinutes}");
                return;
            }

            // Text validation

            if(string.IsNullOrEmpty(summary))
            {
                await ReplyAsync("Invalid summary");
                return;
            }

            if(string.IsNullOrEmpty(Pandorum.Configuration.Calendar.Id))
            {
                Pandorum.Log(LogSeverity.Warning, nameof(Commands), "Missing calendar Id");
                return;
            }

            await ReplyAsync($"-> {start.ToString("F", CultureInfo.InvariantCulture)} {start.Kind.ToString()}");

            Event result = Pandorum.Calendar.InsertEvent(Pandorum.Configuration.Calendar.Id, summary, description, start, start);
            Pandorum.Log(LogSeverity.Info, nameof(Calendar), $"Event created: {result.HtmlLink}");
        }

        [RequireMaintainer]
        [Command("delete")]
        public async Task CmdCalendarDelete(string eventId)
        {
            if(string.IsNullOrEmpty(eventId))
            {
                await ReplyAsync("Invalid id");
                return;
            }

            Pandorum.Calendar.DeleteEvent(Pandorum.Configuration.Calendar.Id, eventId);
        }

        [RequireMaintainer]
        [Command("show")]
        public async Task CmdCalendarShow(string option = "")
        {
            if(!Pandorum.Calendar.IncomingEventsCached)
                Pandorum.Calendar.RefreshCalendars();

            if(Pandorum.Calendar.IncomingEvents.Any())
            {
                foreach(var e in Pandorum.Calendar.IncomingEvents)
                {
                    await ReplyAsync(embed: Pandorum.Calendar.GetEventAsEmbed(e, option == "details"));
                }
            }
            else
                await ReplyAsync("No events found");
        }
    }
}
