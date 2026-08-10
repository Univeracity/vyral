using System.Text.Json.Nodes;

namespace Vyral.Providers.Jules;

internal static class JulesLifecycleNormalizer
{
    public static JsonObject BuildSummary(JsonObject output, string operation)
    {
        var normalizedOperation = NormalizeOperation(operation);
        var authoritativeSessionState = IsAuthoritativeSessionState(normalizedOperation);
        var summary = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["operation"] = operation,
            ["stateSource"] = GetStateSource(normalizedOperation),
            ["authoritativeSessionState"] = authoritativeSessionState,
            ["sourceOfTruthOperation"] = "getSession"
        };

        var rawSessionId = FindFirstString(output, "name", "sessionId", "session_id", "id");
        var hasSessionId = !string.IsNullOrWhiteSpace(rawSessionId);
        if (!string.IsNullOrWhiteSpace(rawSessionId))
        {
            summary["sessionId"] = NormalizeSessionId(rawSessionId);
        }

        var providerState = FindFirstString(output, "lifecycleState", "lifecycle_state", "sessionState", "session_state", "state", "status", "phase");
        if (!string.IsNullOrWhiteSpace(providerState))
        {
            summary["providerState"] = providerState;
        }

        var allPendingQuestions = FindPendingQuestions(output)
            .Where(question => !string.IsNullOrWhiteSpace(question))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var pendingQuestions = allPendingQuestions.Take(5).ToList();
        var lifecycleStatus = NormalizeLifecycleStatus(providerState, allPendingQuestions.Count);
        summary["lifecycleStatus"] = lifecycleStatus;
        summary["terminal"] = IsTerminal(lifecycleStatus);
        summary["requiresCallerAction"] = RequiresCallerAction(lifecycleStatus, allPendingQuestions.Count);
        summary["recoverable"] = IsRecoverable(lifecycleStatus);
        summary["requiresSessionRefresh"] = RequiresSessionRefresh(normalizedOperation, lifecycleStatus, hasSessionId);
        summary["pendingQuestionCount"] = allPendingQuestions.Count;
        summary["pendingQuestionsTruncated"] = allPendingQuestions.Count > pendingQuestions.Count;

        if (pendingQuestions.Count > 0)
        {
            var questions = new JsonArray();
            foreach (var question in pendingQuestions)
            {
                questions.Add(question);
            }

            summary["pendingQuestions"] = questions;
        }

        var activityCount = FindFirstArrayLength(output, "activities", "items", "messages");
        if (activityCount.HasValue)
        {
            summary["activityCount"] = activityCount.Value;
        }

        var pullRequest = FindFirstObject(output, "pullRequest", "pull_request", "pr");
        var headRef = FindFirstString(pullRequest, "headRef", "head_ref", "headBranch", "headBranchName", "branchName", "sourceBranch") ??
            FindFirstString(output, "headRef", "head_ref", "headBranch", "headBranchName", "sourceBranch");
        if (!string.IsNullOrWhiteSpace(headRef))
        {
            summary["headRef"] = headRef;
        }

        var pullRequestUrl = FindFirstString(pullRequest, "htmlUrl", "html_url", "webUrl", "url") ??
            FindFirstString(output, "pullRequestUrl", "pull_request_url", "prUrl", "pr_url");
        if (!string.IsNullOrWhiteSpace(pullRequestUrl))
        {
            summary["pullRequestUrl"] = pullRequestUrl;
        }

        if ((FindFirstInt(pullRequest, "number") ?? FindFirstInt(output, "pullRequestNumber", "pull_request_number", "prNumber", "pr_number")) is { } pullRequestNumber)
        {
            summary["pullRequestNumber"] = pullRequestNumber;
        }

        summary["hasPullRequest"] = !string.IsNullOrWhiteSpace(pullRequestUrl) || summary.ContainsKey("pullRequestNumber");
        summary["decisionRequired"] = GetDecisionRequired(lifecycleStatus, allPendingQuestions.Count);
        summary["supportedLifecycleCommands"] = ToJsonArray(new[] { "sendMessage", "getSession", "probeSession", "listActivities" });
        summary["unsupportedLifecycleCommands"] = ToJsonArray(new[] { "pause", "resume", "publishDecision" });
        summary["nextActions"] = ToJsonArray(GetNextActions(
            normalizedOperation,
            lifecycleStatus,
            hasSessionId,
            allPendingQuestions.Count,
            summary["hasPullRequest"]?.GetValue<bool>() == true));
        return summary;
    }

    public static string NormalizeSessionId(string sessionId)
    {
        var parts = sessionId.Trim().Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && parts[0] == "sessions" ? parts[1] : string.Join('/', parts);
    }

    private static string NormalizeOperation(string? operation)
    {
        if (string.IsNullOrWhiteSpace(operation))
        {
            return "unknown";
        }

        var value = operation.Trim();
        if (value.Equals("start", StringComparison.OrdinalIgnoreCase))
        {
            return "createSession";
        }

        if (value.Equals("probeSession", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("refreshSession", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("sessionState", StringComparison.OrdinalIgnoreCase))
        {
            return "getSession";
        }

        return value;
    }

    private static bool IsAuthoritativeSessionState(string operation)
    {
        return operation.Equals("getSession", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetStateSource(string operation)
    {
        if (operation.Equals("getSession", StringComparison.OrdinalIgnoreCase))
        {
            return "session";
        }

        if (operation.Equals("listActivities", StringComparison.OrdinalIgnoreCase))
        {
            return "activities";
        }

        if (operation.Equals("createSession", StringComparison.OrdinalIgnoreCase) ||
            operation.Equals("sendMessage", StringComparison.OrdinalIgnoreCase))
        {
            return "operationResponse";
        }

        return "unknown";
    }

    private static bool RequiresCallerAction(string lifecycleStatus, int pendingQuestionCount)
    {
        return pendingQuestionCount > 0 ||
            lifecycleStatus is "awaitingInput" or "awaitingFeedback" or "awaitingPlanApproval" or "awaitingPublishDecision" or "failedRecoverable";
    }

    private static bool IsRecoverable(string lifecycleStatus)
    {
        return lifecycleStatus is "queued" or "running" or "awaitingInput" or "awaitingFeedback" or "awaitingPlanApproval" or "awaitingPublishDecision" or "failedRecoverable";
    }

    private static bool IsTerminal(string lifecycleStatus)
    {
        return lifecycleStatus is "completed" or "failed" or "cancelled";
    }

    private static bool RequiresSessionRefresh(string operation, string lifecycleStatus, bool hasSessionId)
    {
        if (!hasSessionId || IsAuthoritativeSessionState(operation))
        {
            return false;
        }

        return !IsTerminal(lifecycleStatus) || lifecycleStatus == "unknown";
    }

    private static string? GetDecisionRequired(string lifecycleStatus, int pendingQuestionCount)
    {
        if (pendingQuestionCount > 0 || lifecycleStatus == "awaitingInput")
        {
            return "input";
        }

        return lifecycleStatus switch
        {
            "awaitingFeedback" => "feedback",
            "awaitingPlanApproval" => "planApproval",
            "awaitingPublishDecision" => "publishDecision",
            "failedRecoverable" => "recovery",
            _ => null
        };
    }

    private static IEnumerable<string> GetNextActions(
        string operation,
        string lifecycleStatus,
        bool hasSessionId,
        int pendingQuestionCount,
        bool hasPullRequest)
    {
        var actions = new List<string>();
        if (!IsAuthoritativeSessionState(operation) && hasSessionId)
        {
            actions.Add("getSession");
        }

        switch (lifecycleStatus)
        {
            case "awaitingInput":
                actions.Add(pendingQuestionCount > 0 ? "answerPendingQuestion" : "sendMessage");
                break;
            case "awaitingFeedback":
                actions.Add("sendFeedback");
                break;
            case "awaitingPlanApproval":
                actions.Add("reviewPlanApproval");
                break;
            case "awaitingPublishDecision":
                actions.Add("reviewPublishDecision");
                break;
            case "failedRecoverable":
                actions.Add("inspectFailure");
                actions.Add("sendFeedback");
                break;
            case "queued":
            case "running":
                actions.Add("pollSession");
                break;
            case "completed":
                if (hasPullRequest)
                {
                    actions.Add("reviewPullRequest");
                }

                break;
            case "unknown":
                if (hasSessionId)
                {
                    actions.Add("getSession");
                }

                break;
        }

        return actions.Distinct(StringComparer.Ordinal);
    }

    private static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        return array;
    }

    private static string NormalizeLifecycleStatus(string? providerState, int pendingQuestionCount)
    {
        if (pendingQuestionCount > 0)
        {
            return "awaitingInput";
        }

        if (string.IsNullOrWhiteSpace(providerState))
        {
            return "unknown";
        }

        var state = providerState.ToLowerInvariant();
        if (state.Contains("question", StringComparison.Ordinal) || state.Contains("input", StringComparison.Ordinal))
        {
            return "awaitingInput";
        }

        if (state.Contains("plan", StringComparison.Ordinal) && state.Contains("approval", StringComparison.Ordinal))
        {
            return "awaitingPlanApproval";
        }

        if (state.Contains("publish", StringComparison.Ordinal) &&
            (state.Contains("decision", StringComparison.Ordinal) || state.Contains("approval", StringComparison.Ordinal) ||
             state.Contains("await", StringComparison.Ordinal) || state.Contains("waiting", StringComparison.Ordinal)))
        {
            return "awaitingPublishDecision";
        }

        if (state.Contains("feedback", StringComparison.Ordinal) || state.Contains("await", StringComparison.Ordinal) || state.Contains("waiting", StringComparison.Ordinal))
        {
            return "awaitingFeedback";
        }

        if (state.Contains("fail", StringComparison.Ordinal) || state.Contains("error", StringComparison.Ordinal))
        {
            return state.Contains("recover", StringComparison.Ordinal) || state.Contains("retry", StringComparison.Ordinal)
                ? "failedRecoverable"
                : "failed";
        }

        if (state.Contains("cancel", StringComparison.Ordinal))
        {
            return "cancelled";
        }

        if (state.Contains("complete", StringComparison.Ordinal) || state.Contains("success", StringComparison.Ordinal) ||
            state.Contains("done", StringComparison.Ordinal) || state.Contains("closed", StringComparison.Ordinal) ||
            state.Contains("merged", StringComparison.Ordinal))
        {
            return "completed";
        }

        if (state.Contains("queue", StringComparison.Ordinal) || state.Contains("pending", StringComparison.Ordinal))
        {
            return "queued";
        }

        if (state.Contains("run", StringComparison.Ordinal) || state.Contains("progress", StringComparison.Ordinal) ||
            state.Contains("work", StringComparison.Ordinal) || state.Contains("active", StringComparison.Ordinal))
        {
            return "running";
        }

        return "unknown";
    }

    private static IEnumerable<string> FindPendingQuestions(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var (key, value) in obj)
            {
                if (value is null)
                {
                    continue;
                }

                if (IsResolvedQuestionKey(key))
                {
                    continue;
                }

                if (key.Contains("question", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var question in ExtractQuestionText(value))
                    {
                        yield return question;
                    }

                    continue;
                }

                foreach (var question in FindPendingQuestions(value))
                {
                    yield return question;
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                foreach (var question in FindPendingQuestions(item))
                {
                    yield return question;
                }
            }
        }
    }

    private static IEnumerable<string> ExtractQuestionText(JsonNode node)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
        {
            yield return text;
            yield break;
        }

        if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is not null)
                {
                    foreach (var question in ExtractQuestionText(item))
                    {
                        yield return question;
                    }
                }
            }

            yield break;
        }

        if (node is JsonObject obj)
        {
            if (IsResolvedQuestionObject(obj))
            {
                yield break;
            }

            var questionText = ReadDirectString(obj, "question", "text", "title", "prompt", "content", "message");
            if (!string.IsNullOrWhiteSpace(questionText))
            {
                yield return questionText;
            }
        }
    }

    private static bool IsResolvedQuestionKey(string key)
    {
        return key.Contains("answered", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("resolved", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("closed", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("completed", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsResolvedQuestionObject(JsonObject obj)
    {
        if (ReadDirectBool(obj, "answered", "resolved", "closed") == true)
        {
            return true;
        }

        var status = ReadDirectString(obj, "status", "state", "phase");
        if (string.IsNullOrWhiteSpace(status))
        {
            return false;
        }

        var normalized = status.ToLowerInvariant();
        return normalized.Contains("answered", StringComparison.Ordinal) ||
            normalized.Contains("resolved", StringComparison.Ordinal) ||
            normalized.Contains("closed", StringComparison.Ordinal) ||
            normalized.Contains("complete", StringComparison.Ordinal) ||
            normalized.Contains("done", StringComparison.Ordinal);
    }

    private static string? FindFirstString(JsonNode? node, params string[] keys)
    {
        if (node is JsonObject obj)
        {
            var direct = ReadDirectString(obj, keys);
            if (!string.IsNullOrWhiteSpace(direct))
            {
                return direct;
            }

            foreach (var child in obj.Select(pair => pair.Value))
            {
                var found = FindFirstString(child, keys);
                if (!string.IsNullOrWhiteSpace(found))
                {
                    return found;
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array)
            {
                var found = FindFirstString(child, keys);
                if (!string.IsNullOrWhiteSpace(found))
                {
                    return found;
                }
            }
        }

        return null;
    }

    private static bool? ReadDirectBool(JsonObject obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (obj.TryGetPropertyValue(key, out var value) &&
                value is JsonValue jsonValue &&
                jsonValue.TryGetValue<bool>(out var boolean))
            {
                return boolean;
            }
        }

        return null;
    }

    private static string? ReadDirectString(JsonObject obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (obj.TryGetPropertyValue(key, out var value) &&
                value is JsonValue jsonValue &&
                jsonValue.TryGetValue<string>(out var text) &&
                !string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return null;
    }

    private static int? FindFirstInt(JsonNode? node, params string[] keys)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in keys)
            {
                if (obj.TryGetPropertyValue(key, out var value) &&
                    value is JsonValue jsonValue &&
                    jsonValue.TryGetValue<int>(out var integer))
                {
                    return integer;
                }
            }

            foreach (var child in obj.Select(pair => pair.Value))
            {
                var found = FindFirstInt(child, keys);
                if (found.HasValue)
                {
                    return found;
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array)
            {
                var found = FindFirstInt(child, keys);
                if (found.HasValue)
                {
                    return found;
                }
            }
        }

        return null;
    }

    private static JsonObject? FindFirstObject(JsonNode? node, params string[] keys)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in keys)
            {
                if (obj.TryGetPropertyValue(key, out var value) && value is JsonObject childObject)
                {
                    return childObject;
                }
            }

            foreach (var child in obj.Select(pair => pair.Value))
            {
                var found = FindFirstObject(child, keys);
                if (found is not null)
                {
                    return found;
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array)
            {
                var found = FindFirstObject(child, keys);
                if (found is not null)
                {
                    return found;
                }
            }
        }

        return null;
    }

    private static int? FindFirstArrayLength(JsonNode? node, params string[] keys)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in keys)
            {
                if (obj.TryGetPropertyValue(key, out var value) && value is JsonArray array)
                {
                    return array.Count;
                }
            }

            foreach (var child in obj.Select(pair => pair.Value))
            {
                var found = FindFirstArrayLength(child, keys);
                if (found.HasValue)
                {
                    return found;
                }
            }
        }

        return null;
    }
}
