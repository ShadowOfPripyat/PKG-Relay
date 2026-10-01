using System.Globalization;
using System.Text.Json;

namespace PkgRelay.Services;

internal static class RpiInstallRecovery
{
    internal const int TaskDuplicated = unchecked((int)0x80990015);

    // Keep every operation on the endpoint which rejected registration. A
    // failed task can retain a URL from an earlier PC address or server route.
    internal static async Task<JsonDocument> PostInstallAsync(
        Func<string, object, CancellationToken, Task<JsonDocument>> post,
        object payload,
        string contentId,
        int subType,
        Func<string, Task> log,
        CancellationToken cancellationToken)
    {
        try
        {
            return await post("install", payload, cancellationToken);
        }
        catch (RpiException ex) when (ex.ErrorResult == TaskDuplicated &&
                                      !string.IsNullOrWhiteSpace(contentId))
        {
            try
            {
                using var found = await post("find_task",
                    new { content_id = contentId, sub_type = subType }, cancellationToken);
                if (!found.RootElement.TryGetProperty("task_id", out var taskValue) ||
                    ReadInteger(taskValue) is not { } taskId || taskId < 0)
                    throw new RpiException("A duplicate PS4 download exists, but its task ID could not be confirmed.", ex);

                using var progress = await post("get_task_progress",
                    new { task_id = taskId }, cancellationToken);
                // Missing/malformed error data is not evidence that deleting a
                // task is safe. Do not cancel a healthy, paused or completed task.
                var error = ReadError(progress.RootElement);
                if (error is null or 0)
                    throw new RpiException(
                        $"PS4 task {taskId} already exists for this package and is not confirmed failed. " +
                        "Manage that download on the PS4 before sending another copy.", ex);

                cancellationToken.ThrowIfCancellationRequested();
                await log($"Removing failed duplicate BGFT task {taskId} for {contentId} " +
                          $"(error 0x{error.Value:X8}) before retrying registration.");
                using var removed = await post("unregister_task",
                    new { task_id = taskId }, cancellationToken);
                if (!IsSuccessResponse(removed.RootElement))
                    throw new RpiException("The PS4 did not confirm removal of the failed duplicate task.");
            }
            catch (Exception recoveryError) when (!cancellationToken.IsCancellationRequested)
            {
                // A timeout during lookup/removal is not an ambiguous install.
                // Do not let callers attach to the stale task as if it were new.
                throw new RpiException($"Duplicate PS4 task recovery failed: {recoveryError.Message}", recoveryError);
            }
        }

        // Exactly one retry, using the current URLs. A second failure must be
        // reported rather than repeatedly deleting tasks or switching services.
        return await post("install", payload, cancellationToken);
    }

    internal static bool IsErrorResponse(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty("status", out var status) &&
        (status.ValueKind == JsonValueKind.False ||
         (status.ValueKind == JsonValueKind.String &&
          string.Equals(status.GetString(), "error", StringComparison.OrdinalIgnoreCase)));

    private static bool IsSuccessResponse(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty("status", out var status) &&
        (status.ValueKind == JsonValueKind.True ||
         (status.ValueKind == JsonValueKind.String &&
          (string.Equals(status.GetString(), "success", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(status.GetString(), "ok", StringComparison.OrdinalIgnoreCase))));

    internal static int? ReadError(string json)
    {
        try
        {
            using var document = RpiJson.Parse(json);
            return ReadError(document.RootElement);
        }
        catch (JsonException) { return null; }
    }

    internal static int? ReadError(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var name in new[] { "error_result", "error", "error_code" })
            if (root.TryGetProperty(name, out var value) && ReadInteger(value) is { } error)
                return error;
        return null;
    }

    private static int? ReadInteger(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) &&
            number >= int.MinValue && number <= uint.MaxValue)
            return unchecked((int)number);
        if (value.ValueKind != JsonValueKind.String)
            return null;
        var text = value.GetString()?.Trim();
        if (text is null)
            return null;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            uint.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
            return unchecked((int)hex);
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out number) &&
               number >= int.MinValue && number <= uint.MaxValue ? unchecked((int)number) : null;
    }
}
