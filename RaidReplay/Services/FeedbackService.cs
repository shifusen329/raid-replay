using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RaidReplay.Services;

/// <summary>
/// Delivers feedback reports (JSON built by <see cref="Core.Feedback.FeedbackReport"/>) to the Raid Replay feedback
/// endpoint. Nothing is sent unless the user presses Send. A report that can't get through (offline, server down, rate
/// limited) is saved in the plugin's config folder and retried later; one the server rejects is dropped.
/// </summary>
public sealed class FeedbackService : IDisposable
{
    public const string Endpoint = "https://supabase.gofigstudios.com/rr/feedback";

    private const int MaxQueued = 20;
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(14);

    private readonly HttpClient http;
    private readonly string queueDir;
    private readonly SemaphoreSlim retrying = new(1, 1);
    private readonly CancellationTokenSource cts = new();

    public FeedbackService(string configDir, string version)
    {
        queueDir = Path.Combine(configDir, "feedback-queue");
        http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"RaidReplay/{version}");
    }

    public enum Result
    {
        Sent,
        Queued,
        Rejected,
    }

    /// <summary>Reports waiting to be retried.</summary>
    public int QueuedCount => Directory.Exists(queueDir) ? Directory.GetFiles(queueDir, "*.json").Length : 0;

    /// <summary>Sends one report; saves it for a later retry when the server can't be reached.</summary>
    public async Task<(Result Result, string Message)> SendAsync(string json)
    {
        var (outcome, message) = await Post(json).ConfigureAwait(false);
        switch (outcome)
        {
            case Outcome.Sent:
                _ = RetryQueuedAsync();
                return (Result.Sent, string.Empty);
            case Outcome.Transient:
                Save(json);
                return (Result.Queued, message);
            default:
                return (Result.Rejected, message);
        }
    }

    /// <summary>Delivers saved reports, oldest first, stopping at the first one that still can't get through.</summary>
    public async Task RetryQueuedAsync()
    {
        if (!Directory.Exists(queueDir) || !await retrying.WaitAsync(0).ConfigureAwait(false))
            return;
        try
        {
            foreach (var file in Directory.GetFiles(queueDir, "*.json").Order(StringComparer.Ordinal))
            {
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow - MaxAge)
                {
                    File.Delete(file);
                    continue;
                }

                var (outcome, message) = await Post(await File.ReadAllTextAsync(file, cts.Token).ConfigureAwait(false)).ConfigureAwait(false);
                if (outcome == Outcome.Transient)
                    break;
                if (outcome == Outcome.Rejected)
                    Plugin.Log.Warning($"A saved feedback report was rejected and dropped: {message}");
                File.Delete(file);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Plugin.Log.Warning(e, "Retrying saved feedback reports failed");
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            retrying.Release();
        }
    }

    private enum Outcome
    {
        Sent,
        Transient,
        Rejected,
    }

    private async Task<(Outcome, string)> Post(string json)
    {
        try
        {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var res = await http.PostAsync(Endpoint, content, cts.Token).ConfigureAwait(false);
            if (res.IsSuccessStatusCode)
                return (Outcome.Sent, string.Empty);
            var code = (int)res.StatusCode;
            var text = (await res.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false)).Trim();
            var message = $"{code} {res.ReasonPhrase}" + (text.Length > 0 ? $": {text[..Math.Min(text.Length, 300)]}" : string.Empty);
            return code is 408 or 429 or >= 500 ? (Outcome.Transient, message) : (Outcome.Rejected, message);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            return (Outcome.Transient, e.Message);
        }
    }

    private void Save(string json)
    {
        try
        {
            Directory.CreateDirectory(queueDir);
            var files = Directory.GetFiles(queueDir, "*.json").Order(StringComparer.Ordinal).ToList();
            foreach (var old in files.Take(Math.Max(0, files.Count - MaxQueued + 1)))
                File.Delete(old);
            File.WriteAllText(Path.Combine(queueDir, $"{DateTime.UtcNow.Ticks:D20}.json"), json);
        }
        catch (Exception e)
        {
            Plugin.Log.Warning(e, "Saving a feedback report for later failed");
        }
    }

    public void Dispose()
    {
        cts.Cancel();
        http.Dispose();
        cts.Dispose();
    }
}
