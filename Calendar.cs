using System;
using System.Collections.Generic;
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

namespace Pandorum
{
    public class Calendar
    {
        public List<Event> IncomingEvents { get; private set; } = new List<Event>();
        public bool IncomingEventsCached { get; private set; } = false;

        private const string AppName = "Pandorum";

        private readonly CalendarService  GoogleCalendar;

        private CancellationTokenSource WorkerToken;

        public Calendar()
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

            var discord = Pandorum.Services.GetRequiredService<DiscordSocketClient>();
            discord.Connected += OnDiscordConnected;
            discord.Disconnected += OnDiscordDisconnected;
        }

        private async Task Worker(CancellationToken token)
        {
            await Task.Delay(1000 - DateTime.Now.Millisecond);
            Pandorum.Log(LogSeverity.Verbose, nameof(Calendar), $"Worker spin @ {DateTime.Now.ToString()}");

            while(!token.IsCancellationRequested)
            {
                if(DateTime.Now.Second == 0)
                {
                    Pandorum.Log(LogSeverity.Verbose, nameof(Calendar), "Refresh cached events");
                    RefreshCalendars();

                    var debugChannelId = Pandorum.Services.GetRequiredService<Configuration>().Calendar.DebugChannel;
                    var debugChannel = Pandorum.Services.GetRequiredService<DiscordSocketClient>().GetChannel(debugChannelId) as ISocketMessageChannel;

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
            request.TimeMin = DateTime.Now;
            request.ShowDeleted = false;
            request.SingleEvents = true;
            request.MaxResults = 10;
            request.OrderBy = EventsResource.ListRequest.OrderByEnum.StartTime;

            Events eventsList = request.Execute();
            if (eventsList.Items != null && eventsList.Items.Count > 0)
            {
                foreach(var eventItem in eventsList.Items)
                {
                    events.Add(eventItem);
                }
            }
        }

        public void AddEvent(string calendarId, string summary, string description, DateTime dateTime, DateTime endDateTime)
        {
            var calendarEvent = new Event();

            EventDateTime eventStart = new EventDateTime();
            eventStart.DateTime = dateTime;

            EventDateTime eventEnd = new EventDateTime();
            eventEnd.DateTime = endDateTime;

            calendarEvent.Start = eventStart;
            calendarEvent.End = eventEnd;
            calendarEvent.Summary = summary;
            calendarEvent.Description = description;

            Event result = GoogleCalendar.Events.Insert(calendarEvent, calendarId).Execute();
            Pandorum.Log(LogSeverity.Info, nameof(Calendar), $"Event created: {result.HtmlLink}");
        }

        public Embed GetEventAsEmbed(Event e)
        {
            EmbedBuilder embed = new EmbedBuilder();;
            EmbedFooterBuilder footer = new EmbedFooterBuilder();;

            const string format = "dd.MM HH:mm";
            TimeZoneInfo tzEST = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

            embed.WithTitle(e.Summary);
            if(!string.IsNullOrEmpty(e.Description))
                embed.WithDescription(e.Description);

            DateTime utc = ((DateTime)e.Start.DateTime).ToUniversalTime();
            DateTime est = TimeZoneInfo.ConvertTimeFromUtc(utc, tzEST);

            if(!string.IsNullOrEmpty(e.Start.TimeZone))
                footer.WithText($"{utc.ToString(format)} UTC");// / {est.ToString(format)} EST");
            else
                footer.WithText($"{utc.ToString(format)} (timezone not set)");


            embed.WithFooter(footer);

            return embed.Build();
        }
    }

    [Group("calendar")]
    public class CommandsCalendar : ModuleBase<SocketCommandContext>
    {
        [RequireMaintainer]
        [Command("add")]
        public /*async*/ Task CmdCalendarAdd(string date, string time, string timezone, string summary, string description = "")
        {
            ReplyAsync("Calendar Add");

            return Task.CompletedTask;
        }

        [RequireMaintainer]
        [Command("show")]
        public async Task CmdCalendarShow()
        {
            var calendar = Pandorum.Services.GetRequiredService<Calendar>();

            // Happen only when using !calendar right after start
            if(!calendar.IncomingEventsCached)
                calendar.RefreshCalendars();

            if(calendar.IncomingEvents.Any())
            {
                foreach(var e in calendar.IncomingEvents)
                {
                    await ReplyAsync(embed: calendar.GetEventAsEmbed(e));
                }
            }
            else
                await ReplyAsync("No events found");
        }
    }
}
