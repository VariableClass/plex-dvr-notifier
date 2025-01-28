using System.Net;
using System.Xml.Serialization;
using VariableClass.PlexDvrNotifier.Models;

const int DefaultPollIntervalMs = 300000;
const int MinimumPollIntervalMs = 1000;

const int DefaultErrorNotifyCooldownHours = 24;
const int MinimumErrorNotifyCooldownHours = 1;

const int DefaultRecordingsTimeToLiveHours = 24;
const int MinimumRecordingsTimeToLiveHours = 1;


const string PollEndpoint = "/activities";
const string TokenHeaderName = "X-Plex-Token";

const string NtfyHeaderTitle = "Title";
const string NtfyHeaderPriority = "Priority";
const string NtfyPriorityUrgent = "urgent";
const string NtfyTagUp = "green_circle";
const string NtfyTagWarning = "warning";
const string NtfyTagRecording = "red_circle";
const string NtfyHeaderTags = "Tags";
const string NtfyHeaderActions = "Actions";

const string NtfyTitleUp = "Plex DVR Notifier running";
const string NtfyTitlePollingError = "Plex polling error";
const string NtfyTitleProcessingError = "An error occurred when processing the response from Plex";

const string NtfyMessageRecording = "Recording started";

var plexServer = Environment.GetEnvironmentVariable("PLEX_SERVER_URL");
var plexToken = Environment.GetEnvironmentVariable("PLEX_TOKEN");

var ntfyServer = Environment.GetEnvironmentVariable("NTFY_SERVER_URL");
var ntfyTopicError = Environment.GetEnvironmentVariable("NTFY_TOPIC_ERROR");
var ntfyTopicRecording = Environment.GetEnvironmentVariable("NTFY_TOPIC_RECORDING");

var userProvidedPollInterval =
	int.TryParse(
		Environment.GetEnvironmentVariable("POLL_INTERVAL_MS"), out var desiredPollIntervalMs);

var pollIntervalMs =
	!userProvidedPollInterval || desiredPollIntervalMs < MinimumPollIntervalMs
	? DefaultPollIntervalMs
	: desiredPollIntervalMs;

var userProvidedErrorNotifyCooldown =
	int.TryParse(
		Environment.GetEnvironmentVariable("ERROR_NOTIFY_COOLDOWN_HOURS"), out var desiredErrorNotifyCooldownHours);

var errorNotifyCooldownHours =
	!userProvidedErrorNotifyCooldown || desiredErrorNotifyCooldownHours < MinimumErrorNotifyCooldownHours
	? DefaultErrorNotifyCooldownHours
	: desiredErrorNotifyCooldownHours;

var userProvidedRecordingsTtl =
	int.TryParse(
		Environment.GetEnvironmentVariable("RECORDINGS_TTL_HOURS"), out var desiredRecordingsTtlHours);

var knownRecordingsTtlHrs =
	!userProvidedRecordingsTtl || desiredRecordingsTtlHours < MinimumRecordingsTimeToLiveHours
	? DefaultRecordingsTimeToLiveHours
	: desiredRecordingsTtlHours;

var knownRecordings = new Dictionary<string, DateTimeOffset>();
var devLastNotified = DateTimeOffset.UtcNow.AddHours(-errorNotifyCooldownHours);

var httpClient = new HttpClient();

var upNotifyRequest = new HttpRequestMessage(HttpMethod.Post, $"{ntfyServer}/{ntfyTopicError}");
upNotifyRequest.Headers.Add(NtfyHeaderTitle, string.Format(NtfyTitleUp));
upNotifyRequest.Headers.Add(NtfyHeaderTags, NtfyTagUp);
await httpClient.SendAsync(upNotifyRequest);

Console.WriteLine("Plex DVR Notifier is up and running");

// I know, I know. I'll refactor this away tomorrow, I promise, but it's quite late and I want to go to bed
var hasRunAtLeastOnce = false;

do
{
	if (hasRunAtLeastOnce)
	{
		Thread.Sleep(pollIntervalMs);
	}
	
	hasRunAtLeastOnce = true;

	// Remove expired recordings
	knownRecordings =
		knownRecordings
		.Where(x => x.Value < DateTimeOffset.UtcNow.AddHours(-knownRecordingsTtlHrs))
		.ToDictionary(x => x.Key, x => x.Value);
	
	PlexActivitiesResponse? plexResponse = null;

	try
	{
		var pollRequest =  new HttpRequestMessage(HttpMethod.Get, $"{plexServer}{PollEndpoint}");
		pollRequest.Headers.Add(TokenHeaderName, plexToken);

		Console.WriteLine("Polling Plex");
		var response = await httpClient.SendAsync(pollRequest);
		var responseStream = await response.Content.ReadAsStreamAsync();
		var reader = new StreamReader(responseStream);
		plexResponse = new XmlSerializer(typeof(PlexActivitiesResponse)).Deserialize(reader) as PlexActivitiesResponse;
		
		Console.WriteLine("Activities recieved");
	}
	catch(Exception e)
	{
		if (devLastNotified > DateTimeOffset.UtcNow.AddHours(-errorNotifyCooldownHours))
		{
			continue;
		}

		Console.WriteLine("Polling failed, notifying developer");
		
		var errorNotifyRequest = new HttpRequestMessage(HttpMethod.Post, $"{ntfyServer}/{ntfyTopicError}")
		{
			Content = new StringContent(string.Format(e.Message))
		};

		errorNotifyRequest.Headers.Add(NtfyHeaderTitle, string.Format(NtfyTitlePollingError));
		errorNotifyRequest.Headers.Add(NtfyHeaderTags, NtfyTagWarning);
		errorNotifyRequest.Headers.Add(NtfyHeaderPriority, NtfyPriorityUrgent);
		errorNotifyRequest.Headers.Add(NtfyHeaderActions, $"http, Open Plex, {plexServer}, clear=true");
		
		await httpClient.SendAsync(errorNotifyRequest);
		devLastNotified = DateTimeOffset.UtcNow;

		Console.WriteLine("Dev notified");

		continue;
	}

	try
	{
		if (!plexResponse.Activities.Any(x => x.IsRecording() || knownRecordings.ContainsKey(x.Subtitle)))
		{
			Console.WriteLine("No new recordings, continuing");
		}

		foreach(var activity in plexResponse.Activities)
		{
			if (!activity.IsRecording())
			{
				continue;
			}

			if (knownRecordings.ContainsKey(activity.Subtitle))
			{
				continue;
			}

			Console.WriteLine($"New recording found for {activity.Subtitle}, sending notification");

			knownRecordings.Add(activity.Subtitle, DateTimeOffset.UtcNow);
			
			// Notify
			var recordingNotifyRequest = new HttpRequestMessage(HttpMethod.Post, $"{ntfyServer}/{ntfyTopicRecording}")
			{
				Content = new StringContent(string.Format(NtfyMessageRecording))
			};

			recordingNotifyRequest.Headers.Add(NtfyHeaderTitle, activity.Subtitle);
			recordingNotifyRequest.Headers.Add(NtfyHeaderTags, NtfyTagRecording);
			
			await httpClient.SendAsync(recordingNotifyRequest);

			Console.WriteLine("Notification sent");
		}
	}
	catch (Exception e)
	{
		if (devLastNotified > DateTimeOffset.UtcNow.AddHours(-errorNotifyCooldownHours))
		{
			continue;
		}

		Console.WriteLine("Processing failed, notifying developer");
		var errorNotifyRequest = new HttpRequestMessage(HttpMethod.Post, $"{ntfyServer}/{ntfyTopicError}")
		{
			Content = new StringContent(string.Format(e.Message))
		};

		errorNotifyRequest.Headers.Add(NtfyHeaderTitle, string.Format(NtfyTitleProcessingError));
		errorNotifyRequest.Headers.Add(NtfyHeaderTags, NtfyTagWarning);
		errorNotifyRequest.Headers.Add(NtfyHeaderPriority, NtfyPriorityUrgent);
		errorNotifyRequest.Headers.Add(NtfyHeaderActions, $"http, Open Plex, {plexServer}, clear=true");
		
		await httpClient.SendAsync(errorNotifyRequest);
		devLastNotified = DateTimeOffset.UtcNow;

		Console.WriteLine("Dev notified");
	}
}
while (true);
