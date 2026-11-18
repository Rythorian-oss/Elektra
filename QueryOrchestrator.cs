/*
 * Creator: Justin L. Ross | Farmington, Maine. | Remote Location: Iceland | Black Star Research Facility.
 */

using Elektra.Configuration;
using Elektra.Knowledge.Caching;
using Elektra.Knowledge.Context;
using Elektra.Knowledge.Models;
using Elektra.Knowledge.Providers;
using Elektra.Learning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Elektra.Knowledge;

public sealed class QueryOrchestrator(
    TwoTierKnowledgeCache cache,
    ConversationContextTracker contextTracker,
    IEnumerable<IKnowledgeProvider> providers,
    LocalLearningService learning,
    IOptions<AssistantOptions> options,
    ILogger<QueryOrchestrator> logger)
{
    public async Task<QueryResponse> ResolveAsync(string rawQuery, CancellationToken ct)
    {
        string trimmedRaw = rawQuery.Trim();
        string? memoryTopic = GetMemoryRecallTopic(trimmedRaw);
        if (memoryTopic != null)
        {
            var memories = await learning.SearchMemoriesAsync(memoryTopic, ct);
            string answer = memories.Count == 0
                ? $"I don't have a saved local memory about {memoryTopic}."
                : string.Join(Environment.NewLine, memories.Select(memory => $"{memory.Key}: {memory.Value}"));
            return new QueryResponse(trimmedRaw, answer, "Local memory");
        }

        bool isDirectRealTimeQuery = providers
            .OfType<IRealTimeKnowledgeProvider>()
            .Any(provider => provider.CanHandle(trimmedRaw));
        string? resolvedEntity = null;
        string canonicalQuery = isDirectRealTimeQuery
            ? trimmedRaw
            : contextTracker.ExpandQueryWithContext(trimmedRaw, out resolvedEntity);

        if (resolvedEntity != null)
            logger.LogDebug("Follow-up question resolved for entity {Entity}.", resolvedEntity);

        bool requiresFreshAnswer = providers
            .OfType<IRealTimeKnowledgeProvider>()
            .Any(provider => provider.CanHandle(canonicalQuery));
        bool allowOnlineLookups = options.Value.EnableOnlineLookups;

        if (allowOnlineLookups && !requiresFreshAnswer)
        {
            var cached = await cache.GetAsync(canonicalQuery, ct);
            if (cached != null)
            {
                var cachedDisplay = cached with { Query = canonicalQuery };
                contextTracker.RecordTurn(trimmedRaw, canonicalQuery, cachedDisplay);
                return cachedDisplay;
            }
        }

        foreach (var provider in providers.OrderByDescending(provider => learning.GetProviderScore(provider.ProviderName)))
        {
            if (!provider.CanHandle(canonicalQuery))
                continue;

            try
            {
                var result = await provider.TryResolveAsync(canonicalQuery, ct);
                if (result != null)
                {
                    if (provider is not IRealTimeKnowledgeProvider)
                    {
                        if (allowOnlineLookups)
                            await cache.SetAsync(result, ct);
                        contextTracker.RecordTurn(trimmedRaw, canonicalQuery, result);
                    }
                    return result;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not AccessViolationException)
            {
                logger.LogWarning(ex, "Provider {Provider} failed while resolving a {QueryLength}-character query.",
                    provider.ProviderName, canonicalQuery.Length);
            }
        }

        return new QueryResponse(canonicalQuery, $"I couldn't find a verified answer for \"{canonicalQuery}\".", "Fallback");
    }

    private static string? GetMemoryRecallTopic(string query)
    {
        string[] prefixes = ["what do you remember about ", "what did i tell you about ", "what did I tell you about "];
        foreach (string prefix in prefixes)
        {
            if (query.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                string topic = query[prefix.Length..].Trim().TrimEnd('?', '.', '!');
                return topic.Length is > 0 and <= 120 ? topic : null;
            }
        }
        return null;
    }
}